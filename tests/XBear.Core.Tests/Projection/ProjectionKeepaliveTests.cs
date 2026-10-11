using System.Text;
using XBear.Core.Abstractions;
using XBear.Core.Adb;
using XBear.Core.Diagnostics;
using XBear.Core.Projection;
using XBear.Core.Tests.Adb;

namespace XBear.Core.Tests.Projection;

/// <summary>
/// 保活损伤通道测试。全部针对内存内的假 adbd 运行，
/// 断言的是真正发到 adbd 上的命令形态：脚本落地、后台分离启动与会话停止时的清理。
/// </summary>
public sealed class ProjectionKeepaliveTests
{
    private const int FakePid = 4242;

    private static FakeAdbdServer CreateServer() =>
        new()
        {
            ShellHandler = command =>
                command.StartsWith("cat ", StringComparison.Ordinal) && command.Contains("keepalive.pid", StringComparison.Ordinal)
                    ? new ShellResponse($"{FakePid}\n", 0)
                    : new ShellResponse(string.Empty, 0),
        };

    private static async Task<IAdbClient> ConnectAsync(FakeAdbdServer server)
    {
        IAdbClient client = server.CreateClient(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(5555);
        return client;
    }

    [Fact]
    public void 损伤脚本_取值单调递增且节拍可读_不使用外部进程实现节拍()
    {
        string script = ProjectionKeepalive.BuildScript(30);

        Assert.StartsWith("#!/system/bin/sh", script, StringComparison.Ordinal);
        Assert.Contains($"F={ProjectionKeepalive.GuestFramebufferPath}", script, StringComparison.Ordinal);
        Assert.Contains($"echo $$ > {ProjectionKeepalive.GuestPidPath}", script, StringComparison.Ordinal);
        Assert.Contains("mkfifo " + ProjectionKeepalive.GuestFifoPath, script, StringComparison.Ordinal);
        Assert.Contains("exec 3<>" + ProjectionKeepalive.GuestFifoPath, script, StringComparison.Ordinal);
        Assert.Contains("read -t 0.0333 _xb <&3", script, StringComparison.Ordinal);

        // 取值必须逐次不同：服务端按逐块比对判定损伤，两色交替会被判为无变化。
        Assert.Contains("echo -ne '\\000' > $F", script, StringComparison.Ordinal);
        Assert.Contains($"echo -ne '\\{ProjectionKeepalive.DamageValueCount - 1:000}' > $F", script, StringComparison.Ordinal);

        // 写入与节拍都由 shell 内建完成，不派生外部进程。
        Assert.DoesNotContain("sleep ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("dd ", script, StringComparison.Ordinal);
    }

    [Fact]
    public void 损伤脚本_节拍随频率变化且频率被夹取到允许区间()
    {
        Assert.Contains("read -t 0.125 _xb", ProjectionKeepalive.BuildScript(8), StringComparison.Ordinal);
        Assert.Contains("read -t 0.0667 _xb", ProjectionKeepalive.BuildScript(15), StringComparison.Ordinal);
        Assert.Contains("read -t 0.02 _xb", ProjectionKeepalive.BuildScript(50), StringComparison.Ordinal);
        Assert.Contains("read -t 1 _xb", ProjectionKeepalive.BuildScript(1), StringComparison.Ordinal);

        // 越界频率一律夹取到允许区间内，不会生成零节拍或负节拍。
        Assert.Contains(
            "read -t 0.0333 _xb",
            ProjectionKeepalive.BuildScript(0),
            StringComparison.Ordinal);
        Assert.Contains(
            "read -t 0.0167 _xb",
            ProjectionKeepalive.BuildScript(9999),
            StringComparison.Ordinal);
    }

    [Fact]
    public void 启停命令_后台分离且停止时清理全部落地文件()
    {
        string start = ProjectionKeepalive.BuildStartCommand();
        Assert.Equal(
            $"sh {ProjectionKeepalive.GuestScriptPath} </dev/null >{ProjectionKeepalive.GuestLogPath} 2>&1 & true",
            start);

        string stop = ProjectionKeepalive.BuildStopCommand();
        Assert.Contains($"kill $(cat {ProjectionKeepalive.GuestPidPath})", stop, StringComparison.Ordinal);
        Assert.Contains($"rm -f {ProjectionKeepalive.GuestPidPath}", stop, StringComparison.Ordinal);
        Assert.Contains(ProjectionKeepalive.GuestFifoPath, stop, StringComparison.Ordinal);
        Assert.Contains(ProjectionKeepalive.GuestScriptPath, stop, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 启动通道_推入脚本并以脱离adb会话的后台命令拉起()
    {
        await using var server = CreateServer();
        await using IAdbClient client = await ConnectAsync(server);
        await using var keepalive = new ProjectionKeepalive(
            client,
            5555,
            new ProjectionKeepaliveOptions
            {
                FrequencyHz = 15,
                StartupProbeTimeout = TimeSpan.FromSeconds(2),
            });

        Assert.True(await keepalive.StartAsync());
        Assert.True(keepalive.IsActive);
        Assert.Equal(15, keepalive.FrequencyHz);
        Assert.Equal(FakePid, keepalive.Pid);
        Assert.Null(keepalive.LastError);

        Assert.Contains($"mkdir -p {ProjectionKeepalive.GuestDirectory}", server.ShellCommands);
        Assert.Contains($"chmod 700 {ProjectionKeepalive.GuestScriptPath}", server.ShellCommands);
        Assert.Contains(ProjectionKeepalive.BuildStartCommand(), server.ShellCommands);

        byte[]? pushed = server.GetFile(ProjectionKeepalive.GuestScriptPath);
        Assert.NotNull(pushed);
        string script = Encoding.UTF8.GetString(pushed);
        Assert.Equal(ProjectionKeepalive.BuildScript(15), script);
        Assert.Contains("read -t 0.0667 _xb", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 停止通道_发出终止命令并清理脚本与进程号文件()
    {
        await using var server = CreateServer();
        await using IAdbClient client = await ConnectAsync(server);
        var keepalive = new ProjectionKeepalive(
            client,
            5555,
            new ProjectionKeepaliveOptions { StartupProbeTimeout = TimeSpan.FromSeconds(2) });

        Assert.True(await keepalive.StartAsync());
        await keepalive.StopAsync();

        Assert.False(keepalive.IsActive);
        Assert.Null(keepalive.Pid);
        Assert.Contains(ProjectionKeepalive.BuildStopCommand(), server.ShellCommands);
    }

    [Fact]
    public async Task 重复启动_先清理旧循环再拉起_不会叠加两个损伤循环()
    {
        await using var server = CreateServer();
        await using IAdbClient client = await ConnectAsync(server);
        await using var keepalive = new ProjectionKeepalive(
            client,
            5555,
            new ProjectionKeepaliveOptions { StartupProbeTimeout = TimeSpan.FromSeconds(2) });

        await keepalive.StartAsync();
        int afterFirst = server.ShellCommands.Count(c => c == ProjectionKeepalive.BuildStopCommand());
        await keepalive.StartAsync();
        int afterSecond = server.ShellCommands.Count(c => c == ProjectionKeepalive.BuildStopCommand());

        Assert.Equal(0, afterFirst);
        Assert.Equal(1, afterSecond);
        Assert.True(keepalive.IsActive);
    }

    [Fact]
    public async Task 未启动即停止_不向实例发出任何命令()
    {
        await using var server = CreateServer();
        await using IAdbClient client = await ConnectAsync(server);
        await using var keepalive = new ProjectionKeepalive(client, 5555);

        await keepalive.StopAsync();

        Assert.Empty(server.ShellCommands);
        Assert.False(keepalive.IsActive);
    }

    [Fact]
    public async Task 通道释放_等价于停止并清理落地文件()
    {
        await using var server = CreateServer();
        await using IAdbClient client = await ConnectAsync(server);
        var keepalive = new ProjectionKeepalive(
            client,
            5555,
            new ProjectionKeepaliveOptions { StartupProbeTimeout = TimeSpan.FromSeconds(2) });

        await keepalive.StartAsync();
        Assert.True(keepalive.IsActive);

        await keepalive.DisposeAsync();

        Assert.False(keepalive.IsActive);
        Assert.Contains(ProjectionKeepalive.BuildStopCommand(), server.ShellCommands);
    }

    [Fact]
    public async Task 启动过程_进程号文件稍晚生成时持续重试直至出现()
    {
        int probes = 0;
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command =>
            {
                if (!command.Contains("keepalive.pid", StringComparison.Ordinal))
                {
                    return new ShellResponse(string.Empty, 0);
                }

                probes++;
                return probes < 4
                    ? new ShellResponse(string.Empty, 1)
                    : new ShellResponse($"{FakePid}" + Environment.NewLine, 0);
            },
        };
        await using IAdbClient client = await ConnectAsync(server);
        await using var keepalive = new ProjectionKeepalive(
            client,
            5555,
            new ProjectionKeepaliveOptions
            {
                StartupProbeTimeout = TimeSpan.FromSeconds(5),
                CommandTimeout = TimeSpan.FromSeconds(5),
            });

        Assert.True(await keepalive.StartAsync());
        Assert.True(keepalive.IsActive);
        Assert.Equal(FakePid, keepalive.Pid);
        Assert.Null(keepalive.LastError);
    }

    [Fact]
    public async Task 启动失败_通道保持未激活并记录原因而不抛出()
    {
        await using var server = new FakeAdbdServer
        {
            ShellHandler = command =>
                command.Contains("keepalive.pid", StringComparison.Ordinal)
                    ? new ShellResponse(string.Empty, 1)
                    : new ShellResponse(string.Empty, 1),
        };
        await using IAdbClient client = await ConnectAsync(server);
        await using var keepalive = new ProjectionKeepalive(
            client,
            5555,
            new ProjectionKeepaliveOptions
            {
                StartupProbeTimeout = TimeSpan.FromMilliseconds(300),
                CommandTimeout = TimeSpan.FromSeconds(2),
            });

        Assert.False(await keepalive.StartAsync());
        Assert.False(keepalive.IsActive);
        Assert.Null(keepalive.Pid);
        Assert.NotNull(keepalive.LastError);
    }

    [Fact]
    public async Task 端口非法_构造即被拒绝()
    {
        await using var server = CreateServer();
        await using IAdbClient client = await ConnectAsync(server);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectionKeepalive(client, 0));
        Assert.Throws<ArgumentNullException>(() => new ProjectionKeepalive(null!, 5555));
    }
}