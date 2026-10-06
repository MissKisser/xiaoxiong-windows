using System.Diagnostics;

namespace XBear.Core.Qemu;

/// <summary>一次性外部命令的执行结果。</summary>
/// <param name="ExitCode">进程退出码，超时强杀时为 -1。</param>
/// <param name="StandardOutput">标准输出全文。</param>
/// <param name="StandardError">标准错误全文。</param>
/// <param name="TimedOut">是否因超时被强制终止。</param>
internal sealed record QemuCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    /// <summary>标准输出与标准错误的合并文本，用于诊断信息。</summary>
    internal string CombinedOutput
        => string.IsNullOrWhiteSpace(StandardOutput)
            ? StandardError
            : string.IsNullOrWhiteSpace(StandardError)
                ? StandardOutput
                : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>以受限时长运行 QEMU 自带命令行工具，并收集其输出。</summary>
internal static class QemuCommandRunner
{
    /// <summary>运行命令直到其自行退出或超时。</summary>
    /// <param name="executablePath">可执行文件绝对路径。</param>
    /// <param name="arguments">参数序列。</param>
    /// <param name="timeout">等待退出的上限时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果，含退出码与合并输出。</returns>
    internal static async Task<QemuCommandResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = CommandLineFormatter.Join(arguments),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);

        var completed = await Task.WhenAny(exitTask, Task.Delay(timeout, cancellationToken))
            .ConfigureAwait(false);

        var timedOut = completed != exitTask;
        if (timedOut)
        {
            TryKill(process);
        }

        await exitTask.ConfigureAwait(false);
        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        if (timedOut)
        {
            return new QemuCommandResult(-1, standardOutput, standardError, true);
        }

        return new QemuCommandResult(process.ExitCode, standardOutput, standardError, false);
    }

    /// <summary>尽力终止进程树，失败时不向上抛出。</summary>
    /// <param name="process">目标进程。</param>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
                                              or System.ComponentModel.Win32Exception)
        {
            // 进程可能已自行退出或无权限终止，此处无需中断调用方。
        }
    }
}