using System.Diagnostics;
using System.Text;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Qemu;

/// <summary>拉起并托管 QEMU 子进程，把标准输出与标准错误合并写入实例专属日志。</summary>
public sealed class QemuLauncher : IQemuLauncher
{
    /// <summary>启动后判定早期失败的默认观察时长。</summary>
    public static readonly TimeSpan DefaultStartupProbeTimeout = TimeSpan.FromSeconds(2);

    private const string LogFileExtension = ".log";
    private const int LogTailLineLimit = 20;
    private const int LogTailCharacterLimit = 2000;

    private readonly QemuPaths _paths;
    private readonly IQemuArgBuilder _argBuilder;
    private readonly string _logDirectory;
    private readonly TimeSpan _startupProbeTimeout;

    /// <summary>构造启动器。</summary>
    /// <param name="paths">QEMU 可执行文件定位结果。</param>
    /// <param name="argBuilder">参数生成器。</param>
    /// <param name="logDirectory">实例日志根目录，不存在时自动创建。</param>
    public QemuLauncher(QemuPaths paths, IQemuArgBuilder argBuilder, string logDirectory)
        : this(paths, argBuilder, logDirectory, null)
    {
    }

    /// <summary>构造启动器并指定早期失败观察时长。</summary>
    /// <param name="paths">QEMU 可执行文件定位结果。</param>
    /// <param name="argBuilder">参数生成器。</param>
    /// <param name="logDirectory">实例日志根目录，不存在时自动创建。</param>
    /// <param name="startupProbeTimeout">
    /// 启动后观察早期退出的时长，为 null 时使用 <see cref="DefaultStartupProbeTimeout"/>。
    /// </param>
    /// <exception cref="XBearException">日志目录为空或不可创建时抛出 <see cref="ErrorCategory.Process"/>。</exception>
    public QemuLauncher(
        QemuPaths paths,
        IQemuArgBuilder argBuilder,
        string logDirectory,
        TimeSpan? startupProbeTimeout)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(argBuilder);

        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new XBearException(ErrorCategory.Process, "实例日志目录不能为空。");
        }

        try
        {
            Directory.CreateDirectory(logDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new XBearException(
                ErrorCategory.Process,
                $"无法创建实例日志目录 {logDirectory}：{exception.Message}",
                "请确认该目录可写，或改用其他日志目录。",
                exception);
        }

        _paths = paths;
        _argBuilder = argBuilder;
        _logDirectory = logDirectory;
        _startupProbeTimeout = startupProbeTimeout ?? DefaultStartupProbeTimeout;
    }

    /// <summary>启动实例进程并开始捕获日志。</summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="image">实例引用的镜像清单，可为空，空值不下发镜像保真度相关引导参数。</param>
    /// <param name="diskPath">实例可写磁盘镜像路径。</param>
    /// <param name="ports">本次分配到的宿主端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>运行中的进程句柄。</returns>
    /// <exception cref="XBearException">
    /// 进程无法启动时抛出 <see cref="ErrorCategory.Dependency"/>；
    /// 进程在启动后立即以非零码退出时抛出 <see cref="ErrorCategory.Process"/>，异常信息含退出码与日志尾部。
    /// </exception>
    public async Task<QemuProcessHandle> StartAsync(
        InstanceSpec spec,
        ImageSpec? image,
        string diskPath,
        AllocatedPorts ports,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var executablePath = ResolveExecutablePath();
        var arguments = _argBuilder.BuildStartArguments(spec, image, diskPath, ports);
        var logFilePath = BuildLogFilePath(spec.Id);

        var logWriter = new StreamWriter(
            new FileStream(
                logFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.Asynchronous),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = CommandLineFormatter.Join(arguments),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 进程对象的生命周期在成功启动后移交句柄接管，此处不使用 using 以免提前释放。
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                await logWriter.DisposeAsync().ConfigureAwait(false);
                process.Dispose();
                throw new XBearException(
                    ErrorCategory.Dependency,
                    $"QEMU 进程未能启动：{executablePath}",
                    "请确认该路径下是完整的 QEMU for Windows 安装，并重新定位 QEMU 可执行文件。");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException)
        {
            await logWriter.DisposeAsync().ConfigureAwait(false);
            process.Dispose();
            throw new XBearException(
                ErrorCategory.Dependency,
                $"QEMU 进程启动失败：{exception.Message}",
                "请确认 QEMU 安装完整且可执行文件未被安全软件拦截。",
                exception);
        }

        var handle = new QemuProcessHandleImpl(process, startInfo.Arguments, logFilePath, logWriter, ports.Qmp);

        await ProbeStartupFailureAsync(handle, cancellationToken).ConfigureAwait(false);
        return handle;
    }

    /// <summary>在观察窗口内检测早期异常退出。</summary>
    /// <param name="handle">待检测的句柄。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>未观察到异常退出时返回 true。</returns>
    /// <exception cref="XBearException">进程在观察窗口内以非零码退出时抛出 <see cref="ErrorCategory.Process"/>。</exception>
    private async Task<bool> ProbeStartupFailureAsync(
        QemuProcessHandle handle,
        CancellationToken cancellationToken)
    {
        if (_startupProbeTimeout <= TimeSpan.Zero)
        {
            return true;
        }

        await Task.Delay(_startupProbeTimeout, cancellationToken).ConfigureAwait(false);

        if (!handle.HasExited || handle.ExitCode == 0)
        {
            return true;
        }

        var logTail = ReadLogTail(handle.LogFilePath);
        await handle.DisposeAsync().ConfigureAwait(false);

        throw new XBearException(
            ErrorCategory.Process,
            $"QEMU 进程启动后立即退出，退出码 {handle.ExitCode}。日志尾部：{logTail}",
            "常见原因是 WHPX 硬件加速不可用或磁盘镜像损坏；请确认系统已启用 Windows Hypervisor 平台与虚拟机平台，并用 qemu-img 校验镜像后重试。");
    }

    /// <summary>读取日志文件末尾若干行，用于诊断信息。</summary>
    /// <param name="logFilePath">日志文件路径。</param>
    /// <returns>截断后的日志尾部文本，读取失败时返回占位说明。</returns>
    private static string ReadLogTail(string logFilePath)
    {
        try
        {
            if (!File.Exists(logFilePath))
            {
                return "（日志文件尚未生成）";
            }

            // 日志写入句柄仍处于打开状态，必须共享写入权限才能读到已刷出的内容。
            using var stream = new FileStream(
                logFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            var lines = reader.ReadToEnd().Split(
                new[] { "\r\n", "\n", "\r" },
                StringSplitOptions.RemoveEmptyEntries);

            var tail = lines.Length > LogTailLineLimit ? lines[^LogTailLineLimit..] : lines;
            var text = string.Join(Environment.NewLine, tail);

            return text.Length > LogTailCharacterLimit ? text[^LogTailCharacterLimit..] : text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"（日志读取失败：{exception.Message}）";
        }
    }

    /// <summary>定位 QEMU 系统模拟器可执行文件。</summary>
    /// <returns>可执行文件绝对路径。</returns>
    /// <exception cref="XBearException">路径不存在时抛出 <see cref="ErrorCategory.Dependency"/>。</exception>
    private string ResolveExecutablePath()
    {
        var executablePath = _paths.QemuSystemPath;
        if (File.Exists(executablePath))
        {
            return executablePath;
        }

        throw new XBearException(
            ErrorCategory.Dependency,
            $"QEMU 可执行文件不存在：{executablePath}",
            "请重新定位 QEMU 安装目录，确认其中的 qemu-system-x86_64.exe 存在且未被移动。");
    }

    /// <summary>由实例标识派生实例专属日志文件路径。</summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>日志文件绝对路径。</returns>
    private string BuildLogFilePath(string instanceId)
    {
        var leaf = string.IsNullOrWhiteSpace(instanceId) ? "instance" : SanitizeFileName(instanceId);
        return Path.Combine(_logDirectory, leaf + LogFileExtension);
    }

    /// <summary>把实例标识转换为合法文件名。</summary>
    /// <param name="value">原始标识。</param>
    /// <returns>仅含合法文件名字符的文本。</returns>
    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var characters = value.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(characters);
    }
}