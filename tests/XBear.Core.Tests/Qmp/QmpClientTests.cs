using System.Diagnostics;
using XBear.Core.Diagnostics;
using XBear.Core.Qmp;

namespace XBear.Core.Tests.Qmp;

/// <summary>QMP 客户端测试，全部针对内存内的假服务端，不需要真实 QEMU。</summary>
public sealed class QmpClientTests
{
    private const string Greeting =
        """
        {"QMP": {"version": {"qemu": {"major": 8, "minor": 2, "micro": 0}}, "capabilities": ["oob"]}}
        """;

    [Fact]
    public async Task ConnectAsync_握手成功并返回能力集()
    {
        List<string> received = [];
        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            received.Add(await connection.ReceiveCommandAsync());
            await connection.SendAsync("{\"return\": {}}");
        });

        await using var client = new QmpClient();
        IReadOnlySet<string> capabilities = await client.ConnectAsync(server.Port);

        Assert.Contains("oob", capabilities);
        Assert.Equal(["qmp_capabilities"], received);
    }

    [Fact]
    public async Task ExecuteAsync_多条异步事件之后仍能取到对应命令的回包()
    {
        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");

            _ = await connection.ReceiveCommandAsync();
            for (int i = 0; i < 5; i++)
            {
                await connection.SendAsync($"{{\"event\": \"RTC_CHANGE\", \"data\": {{\"index\": {i}}}}}");
            }

            await connection.SendAsync("{\"return\": {\"status\": \"running\", \"singlestep\": false}}");

            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {\"name\": \"xiaoxiong-01\"}}");
        });

        await using var client = new QmpClient();
        await client.ConnectAsync(server.Port);

        Assert.True(await client.QueryRunningAsync());
        Assert.Equal("xiaoxiong-01", await client.QueryNameAsync());
    }

    [Fact]
    public async Task ExecuteAsync_并发调用时各自取到自己的回包()
    {
        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");

            for (int i = 0; i < 8; i++)
            {
                string command = await connection.ReceiveCommandAsync();
                await connection.SendAsync($"{{\"event\": \"BLOCKED\", \"data\": {{\"seq\": {i}}}}}");
                await connection.SendAsync($"{{\"return\": {{\"seq\": {i}, \"command\": \"{command}\"}}}}");
            }
        });

        await using var client = new QmpClient();
        await client.ConnectAsync(server.Port);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => client.ExecuteAsync($"probe-{index}")));

        var matched = results
            .Select(element => (Seq: element.GetProperty("seq").GetInt32(), Command: element.GetProperty("command").GetString()))
            .ToArray();

        Assert.Equal(8, matched.Distinct().Count());
        Assert.Equal(8, matched.DistinctBy(pair => pair.Seq).Count());
    }

    [Fact]
    public async Task ExecuteAsync_对端返回error时抛协议错误()
    {
        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");

            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"error\": {\"class\": \"CommandNotFound\", \"desc\": \"未实现\"}}");
        });

        await using var client = new QmpClient();
        await client.ConnectAsync(server.Port);

        XBearException error = await Assert.ThrowsAsync<XBearException>(() => client.ExecuteAsync("nope"));
        Assert.Equal(ErrorCategory.Protocol, error.Category);
        Assert.Contains("CommandNotFound", error.Message);
    }

    [Theory]
    [InlineData("running", true)]
    [InlineData("paused", false)]
    [InlineData("prelaunch", false)]
    public async Task QueryRunningAsync_按状态字段判断运行状态(string status, bool expected)
    {
        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");

            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync($"{{\"return\": {{\"status\": \"{status}\", \"running\": {expected.ToString().ToLowerInvariant()}}}}}");
        });

        await using var client = new QmpClient();
        await client.ConnectAsync(server.Port);

        Assert.Equal(expected, await client.QueryRunningAsync());
    }

    [Fact]
    public async Task ConnectAsync_连接被拒时抛协议错误()
    {
        int port = FakeQmpServer.ReserveClosedPort();

        await using var client = new QmpClient();
        XBearException error = await Assert.ThrowsAsync<XBearException>(() => client.ConnectAsync(port));

        Assert.Equal(ErrorCategory.Protocol, error.Category);
    }

    [Fact]
    public async Task ExecuteAsync_服务端不响应时按超时抛错且不挂起()
    {
        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");
            _ = await connection.ReceiveCommandAsync();

            // 收到命令后刻意不回应。
            while (true)
            {
                await Task.Delay(50, CancellationToken.None);
            }
        });

        await using var client = new QmpClient(TimeSpan.FromMilliseconds(300));
        await client.ConnectAsync(server.Port);

        Stopwatch watch = Stopwatch.StartNew();
        XBearException error = await Assert.ThrowsAsync<XBearException>(() => client.ExecuteAsync("query-status"));
        watch.Stop();

        Assert.Equal(ErrorCategory.Timeout, error.Category);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"实际耗时 {watch.Elapsed}，超时未生效。");
    }

    [Fact]
    public async Task ExecuteAsync_取消令牌可中断等待()
    {
        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");
            _ = await connection.ReceiveCommandAsync();

            while (true)
            {
                await Task.Delay(50, CancellationToken.None);
            }
        });

        await using var client = new QmpClient(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(server.Port);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExecuteAsync("query-status", null, cts.Token));
    }

    [Fact]
    public async Task DisposeAsync_之后命令调用被拒绝()
    {
        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");
            while (true)
            {
                await Task.Delay(50, CancellationToken.None);
            }
        });

        var client = new QmpClient(TimeSpan.FromSeconds(30));
        await client.ConnectAsync(server.Port);
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ExecuteAsync("query-status"));
    }

    [Fact]
    public async Task RequestShutdownAsync_状态转为停止后返回成功()
    {
        int polls = 0;
        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");

            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");

            polls++;
            await connection.SendAsync("{\"return\": {\"status\": \"running\"}}");

            // 收到下一次查询时先插入异步事件，再把状态改为非运行。
            while (await connection.ReceiveCommandAsync() == "query-status")
            {
                if (polls == 1)
                {
                    polls++;
                    await connection.SendAsync("{\"event\": \"SHUTDOWN\", \"data\": {}}");
                    await connection.SendAsync("{\"return\": {\"status\": \"paused\"}}");
                }
            }
        });

        await using var client = new QmpClient();
        await client.ConnectAsync(server.Port);

        Assert.True(await client.RequestShutdownAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task RequestShutdownAsync_关机过程中连接被关闭视为已停止()
    {
        await using var server = new FakeQmpServer(async connection =>
        {
            await connection.SendAsync(Greeting);
            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");

            _ = await connection.ReceiveCommandAsync();
            await connection.SendAsync("{\"return\": {}}");
            connection.Close();
        });

        await using var client = new QmpClient();
        await client.ConnectAsync(server.Port);

        Assert.True(await client.RequestShutdownAsync(TimeSpan.FromSeconds(10)));
    }
}