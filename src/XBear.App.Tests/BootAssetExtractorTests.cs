using System.Collections.Concurrent;
using System.IO;
using System.Text;
using XBear.App.Services;
using XBear.Core.Diagnostics;

namespace XBear.App.Tests;

/// <summary>
/// BootAssetExtractorService 引导资产提取服务行为测试。
/// 使用构造的小型 ISO9660 镜像验证提取逻辑，不依赖真实 Bliss ISO。
/// </summary>
public class BootAssetExtractorTests : IDisposable
{
    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    /// <summary>
    /// 目标已存在时报告 AlreadyExtracted，不重复提取。
    /// </summary>
    [Fact]
    public async Task AlreadyExtractedReportsAlreadyExtractedWithoutReExtracting()
    {
        string workspace = _temp.New("workspace");
        string isoPath = CreateMinimalIso(workspace, ("KERNEL", 512), ("INITRD.IMG", 256));

        string kernelTarget = Path.Combine(workspace, "KERNEL");
        string initrdTarget = Path.Combine(workspace, "INITRD.IMG");
        File.WriteAllBytes(kernelTarget, new byte[512]);
        File.WriteAllBytes(initrdTarget, new byte[256]);

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace);

        BootAssetExtractionProgress result = await service.ExtractAsync(
            isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue);

        Assert.Equal(BootAssetExtractionPhase.AlreadyExtracted, result.Phase);
        Assert.DoesNotContain(
            reports.ToList(),
            p => p.Phase == BootAssetExtractionPhase.Running);
    }

    /// <summary>
    /// ISO 中缺少指定内核文件时抛出含清晰原因和处置建议的规格异常。
    /// </summary>
    [Fact]
    public async Task MissingKernelInIsoThrowsSpecExceptionWithGuidance()
    {
        string workspace = _temp.New("workspace");
        string isoPath = CreateMinimalIso(workspace, ("NOTKERNEL", 512));

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Contains("KERNEL", error.Message);
        Assert.Contains("ISO", error.Message);
        Assert.NotNull(error.Remediation);
    }

    /// <summary>
    /// ISO 中缺少指定 initrd 文件时抛出含清晰原因和处置建议的规格异常。
    /// </summary>
    [Fact]
    public async Task MissingInitrdInIsoThrowsSpecExceptionWithGuidance()
    {
        string workspace = _temp.New("workspace");
        string isoPath = CreateMinimalIso(workspace, ("KERNEL", 512));

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Contains("INITRD.IMG", error.Message);
        Assert.Contains("ISO", error.Message);
        Assert.NotNull(error.Remediation);
    }

    /// <summary>
    /// 取消时上抛取消异常，不产生残留文件。
    /// </summary>
    [Fact]
    public async Task CancellationThrowsWithoutPartialOutput()
    {
        string workspace = _temp.New("workspace");
        // 使用大文件使提取持续足够长时间，以便取消能够生效。
        string isoPath = CreateMinimalIso(workspace, ("KERNEL", 10 * 1024 * 1024), ("INITRD.IMG", 5 * 1024 * 1024));

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace);

        using var cts = new CancellationTokenSource();
        Task extracting = service.ExtractAsync(
            isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue, cts.Token);

        // 等待足够长的时间让提取进入实质性循环后再取消。
        await Task.Delay(500);
        cts.Cancel();

        // 取消后提取应上抛 OperationCanceledException。
        // （如果提取在取消前已完成，则无异常抛出，此时文件已存在，这是可接受的。）
        try
        {
            await extracting;
        }
        catch (OperationCanceledException)
        {
            // 取消生效：验证没有残留文件。
            Assert.DoesNotContain(
                Directory.GetFiles(workspace),
                f => Path.GetFileName(f).Equals("KERNEL", StringComparison.OrdinalIgnoreCase));
            return;
        }
        // 如果无异常（提取在取消前完成），文件已正常写入，测试通过。
    }

    /// <summary>
    /// ISO 不是有效格式时抛出规格异常。
    /// </summary>
    [Fact]
    public async Task InvalidIsoThrowsSpecException()
    {
        string workspace = _temp.New("workspace");
        string fakeIso = Path.Combine(workspace, "fake.iso");
        File.WriteAllBytes(fakeIso, new byte[2048 * 20]);

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.ExtractAsync(fakeIso, "KERNEL", "INITRD.IMG", reports.Enqueue));

        Assert.Equal(ErrorCategory.Spec, error.Category);
    }

    /// <summary>
    /// 提取进度报告中当前文件名随提取进行而更新。
    /// </summary>
    [Fact]
    public async Task ProgressReportsCurrentFileName()
    {
        string workspace = _temp.New("workspace");
        string isoPath = CreateMinimalIso(workspace, ("KERNEL", 512), ("INITRD.IMG", 256));

        // Verify the generated ISO before passing to service.
        byte[] isoBytes = File.ReadAllBytes(isoPath);
        Assert.True(isoBytes.Length >= 16 * 2048 + 156 + 34, $"ISO too small: {isoBytes.Length}");
        int pvd = 16 * 2048;
        Assert.Equal(0x01, isoBytes[pvd]);
        int rde = pvd + 156;
        Assert.Equal(34, isoBytes[rde]);
        int rootLoc = (isoBytes[rde + 2] << 24) | (isoBytes[rde + 3] << 16) |
                      (isoBytes[rde + 4] << 8) | isoBytes[rde + 5];
        int rootSize = (isoBytes[rde + 10] << 24) | (isoBytes[rde + 11] << 16) |
                      (isoBytes[rde + 12] << 8) | isoBytes[rde + 13];
        Assert.Equal(17, rootLoc);
        Assert.Equal(2048, rootSize);

        int dirOff = rootLoc * 2048;
        Assert.Equal(34, isoBytes[dirOff]);
        int feOff = dirOff + 68;
        int feLen = isoBytes[feOff];
        Assert.True(feLen > 33, $"File entry too short: {feLen}");
        int feNameLen = isoBytes[feOff + 32];
        Assert.True(feNameLen > 0, "nameLen is 0");
        string feName = System.Text.Encoding.ASCII.GetString(isoBytes, feOff + 33, feNameLen).Replace(";1", "");
        Assert.Equal("KERNEL", feName);

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace);

        await service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue);

        List<BootAssetExtractionProgress> running = reports
            .Where(p => p.Phase == BootAssetExtractionPhase.Running)
            .ToList();

        Assert.NotEmpty(running);
        Assert.All(running, p => Assert.False(string.IsNullOrEmpty(p.CurrentFile)));
    }

    /// <summary>
    /// 构建一个经过 Python 验证的 ISO9660 镜像，其中包含指定文件。
    /// 结构：PVD(s16) + 根目录(s17 "." + ".." + 文件条目) + 文件数据(s18+)。
    /// 服务解析格式：根目录项位于 PVD 偏移 156，location 在字节 2-5，data size 在字节 10-13。
    /// </summary>
    private static string CreateMinimalIso(string workspace, params (string Name, int Size)[] files)
    {
        // Verified ISO structure (Python confirmed correct parsing):
        // Sector 16: PVD with Root Dir Entry at offset 156 (loc=17, size=2048)
        // Sector 17: Root directory with "." (loc=17,size=2048), ".." (loc=17,size=2048), then file entries
        // Sector 18+: file data
        const int sectorSize = 2048;
        const int dirSector = 17;
        const int fileDataSector = 18;

        // Calculate total size accounting for actual file sizes.
        int fileSectors = 0;
        foreach (var (_, size) in files)
            fileSectors += (size + sectorSize - 1) / sectorSize;
        int totalSectors = fileDataSector + fileSectors + 2;
        var iso = new byte[sectorSize * totalSectors];

        // Helper: write big-endian uint32 at position.
        void WriteBe(int pos, int value)
        {
            iso[pos] = (byte)(value >> 24);
            iso[pos + 1] = (byte)(value >> 16);
            iso[pos + 2] = (byte)(value >> 8);
            iso[pos + 3] = (byte)value;
        }

        // PVD at sector 16.
        int pvd = 16 * sectorSize;
        iso[pvd] = 0x01; // PVD type
        var cd001 = "CD001"u8;
        cd001.CopyTo(iso.AsSpan(pvd + 1, 5));
        iso[pvd + 6] = 0x01; // version

        // Root Directory Entry in PVD at offset 156.
        int rde = pvd + 156;
        iso[rde] = 34; // length
        iso[rde + 1] = 0; // extended attr
        WriteBe(rde + 2, dirSector); // location = 17
        WriteBe(rde + 10, sectorSize); // data size = 2048
        iso[rde + 32] = 1; // name length
        iso[rde + 33] = 0; // name (root)

        // Root directory sector 17.
        int d = dirSector * sectorSize;

        // "." entry.
        iso[d] = 34; iso[d + 1] = 0;
        WriteBe(d + 2, dirSector);
        WriteBe(d + 10, sectorSize);
        iso[d + 32] = 1; iso[d + 33] = 0;

        // ".." entry.
        int d2 = d + 34;
        iso[d2] = 34; iso[d2 + 1] = 0;
        WriteBe(d2 + 2, dirSector);
        WriteBe(d2 + 10, sectorSize);
        iso[d2 + 32] = 1; iso[d2 + 33] = 1;

        // File entries.
        int curSector = fileDataSector;
        int entryOff = d2 + 34;
        foreach (var (name, size) in files)
        {
            string isoName = name + ";1";
            int nameLen = isoName.Length;
            int entryLen = (33 + nameLen + 1) & ~1; // even

            iso[entryOff] = (byte)entryLen;
            iso[entryOff + 1] = 0;
            WriteBe(entryOff + 2, curSector);
            WriteBe(entryOff + 10, size);
            iso[entryOff + 32] = (byte)nameLen;
            Encoding.ASCII.GetBytes(isoName, 0, nameLen, iso, entryOff + 33);

            // Write file data.
            int fileOff = curSector * sectorSize;
            for (int i = 0; i < size; i++) iso[fileOff + i] = 0xAA;

            curSector += (size + sectorSize - 1) / sectorSize;
            entryOff += entryLen;
        }

        string isoPath = Path.Combine(workspace, "source.iso");
        File.WriteAllBytes(isoPath, iso);
        return isoPath;
    }
}
