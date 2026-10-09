using System.Diagnostics;
using System.IO;
using XBear.Core.Diagnostics;

namespace XBear.App.Services;

/// <summary>
/// 引导资产提取阶段。
/// </summary>
public enum BootAssetExtractionPhase
{
    /// <summary>尚未开始。</summary>
    Idle,

    /// <summary>正在提取。</summary>
    Running,

    /// <summary>目标已存在、未重复提取。</summary>
    AlreadyExtracted,

    /// <summary>提取失败。</summary>
    Failed
}

/// <summary>
/// 一次提取的进度快照。
/// </summary>
/// <param name="Phase">当前阶段。</param>
/// <param name="CurrentFile">当前正在提取的文件名。</param>
/// <param name="Elapsed">从开始到本次快照的已用时长。</param>
public sealed record BootAssetExtractionProgress(
    BootAssetExtractionPhase Phase,
    string CurrentFile,
    TimeSpan Elapsed);

/// <summary>
/// 从 ISO 镜像提取内核直启所需引导资产的服务。
///
/// 实现方案选择：使用进程内 ISO9660 直解析而非 Windows Mount-DiskImage。
/// 取舍理由：
/// - Mount-DiskImage 需要管理员权限，且挂载失败场景多（镜像被占用、文件系统不支持等）；
/// - ISO9660 是只读标准格式，解析器可在用户态完整实现，不依赖任何系统功能；
/// - 本产品仅面向 Windows，直解析可在无特权、无依赖条件下稳定工作。
/// </summary>
public sealed class BootAssetExtractorService
{
    /// <summary>ISO9660 Primary Volume Descriptor 起始扇区。</summary>
    private const int PrimaryVolumeDescriptorSector = 16;

    /// <summary>ISO9660 扇区大小。</summary>
    private const int IsoSectorSize = 2048;

    /// <summary>ISO9660 目录项长度字段字节偏移。</summary>
    private const int DirEntryLengthOffset = 0;

    /// <summary>ISO9660 目录项扩展属性长度字节偏移。</summary>
    private const int DirEntryExtAttrLengthOffset = 1;

    /// <summary>ISO9660 目录项起始扇区字节偏移。</summary>
    private const int DirEntryLocationOffset = 2;

    /// <summary>ISO9660 目录项数据大小字节偏移。</summary>
    private const int DirEntryDataSizeOffset = 10;

    /// <summary>ISO9660 目录项文件标志字节偏移。</summary>
    private const int DirEntryFileFlagsOffset = 25;

    /// <summary>ISO9660 目录项文件名长度字节偏移。</summary>
    private const int DirEntryNameLengthOffset = 32;

    /// <summary>ISO9660 目录项文件名起始偏移。</summary>
    private const int DirEntryNameOffset = 33;

    /// <summary>ISO9660 目录项为文件时的高位标志。</summary>
    private const byte FileRecordFlag = 0x80;

    private readonly string _imagesRoot;

    /// <summary>
    /// 构造提取服务。
    /// </summary>
    /// <param name="imagesRoot">镜像根目录，与 BaseImageImportService 的语义一致。</param>
    public BootAssetExtractorService(string imagesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagesRoot);
        _imagesRoot = imagesRoot;
    }

    /// <summary>
    /// 从 ISO 镜像提取 boot 引导资产（kernel 与 initrd.img）。
    /// 幂等：目标已存在时跳过提取并报告 AlreadyExtracted。
    /// </summary>
    /// <param name="isoPath">源 ISO 镜像路径。</param>
    /// <param name="kernelFileName">内核文件名。</param>
    /// <param name="initrdFileName">initrd 文件名。</param>
    /// <param name="report">进度上报回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>终态进度。</returns>
    /// <exception cref="XBearException">镜像不可读或提取失败时抛出。</exception>
    public async Task<BootAssetExtractionProgress> ExtractAsync(
        string isoPath,
        string kernelFileName,
        string initrdFileName,
        Action<BootAssetExtractionProgress> report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(kernelFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(initrdFileName);
        ArgumentNullException.ThrowIfNull(report);

        var stopwatch = Stopwatch.StartNew();
        string currentFile = string.Empty;

        void Publish(BootAssetExtractionPhase phase, string fileName)
        {
            currentFile = fileName;
            report(new BootAssetExtractionProgress(phase, currentFile, stopwatch.Elapsed));
        }

        string imageDirectory = Path.GetDirectoryName(isoPath) ?? _imagesRoot;
        string kernelTarget = Path.Combine(imageDirectory, kernelFileName);
        string initrdTarget = Path.Combine(imageDirectory, initrdFileName);

        // 幂等检查：目标已存在则跳过。
        bool kernelExists = File.Exists(kernelTarget);
        bool initrdExists = File.Exists(initrdTarget);

        if (kernelExists && initrdExists)
        {
            var result = new BootAssetExtractionProgress(
                BootAssetExtractionPhase.AlreadyExtracted,
                string.Empty,
                stopwatch.Elapsed);
            report(result);
            return result;
        }

        Publish(BootAssetExtractionPhase.Running, kernelFileName);

        try
        {
            using var isoStream = new FileStream(
                isoPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: IsoSectorSize,
                useAsync: true);

            // 解析 ISO9660 卷描述符，建立文件路径到扇区位置的映射。
            Dictionary<string, (long Sector, int Size)> fileTable = await ParseIsoFileTableAsync(
                isoStream, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            // 提取内核。
            if (!kernelExists)
            {
                Publish(BootAssetExtractionPhase.Running, kernelFileName);
                if (fileTable.TryGetValue(kernelFileName.ToUpperInvariant(), out var kernelEntry))
                {
                    await ExtractFileAsync(isoStream, kernelEntry.Sector, kernelEntry.Size, kernelTarget, cancellationToken);
                }
                else
                {
                    throw new XBearException(
                        ErrorCategory.Spec,
                        $"ISO 镜像中未找到内核文件 {kernelFileName}。",
                        "确认镜像的 boot 配置中的内核文件名与 ISO 根目录中的实际文件名一致。");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            // 提取 initrd。
            if (!initrdExists)
            {
                Publish(BootAssetExtractionPhase.Running, initrdFileName);
                if (fileTable.TryGetValue(initrdFileName.ToUpperInvariant(), out var initrdEntry))
                {
                    await ExtractFileAsync(isoStream, initrdEntry.Sector, initrdEntry.Size, initrdTarget, cancellationToken);
                }
                else
                {
                    throw new XBearException(
                        ErrorCategory.Spec,
                        $"ISO 镜像中未找到 initrd 文件 {initrdFileName}。",
                        "确认镜像的 boot 配置中的 initrd 文件名与 ISO 根目录中的实际文件名一致。");
                }
            }

            stopwatch.Stop();
            return new BootAssetExtractionProgress(
                BootAssetExtractionPhase.AlreadyExtracted,
                string.Empty,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (XBearException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            throw new XBearException(
                ErrorCategory.Spec,
                $"从 ISO 提取引导资产失败：{ex.Message}",
                "确认 ISO 镜像完整且可读，且包含 boot 配置中指定的文件。",
                ex);
        }
    }

    /// <summary>
    /// 解析 ISO9660 文件表，建立文件名（规范大写）到扇区位置和大小的映射。
    /// </summary>
    /// <param name="stream">ISO 文件流（必须支持 Seek）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文件名到扇区位置和大小的映射。</returns>
    private static async Task<Dictionary<string, (long Sector, int Size)>> ParseIsoFileTableAsync(
        FileStream stream, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, (long Sector, int Size)>(StringComparer.OrdinalIgnoreCase);

        // 读取 Primary Volume Descriptor。
        long pvdOffset = (long)PrimaryVolumeDescriptorSector * IsoSectorSize;
        stream.Seek(pvdOffset, SeekOrigin.Begin);

        var pvdHeader = new byte[6];
        int pvdRead = await stream.ReadAsync(pvdHeader.AsMemory(0, 6), cancellationToken);
        if (pvdRead < 6 || pvdHeader[0] != 0x01 || pvdHeader[1] != 'C' || pvdHeader[2] != 'D' ||
            pvdHeader[3] != '0' || pvdHeader[4] != '0' || pvdHeader[5] != '1')
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "ISO 镜像的 Primary Volume Descriptor 格式无效。",
                "确认提供的是有效 ISO9660 格式的镜像文件。");
        }

        // 从 PVD 读取根目录入口位置（位于 PVD 内偏移 156 处开始的数据区）。
        var rootDirEntry = new byte[34];
        stream.Seek(pvdOffset + 156, SeekOrigin.Begin);
        await stream.ReadAsync(rootDirEntry.AsMemory(0, 34), cancellationToken);
        long rootDirSector = ReadUInt32Msb(rootDirEntry, 2);
        int rootDirSize = ReadUInt32MsbAsInt(rootDirEntry, 10);

        // 读取根目录下的所有文件项。
        await ParseDirectoryAsync(stream, rootDirSector, rootDirSize, files, cancellationToken);

        return files;
    }

    /// <summary>
    /// 递归解析 ISO9660 目录，将其中的文件项加入映射表。
    /// </summary>
    /// <param name="stream">ISO 文件流。</param>
    /// <param name="sector">目录起始扇区。</param>
    /// <param name="size">目录数据总字节数。</param>
    /// <param name="files">文件映射表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task ParseDirectoryAsync(
        FileStream stream,
        long sector,
        int size,
        Dictionary<string, (long Sector, int Size)> files,
        CancellationToken cancellationToken)
    {
        stream.Seek(sector * IsoSectorSize, SeekOrigin.Begin);

        var buffer = new byte[size];
        int totalRead = 0;

        while (totalRead < size)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 读取目录项头（至少 33 字节）。
            var header = new byte[33];
            int headerRead = await stream.ReadAsync(header.AsMemory(0, 33), cancellationToken);
            if (headerRead < 33)
            {
                break;
            }

            totalRead += headerRead;

            byte entryLength = header[DirEntryLengthOffset];
            if (entryLength == 0)
            {
                // 目录项填充到扇区边界，继续下一个扇区。
                int padding = IsoSectorSize - (totalRead % IsoSectorSize);
                if (padding > 0 && padding < IsoSectorSize)
                {
                    stream.Seek(padding, SeekOrigin.Current);
                    totalRead += padding;
                }
                continue;
            }

            byte nameLength = header[DirEntryNameLengthOffset];
            bool isFile = (header[DirEntryFileFlagsOffset] & FileRecordFlag) == 0;

            // 读取文件名。
            var nameBytes = new byte[nameLength];
            int nameRead = await stream.ReadAsync(nameBytes.AsMemory(0, nameLength), cancellationToken);
            totalRead += nameRead;

            if (isFile && nameLength > 0)
            {
                string name = System.Text.Encoding.ASCII.GetString(nameBytes)
                    .Split(';')[0]
                    .TrimEnd('\0');
                long fileSector = ReadUInt32Msb(header, DirEntryLocationOffset);
                int fileSize = ReadUInt32MsbAsInt(header, DirEntryDataSizeOffset);

                // 只收录根目录下的直接文件（不递归子目录）。
                if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith("."))
                {
                    files[name.ToUpperInvariant()] = (fileSector, fileSize);
                }
            }

            // 跳过当前目录项的剩余字节。
            int remaining = entryLength - 33 - nameLength;
            if (remaining > 0)
            {
                stream.Seek(remaining, SeekOrigin.Current);
                totalRead += remaining;
            }
        }
    }

    /// <summary>
    /// 从 ISO 流提取文件到目标路径。
    /// </summary>
    /// <param name="stream">ISO 文件流。</param>
    /// <param name="sector">文件起始扇区。</param>
    /// <param name="size">文件字节数。</param>
    /// <param name="targetPath">目标文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private static async Task ExtractFileAsync(
        FileStream stream,
        long sector,
        int size,
        string targetPath,
        CancellationToken cancellationToken)
    {
        stream.Seek(sector * IsoSectorSize, SeekOrigin.Begin);

        await using var target = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: IsoSectorSize,
            useAsync: true);

        var buffer = new byte[Math.Min(size, IsoSectorSize * 16)];
        int remaining = size;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int toRead = Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    /// <summary>从字节数组读取大端无符号 32 位整数。</summary>
    private static long ReadUInt32Msb(byte[] buffer, int offset)
    {
        return ((long)buffer[offset] << 24) |
               ((long)buffer[offset + 1] << 16) |
               ((long)buffer[offset + 2] << 8) |
               buffer[offset + 3];
    }

    /// <summary>从字节数组读取大端无符号 32 位整数（作为 int）。</summary>
    private static int ReadUInt32MsbAsInt(byte[] buffer, int offset)
    {
        return (int)ReadUInt32Msb(buffer, offset);
    }
}
