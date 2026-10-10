using System.Collections.Concurrent;
using System.IO;
using System.Text;
using XBear.App.Services;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 导入链的集成测试：BootAssetExtractorService 提取引导资产后，
/// 定制服务是否在同一条链路上把 initrd 加工为带调试通路的版本。
/// </summary>
public class BootAssetInitrdIntegrationTests : IDisposable
{
    /// <summary>测试用的宿主 ADB 公钥内容。</summary>
    private const string HostPublicKey =
        "QAAAintegrationTestKeyMaterialAAAAAAAAAAAAAAAAAAAA== user@host";

    private readonly TempRoot _temp = new();

    /// <inheritdoc />
    public void Dispose() => _temp.Cleanup();

    /// <summary>
    /// 从 ISO 提取出的 initrd 会被就地定制，文件名保持与 boot 推荐一致，
    /// 因此实例侧配置无需任何改动。
    /// </summary>
    [Fact]
    public async Task ExtractedInitrdIsCustomizedInPlaceUnderOriginalName()
    {
        string workspace = _temp.New("workspace");
        byte[] stockInitrd = BuildMinimalInitrd();
        string isoPath = CreateMinimalIso(
            workspace,
            ("KERNEL", new byte[512]),
            ("INITRD.IMG", stockInitrd));

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace, new InitrdCustomizerService(WriteHostKey(workspace)));

        await service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue);

        // boot 推荐引用的文件名不变，文件确实存在。
        string initrdTarget = Path.Combine(workspace, "INITRD.IMG");
        Assert.True(File.Exists(initrdTarget));
        Assert.True(File.Exists(Path.Combine(workspace, "KERNEL")));

        // 内容已是定制版本。
        string init = ReadEntryText(initrdTarget, InitrdCustomizerService.InitEntryName);
        Assert.Contains("service.adb.tcp.port=5555", init);
        Assert.Contains(HostPublicKey, init);

        string autoDetect = ReadEntryText(initrdTarget, InitrdCustomizerService.AutoDetectEntryName);
        Assert.DoesNotContain("dev2mod", autoDetect);
    }

    /// <summary>
    /// 原版 initrd 留档与 ISO 中的内容一致，可用于溯源与重生成。
    /// </summary>
    [Fact]
    public async Task StockCopyMatchesTheInitrdInsideTheIso()
    {
        string workspace = _temp.New("workspace");
        byte[] stockInitrd = BuildMinimalInitrd();
        string isoPath = CreateMinimalIso(
            workspace,
            ("KERNEL", new byte[512]),
            ("INITRD.IMG", stockInitrd));

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace, new InitrdCustomizerService(WriteHostKey(workspace)));

        await service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue);

        Assert.Equal(
            stockInitrd,
            File.ReadAllBytes(Path.Combine(workspace, "INITRD.IMG" + InitrdCustomizerService.StockSuffix)));
    }

    /// <summary>
    /// 重复导入不重复提取，也不重复定制：产物字节保持不变。
    /// </summary>
    [Fact]
    public async Task RepeatedImportKeepsCustomizedInitrdUnchanged()
    {
        string workspace = _temp.New("workspace");
        byte[] stockInitrd = BuildMinimalInitrd();
        string isoPath = CreateMinimalIso(
            workspace,
            ("KERNEL", new byte[512]),
            ("INITRD.IMG", stockInitrd));

        var firstPassReports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace, new InitrdCustomizerService(WriteHostKey(workspace)));

        await service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", firstPassReports.Enqueue);
        byte[] customized = File.ReadAllBytes(Path.Combine(workspace, "INITRD.IMG"));

        var secondPassReports = new ConcurrentQueue<BootAssetExtractionProgress>();
        BootAssetExtractionProgress result =
            await service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", secondPassReports.Enqueue);

        Assert.Equal(BootAssetExtractionPhase.AlreadyExtracted, result.Phase);
        Assert.Equal(customized, File.ReadAllBytes(Path.Combine(workspace, "INITRD.IMG")));

        // 原版留档不被二次导入覆盖。
        Assert.Equal(
            stockInitrd,
            File.ReadAllBytes(Path.Combine(workspace, "INITRD.IMG" + InitrdCustomizerService.StockSuffix)));
    }

    /// <summary>
    /// 上一轮导入未定制、本轮已装配定制服务时，重复导入补齐定制。
    /// 这是升级既有镜像目录时必须成立的行为。
    /// </summary>
    [Fact]
    public async Task ImportCustomizesAlreadyExtractedButUncustomizedInitrd()
    {
        string workspace = _temp.New("workspace");
        byte[] stockInitrd = BuildMinimalInitrd();
        string isoPath = CreateMinimalIso(
            workspace,
            ("KERNEL", new byte[512]),
            ("INITRD.IMG", stockInitrd));

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();

        // 第一轮不装配定制服务，只提取原版。
        await new BootAssetExtractorService(workspace).ExtractAsync(
            isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue);
        Assert.Equal(stockInitrd, File.ReadAllBytes(Path.Combine(workspace, "INITRD.IMG")));

        // 第二轮装配定制服务，重复导入应补齐定制。
        var service = new BootAssetExtractorService(workspace, new InitrdCustomizerService(WriteHostKey(workspace)));
        await service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue);

        Assert.Contains(
            "service.adb.tcp.port=5555",
            ReadEntryText(Path.Combine(workspace, "INITRD.IMG"), InitrdCustomizerService.InitEntryName));
        Assert.Equal(
            stockInitrd,
            File.ReadAllBytes(Path.Combine(workspace, "INITRD.IMG" + InitrdCustomizerService.StockSuffix)));
    }

    /// <summary>
    /// 未装配定制服务时保持既有行为：原样提取，不产生留档与指纹。
    /// </summary>
    [Fact]
    public async Task WithoutCustomizerTheInitrdIsExtractedVerbatim()
    {
        string workspace = _temp.New("workspace");
        byte[] stockInitrd = BuildMinimalInitrd();
        string isoPath = CreateMinimalIso(
            workspace,
            ("KERNEL", new byte[512]),
            ("INITRD.IMG", stockInitrd));

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace);

        await service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue);

        Assert.Equal(stockInitrd, File.ReadAllBytes(Path.Combine(workspace, "INITRD.IMG")));
        Assert.False(File.Exists(Path.Combine(workspace, "INITRD.IMG" + InitrdCustomizerService.StockSuffix)));
    }

    /// <summary>
    /// 宿主公钥缺失时导入中止，原版 initrd 保持可读，不留下半成品。
    /// </summary>
    [Fact]
    public async Task MissingHostKeyAbortsImportWithGuidance()
    {
        string workspace = _temp.New("workspace");
        byte[] stockInitrd = BuildMinimalInitrd();
        string isoPath = CreateMinimalIso(
            workspace,
            ("KERNEL", new byte[512]),
            ("INITRD.IMG", stockInitrd));

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(
            workspace,
            new InitrdCustomizerService(Path.Combine(workspace, "absent", "adbkey.pub")));

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Contains("adbkey.pub", error.Message);
        Assert.Contains("adb keygen", error.Remediation!);

        // 提取已经完成，但定制没有留下任何产物。
        Assert.False(File.Exists(Path.Combine(workspace, "INITRD.IMG" + InitrdCustomizerService.StockSuffix)));
        Assert.Equal(stockInitrd, File.ReadAllBytes(Path.Combine(workspace, "INITRD.IMG")));
    }

    /// <summary>
    /// 定制期间的进度上报带上 initrd 文件名，界面能显示当前处理的文件。
    /// </summary>
    [Fact]
    public async Task CustomizationReportsInitrdFileNameInProgress()
    {
        string workspace = _temp.New("workspace");
        string isoPath = CreateMinimalIso(
            workspace,
            ("KERNEL", new byte[512]),
            ("INITRD.IMG", BuildMinimalInitrd()));

        var reports = new ConcurrentQueue<BootAssetExtractionProgress>();
        var service = new BootAssetExtractorService(workspace, new InitrdCustomizerService(WriteHostKey(workspace)));

        await service.ExtractAsync(isoPath, "KERNEL", "INITRD.IMG", reports.Enqueue);

        List<BootAssetExtractionProgress> running = reports
            .Where(p => p.Phase == BootAssetExtractionPhase.Running)
            .ToList();

        Assert.NotEmpty(running);
        Assert.Contains(running, p => p.CurrentFile == "INITRD.IMG");
    }

    /// <summary>写出宿主 ADB 公钥文件。</summary>
    /// <param name="workspace">工作目录。</param>
    /// <returns>公钥文件路径。</returns>
    private string WriteHostKey(string workspace)
    {
        string path = Path.Combine(workspace, "adbkey.pub");
        File.WriteAllText(path, HostPublicKey + Environment.NewLine);
        return path;
    }

    /// <summary>读出 initrd 中指定条目的文本。</summary>
    /// <param name="initrdPath">initrd 文件路径。</param>
    /// <param name="entryName">条目名。</param>
    /// <returns>条目文本。</returns>
    internal static string ReadEntryText(string initrdPath, string entryName)
    {
        byte[] image = File.ReadAllBytes(initrdPath);
        InitrdCompression compression = InitrdImageCodec.Detect(image);
        IReadOnlyList<CpioNewcEntry> entries = CpioNewcArchive.Parse(
            InitrdImageCodec.Decompress(image, compression));

        return Encoding.UTF8.GetString(CpioNewcArchive.Find(entries, entryName)!.Content);
    }

    /// <summary>构造一个带真实锚点结构的最小 gzip initrd。</summary>
    /// <returns>initrd 字节内容。</returns>
    internal static byte[] BuildMinimalInitrd()
    {
        var entries = new List<CpioNewcEntry>
        {
            new CpioNewcEntry
            {
                Name = InitrdCustomizerService.InitEntryName,
                Ino = 2,
                Mode = 0x81ed,
                Nlink = 1,
                Mtime = 1700000001,
                Content = Encoding.UTF8.GetBytes(
                    "#!/bin/busybox sh\nmount_data\nmount_sdcard\nexec ${SWITCH:-switch_root} /android /init\n")
            },
            new CpioNewcEntry
            {
                Name = InitrdCustomizerService.AutoDetectEntryName,
                Ino = 3,
                Mode = 0x81ed,
                Nlink = 1,
                Mtime = 1700000002,
                Content = Encoding.UTF8.GetBytes(
                    "auto_detect()\n{\n\tbusybox modprobe x\n}\n\n" +
                    "auto_detect_alpine()\n{\n\tbusybox modprobe -b y\n}\n\n" +
                    "load_modules()\n{\n\t:\n}\n")
            },
            new CpioNewcEntry
            {
                Name = CpioNewcArchive.TrailerName,
                Ino = 4,
                Mode = 0,
                Nlink = 1,
                Mtime = 0,
                Content = Array.Empty<byte>()
            }
        };

        return InitrdImageCodec.Compress(CpioNewcArchive.Build(entries), InitrdCompression.GZip);
    }

    /// <summary>
    /// 构建一个经过校验的最小 ISO9660 镜像。
    /// 结构：PVD(s16) + 根目录(s17 "." + ".." + 文件条目) + 文件数据(s18+)。
    /// 服务解析格式：根目录项位于 PVD 偏移 156，location 在字节 2-5，data size 在字节 10-13。
    /// </summary>
    /// <param name="workspace">工作目录。</param>
    /// <param name="files">文件名与内容。</param>
    /// <returns>ISO 文件路径。</returns>
    internal static string CreateMinimalIso(string workspace, params (string Name, byte[] Content)[] files)
    {
        const int sectorSize = 2048;
        const int dirSector = 17;
        const int fileDataSector = 18;

        int fileSectors = 0;
        foreach (var (_, content) in files)
        {
            fileSectors += (content.Length + sectorSize - 1) / sectorSize;
        }

        int totalSectors = fileDataSector + fileSectors + 2;
        var iso = new byte[sectorSize * totalSectors];

        void WriteBe(int pos, int value)
        {
            iso[pos] = (byte)(value >> 24);
            iso[pos + 1] = (byte)(value >> 16);
            iso[pos + 2] = (byte)(value >> 8);
            iso[pos + 3] = (byte)value;
        }

        // PVD at sector 16.
        int pvd = 16 * sectorSize;
        iso[pvd] = 0x01;
        "CD001"u8.CopyTo(iso.AsSpan(pvd + 1, 5));
        iso[pvd + 6] = 0x01;

        // Root Directory Entry in PVD at offset 156.
        int rde = pvd + 156;
        iso[rde] = 34;
        iso[rde + 1] = 0;
        WriteBe(rde + 2, dirSector);
        WriteBe(rde + 10, sectorSize);
        iso[rde + 32] = 1;
        iso[rde + 33] = 0;

        // Root directory sector 17.
        int d = dirSector * sectorSize;

        // "." entry.
        iso[d] = 34;
        iso[d + 1] = 0;
        WriteBe(d + 2, dirSector);
        WriteBe(d + 10, sectorSize);
        iso[d + 32] = 1;
        iso[d + 33] = 0;

        // ".." entry.
        int d2 = d + 34;
        iso[d2] = 34;
        iso[d2 + 1] = 0;
        WriteBe(d2 + 2, dirSector);
        WriteBe(d2 + 10, sectorSize);
        iso[d2 + 32] = 1;
        iso[d2 + 33] = 1;

        // File entries.
        int curSector = fileDataSector;
        int entryOff = d2 + 34;
        foreach (var (name, content) in files)
        {
            string isoName = name + ";1";
            int nameLen = isoName.Length;
            int entryLen = (33 + nameLen + 1) & ~1;

            iso[entryOff] = (byte)entryLen;
            iso[entryOff + 1] = 0;
            WriteBe(entryOff + 2, curSector);
            WriteBe(entryOff + 10, content.Length);
            iso[entryOff + 32] = (byte)nameLen;
            Encoding.ASCII.GetBytes(isoName, 0, nameLen, iso, entryOff + 33);

            int fileOff = curSector * sectorSize;
            Array.Copy(content, 0, iso, fileOff, content.Length);

            curSector += (content.Length + sectorSize - 1) / sectorSize;
            entryOff += entryLen;
        }

        string isoPath = Path.Combine(workspace, "source.iso");
        File.WriteAllBytes(isoPath, iso);
        return isoPath;
    }
}

/// <summary>
/// 导入链生产装配测试：验证 AppComposition 装配与 MainViewModel 导入命令接线。
/// </summary>
public class BootAssetImportWiringTests : IDisposable
{
    private const string HostPublicKey =
        "QAAAintegrationWiringTestKeyMaterialAAAAAAAAAAAAAA== user@host";

    private readonly TempRoot _temp = new();

    public void Dispose() => _temp.Cleanup();

    /// <summary>
    /// AppComposition 正确装配 BootAssetExtractorService、InitrdCustomizerService 与 BaseImageImportService。
    /// </summary>
    [Fact]
    public void AppCompositionAssemblesBootAssetServices()
    {
        string dataRoot = _temp.New("data");
        string imagesRoot = _temp.New("images");
        SpecLoader loader = XBeeSpec.TestSpec();

        AppServices services = AppComposition.Create(dataRoot, imagesRoot, loader);

        Assert.NotNull(services.BootAssetExtractor);
        Assert.NotNull(services.InitrdCustomizer);
        Assert.NotNull(services.Importer);
        Assert.Equal(imagesRoot, services.BootAssetExtractor.ImagesRoot);
        Assert.Equal(imagesRoot, services.Importer.ImagesRoot);
        Assert.Same(services.InitrdCustomizer, services.BootAssetExtractor.InitrdCustomizer);
    }

    /// <summary>
    /// 完整导入链：MainViewModel.ImportBaseImageAsync 导入 ISO 时，
    /// 转换 base 镜像并自动提取引导资产，同时完成 initrd 定制。
    /// </summary>
    [Fact]
    public async Task ImportIsoRunsBaseConversionAndBootAssetExtractionAndCustomization()
    {
        string images = _temp.New("images");
        string keyPath = WriteHostKey(images);

        byte[] stockInitrd = BootAssetInitrdIntegrationTests.BuildMinimalInitrd();
        string isoPath = BootAssetInitrdIntegrationTests.CreateMinimalIso(
            images,
            ("KERNEL", new byte[512]),
            ("INITRD.IMG", stockInitrd));

        var importer = new BaseImageImportService(images, _ => new ScriptedQcow2Manager((src, baseImg, ct) =>
        {
            File.WriteAllBytes(baseImg, new byte[1024]);
            return Task.FromResult(true);
        }));
        var customizer = new InitrdCustomizerService(keyPath);
        var extractor = new BootAssetExtractorService(images, customizer);

        SpecLoader loader = XBeeSpec.TestSpec();
        var viewModel = new ViewModels.MainViewModel(
            new StubInstanceRepository(),
            null,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            new DiagnosticsExporter(loader),
            new Presentation.TerminologyCatalog(loader.LoadTerminology()),
            null,
            importer,
            extractor);

        await viewModel.ImportBaseImageAsync(isoPath);

        Assert.False(viewModel.HasError);
        Assert.Equal(100, viewModel.ImportPercent);
        Assert.Equal(BaseImageImportService.ImportedText, viewModel.ImportStageText);

        // 验证 kernel 与定制后的 initrd.img 均已在目标目录与镜像根目录就绪
        string stem = Path.GetFileNameWithoutExtension(isoPath);
        string stemDir = Path.Combine(images, stem);
        Assert.True(File.Exists(Path.Combine(stemDir, "KERNEL")));
        Assert.True(File.Exists(Path.Combine(stemDir, "INITRD.IMG")));
        Assert.True(File.Exists(Path.Combine(images, "KERNEL")));
        Assert.True(File.Exists(Path.Combine(images, "INITRD.IMG")));

        // 验证 initrd 已经过定制且包含 root 属性
        string initText = BootAssetInitrdIntegrationTests.ReadEntryText(
            Path.Combine(stemDir, "INITRD.IMG"),
            InitrdCustomizerService.InitEntryName);
        Assert.Contains("service.adb.root=1", initText);
        Assert.Contains("service.adb.tcp.port=5555", initText);
    }

    /// <summary>
    /// 公钥缺失时导入在 UI 层呈现可读错误（规范分类、原因、处置建议），不崩溃不静默。
    /// </summary>
    [Fact]
    public async Task MissingPublicKeyPresentsReadableErrorOnUiWithoutCrashing()
    {
        string images = _temp.New("images");
        string missingKeyPath = Path.Combine(images, "absent", "adbkey.pub");

        byte[] stockInitrd = BootAssetInitrdIntegrationTests.BuildMinimalInitrd();
        string isoPath = BootAssetInitrdIntegrationTests.CreateMinimalIso(
            images,
            ("KERNEL", new byte[512]),
            ("INITRD.IMG", stockInitrd));

        var importer = new BaseImageImportService(images, _ => new ScriptedQcow2Manager((src, baseImg, ct) =>
        {
            File.WriteAllBytes(baseImg, new byte[1024]);
            return Task.FromResult(true);
        }));
        var customizer = new InitrdCustomizerService(missingKeyPath);
        var extractor = new BootAssetExtractorService(images, customizer);

        SpecLoader loader = XBeeSpec.TestSpec();
        var terms = new Presentation.TerminologyCatalog(loader.LoadTerminology());
        var viewModel = new ViewModels.MainViewModel(
            new StubInstanceRepository(),
            null,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            new DiagnosticsExporter(loader),
            terms,
            null,
            importer,
            extractor);

        await viewModel.ImportBaseImageAsync(isoPath);

        // 不崩且呈现可读错误
        Assert.False(viewModel.IsImporting);
        Assert.True(viewModel.HasError);
        Assert.Equal(Presentation.ErrorPresenter.Describe(ErrorCategory.Spec, terms).Title, viewModel.ErrorTitle);
        Assert.Contains(viewModel.ImportFailedText, viewModel.ImportStageText);
        Assert.Contains("adbkey.pub", viewModel.ImportFailureText);
        Assert.Contains("adb keygen", viewModel.ImportFailureText);
    }

    private string WriteHostKey(string workspace)
    {
        string path = Path.Combine(workspace, "adbkey.pub");
        File.WriteAllText(path, HostPublicKey + Environment.NewLine);
        return path;
    }
}