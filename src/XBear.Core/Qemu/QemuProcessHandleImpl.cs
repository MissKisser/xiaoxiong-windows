using System.Diagnostics;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;

namespace XBear.Core.Qemu;

/// <summary>QEMU 子进程句柄的实现，负责日志流式落盘与退出状态跟踪。</summary>
internal sealed class QemuProcessHandleImpl : QemuProcessHandle
{
    private const int NotExitedExitCode = -1;

    private readonly Process _process;
    private readonly StreamWriter _logWriter;
    private readonly object _writeGate = new();
    private readonly Task _completion;

    private int _exitCode = NotExitedExitCode;
    private volatile bool _hasExited;
    private int _disposed;

    /// <summary>构造句柄，接管已启动的进程与日志写入器，并启动日志泵与退出监视。</summary>
    /// <param name="process">已启动的 QEMU 进程。</param>
    /// <param name="commandLine">启动时使用的完整命令行。</param>
    /// <param name="logFilePath">合并日志文件路径。</param>
    /// <param name="logWriter">日志写入器，需处于自动刷新状态。</param>
    internal QemuProcessHandleImpl(
        Process process,
        string commandLine,
        string logFilePath,
        StreamWriter logWriter)
    {
        _process = process;
        _logWriter = logWriter;

        CommandLine = commandLine;
        LogFilePath = logFilePath;
        ProcessId = process.Id;

        var standardOutputPump = PumpAsync(process.StandardOutput);
        var standardErrorPump = PumpAsync(process.StandardError);
        _completion = CompleteAsync(standardOutputPump, standardErrorPump);
    }

    /// <inheritdoc />
    public int ProcessId { get; }

    /// <inheritdoc />
    public string CommandLine { get; }

    /// <inheritdoc />
    public string LogFilePath { get; }

    /// <inheritdoc />
    public bool HasExited => _hasExited;

    /// <inheritdoc />
    public int ExitCode => Volatile.Read(ref _exitCode);

    /// <summary>记录进程已退出并写入退出码。</summary>
    /// <param name="exitCode">进程退出码。</param>
    internal void MarkExited(int exitCode)
    {
        Volatile.Write(ref _exitCode, exitCode);
        _hasExited = true;
    }

    /// <summary>终止进程并等待其完全退出。</summary>
    /// <param name="timeout">等待超时，超时后强杀。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="XBearException">强杀后进程仍未退出时抛出 <see cref="ErrorCategory.Process"/>。</exception>
    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_hasExited)
        {
            await _completion.ConfigureAwait(false);
            return;
        }

        _process.CloseMainWindow();

        if (await WaitForExitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            await _completion.ConfigureAwait(false);
            return;
        }

        _process.Kill(entireProcessTree: true);

        if (!await WaitForExitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            throw new XBearException(
                ErrorCategory.Process,
                $"QEMU 进程 {ProcessId} 在请求终止后仍未退出。",
                "请以管理员身份结束残留的 qemu-system-x86_64 进程后重试。");
        }

        await _completion.ConfigureAwait(false);
    }

    /// <summary>释放进程句柄与日志流，不改变进程本身的运行状态。</summary>
    /// <returns>表示释放完成的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_hasExited)
        {
            await _completion.ConfigureAwait(false);
        }

        try
        {
            await _logWriter.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException)
        {
            // 释放竞态下的重复释放无需处理。
        }

        await _logWriter.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>持续读取一条输出流并逐行写入日志。</summary>
    /// <param name="reader">标准输出或标准错误读取器。</param>
    /// <returns>代表该流读完结束的任务。</returns>
    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                lock (_writeGate)
                {
                    _logWriter.WriteLine(line);
                }
            }
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException
                                              or InvalidOperationException)
        {
            // 句柄释放期间流被关闭，读取提前结束属正常收尾。
        }
    }

    /// <summary>等待进程退出、记录退出码，并等待两路日志泵结束。</summary>
    /// <param name="standardOutputPump">标准输出泵任务。</param>
    /// <param name="standardErrorPump">标准错误泵任务。</param>
    /// <returns>代表退出与日志落盘均完成的任务。</returns>
    private async Task CompleteAsync(Task standardOutputPump, Task standardErrorPump)
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            // 进程对象已释放时停止监视，退出码保持为未退出取值。
        }

        MarkExited(SafeExitCode(_process));

        try
        {
            await Task.WhenAll(standardOutputPump, standardErrorPump).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // 日志写入失败不影响退出状态跟踪。
        }
    }

    /// <summary>在上限时长内轮询进程退出状态。</summary>
    /// <param name="timeout">轮询上限时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>在上限时长内退出返回 true。</returns>
    private async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_hasExited || SafeHasExited(_process))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }

        return _hasExited;
    }

    /// <summary>读取进程退出码，状态不可用时返回 -1。</summary>
    /// <param name="process">目标进程。</param>
    /// <returns>退出码，取值不可用时返回 -1。</returns>
    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : NotExitedExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            return NotExitedExitCode;
        }
    }

    /// <summary>查询进程是否已退出，状态不可用时按已退出处理。</summary>
    /// <param name="process">目标进程。</param>
    /// <returns>已退出返回 true。</returns>
    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}