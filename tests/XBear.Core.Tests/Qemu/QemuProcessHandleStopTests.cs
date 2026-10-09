using System.Diagnostics;
using System.Text;
using XBear.Core.Qemu;
using XBear.Core.Tests.Qmp;

namespace XBear.Core.Tests.Qemu;

/// <summary>
/// QEMU 进程句柄的停止路径测试。真实 QEMU 不参与，用无主窗口的长驻子进程充当被停对象，
/// QMP 侧用内存内的假服务端应答，因此可以在无图形界面的环境里稳定复现停止行为。
/// </summary>
public sealed class QemuProcessHandleStopTests : IDisposable
{
    private const string Greeting =
        """
        {"QMP": {"version": {"qemu": {"major": 8, "minor": 2, "micro": 0}}, "capabilities": ["oob"]}}
        """;

    private readonly List<Process> _spawned = [];
    private readonly List<string> _logs = [];

    [Fact]
    public async Task 无头进程停止_跳过关闭主窗口并在短宽限后收敛()
    {
        Process process = SpawnHeadlessLongRunningProcess();
        Assert.Equal(IntPtr.Zero, process.MainWindowHandle);

        await using QemuProcessHandleImpl handle = CreateHandle(process, qmpPort: null);

        var stopwatch = Stopwatch.StartNew();
        await handle.StopAsync(TimeSpan.FromSeconds(15));
        stopwatch.Stop();

        Assert.True(handle.HasExited);
        Assert.True(process.HasExited);

        // 无主窗口时关闭主窗口永远无效，修复前会空转满 15 秒；修复后只给固定短宽限。
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"无头停止耗时 {stopwatch.Elapsed.TotalSeconds:F2}s，未体现短宽限收敛。");
    }

    [Fact]
    public async Task 无头进程停止_优先经QMP请求有序退出()
    {
        Process process = SpawnHeadlessLongRunningProcess();
        List<string> received = [];

        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            received.Add(await connection.ReceiveCommandAsync());
            await connection.SendAsync("{\"return\": {}}");

            received.Add(await connection.ReceiveCommandAsync());

            // 被停对象扮演 QEMU 收到 quit 后自行退出，随后才回包。
            TryKill(process);
            await connection.SendAsync("{\"return\": {}}");
        });

        await using QemuProcessHandleImpl handle = CreateHandle(process, server.Port);

        var stopwatch = Stopwatch.StartNew();
        await handle.StopAsync(TimeSpan.FromSeconds(15));
        stopwatch.Stop();

        Assert.Equal(["qmp_capabilities", "quit"], received);
        Assert.True(handle.HasExited);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"收到 quit 后应在短宽限内收敛，实际耗时 {stopwatch.Elapsed.TotalSeconds:F2}s。");
    }

    [Fact]
    public async Task QMP不可达时停止_仍能在短宽限后强杀收敛()
    {
        Process process = SpawnHeadlessLongRunningProcess();
        int closedPort = FakeQmpServer.ReserveClosedPort();

        await using QemuProcessHandleImpl handle = CreateHandle(process, closedPort);

        var stopwatch = Stopwatch.StartNew();
        await handle.StopAsync(TimeSpan.FromSeconds(15));
        stopwatch.Stop();

        Assert.True(handle.HasExited);
        Assert.True(process.HasExited);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(12),
            $"QMP 不可达时耗时 {stopwatch.Elapsed.TotalSeconds:F2}s，超出短宽限与强杀等待的合理上限。");
    }

    [Fact]
    public async Task 进程已退出时停止_直接返回不再等待()
    {
        Process process = SpawnHeadlessLongRunningProcess();
        TryKill(process);
        await process.WaitForExitAsync();

        await using QemuProcessHandleImpl handle = CreateHandle(process, qmpPort: null);

        var stopwatch = Stopwatch.StartNew();
        await handle.StopAsync(TimeSpan.FromSeconds(15));
        stopwatch.Stop();

        Assert.True(handle.HasExited);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"已退出进程的停止不应再等待，实际耗时 {stopwatch.Elapsed.TotalSeconds:F2}s。");
    }

    public void Dispose()
    {
        foreach (Process process in _spawned)
        {
            TryKill(process);
            process.Dispose();
        }

        foreach (string log in _logs)
        {
            try
            {
                File.Delete(log);
            }
            catch (IOException)
            {
                // 临时日志清理失败不影响测试结论。
            }
            catch (UnauthorizedAccessException)
            {
                // 临时日志清理失败不影响测试结论。
            }
        }
    }

    private QemuProcessHandleImpl CreateHandle(Process process, int? qmpPort)
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"xbear-stop-{Guid.NewGuid():N}.log");
        _logs.Add(logPath);

        var writer = new StreamWriter(
            new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

        return new QemuProcessHandleImpl(process, "qemu-system-x86_64", logPath, writer, qmpPort);
    }

    private Process SpawnHeadlessLongRunningProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 300 127.0.0.1 > nul",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = startInfo };
        process.Start();
        _spawned.Add(process);
        return process;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or NotSupportedException)
        {
            // 进程可能已自行退出，强杀失败不影响测试结论。
        }
    }
}