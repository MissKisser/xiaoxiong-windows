using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Projection;

namespace XBear.Core.Tests.Projection;

/// <summary>
/// RFB 3.8 取帧客户端单元测试，全部针对内存内 FakeVncServer 运行。
/// </summary>
public sealed class VncClientTests
{
    [Fact]
    public async Task 握手成功取到首帧_像素解码逐字节正确()
    {
        const int width = 4;
        const int height = 2;

        // 构造一个 4x2 的已知图案：每像素具有明确独特的 BGRA
        byte[] expectedPixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            expectedPixels[i * 4 + 0] = (byte)(10 + i * 10);  // B
            expectedPixels[i * 4 + 1] = (byte)(50 + i * 10);  // G
            expectedPixels[i * 4 + 2] = (byte)(100 + i * 10); // R
            expectedPixels[i * 4 + 3] = 0xFF;                 // A
        }

        var tcs = new TaskCompletionSource<bool>();
        await using var server = FakeVncServer.CreateStandard(width, height, "test-desktop", async conn =>
        {
            // 等待客户端请求帧缓冲更新
            var req = await conn.ReceiveFramebufferUpdateRequestAsync();
            Assert.Equal(0, req.Incremental); // 首帧为全量请求

            // 发送已知 Raw 像素帧
            await conn.SendRawFrameAsync(width, height, expectedPixels);

            // 等待下一轮增量请求表示首帧处理完成
            await conn.ReceiveFramebufferUpdateRequestAsync();
            tcs.TrySetResult(true);
        });

        var frames = new ProjectionFrameStore(width, height);
        await using var client = new VncClient();

        VncServerInfo info = await client.ConnectAsync(server.Port, frames);
        Assert.Equal(width, info.Geometry.Width);
        Assert.Equal(height, info.Geometry.Height);
        Assert.Equal("test-desktop", info.DesktopName);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = Task.Run(() => client.RunFrameLoopAsync(frames, cts.Token));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }

        ProjectionFrame? snapshot = frames.CaptureLatest();
        Assert.NotNull(snapshot);
        Assert.Equal(width, snapshot.Width);
        Assert.Equal(height, snapshot.Height);
        Assert.Equal(1, snapshot.Sequence);

        // 逐像素核验颜色分量
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var pixel = snapshot.GetPixel(x, y);
                int idx = (y * width) + x;
                Assert.Equal(expectedPixels[idx * 4 + 0], pixel.B);
                Assert.Equal(expectedPixels[idx * 4 + 1], pixel.G);
                Assert.Equal(expectedPixels[idx * 4 + 2], pixel.R);
                Assert.Equal(0xFF, pixel.A);
            }
        }
    }

    [Fact]
    public async Task 分辨率变化后_会话继续且新帧缓冲尺寸正确()
    {
        const int w1 = 4;
        const int h1 = 4;
        const int w2 = 8;
        const int h2 = 6;

        byte[] pixels1 = new byte[w1 * h1 * 4];
        Array.Fill(pixels1, (byte)128);

        byte[] pixels2 = new byte[w2 * h2 * 4];
        for (int i = 0; i < w2 * h2; i++)
        {
            pixels2[i * 4 + 0] = 11; // B
            pixels2[i * 4 + 1] = 22; // G
            pixels2[i * 4 + 2] = 33; // R
            pixels2[i * 4 + 3] = 0xFF; // A
        }

        var tcs = new TaskCompletionSource<bool>();
        await using var server = FakeVncServer.CreateStandard(w1, h1, "resize-desktop", async conn =>
        {
            // 接收首帧请求并推送帧 1
            await conn.ReceiveFramebufferUpdateRequestAsync();
            await conn.SendRawFrameAsync(w1, h1, pixels1);

            // 接收第二轮请求，推送分辨率变化
            await conn.ReceiveFramebufferUpdateRequestAsync();
            await conn.SendResolutionChangeAsync(w2, h2);

            // 接收分辨率变化后的第三轮请求，推送新分辨率下的帧 2
            await conn.ReceiveFramebufferUpdateRequestAsync();
            await conn.SendRawFrameAsync(w2, h2, pixels2);

            // 接收第四轮请求表示帧 2 处理完毕
            await conn.ReceiveFramebufferUpdateRequestAsync();
            tcs.TrySetResult(true);
        });

        var frames = new ProjectionFrameStore(w1, h1);
        await using var client = new VncClient();

        VncResolutionChanged? recordedChange = null;
        client.ResolutionChanged += (sender, args) => recordedChange = args;

        await client.ConnectAsync(server.Port, frames);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = Task.Run(() => client.RunFrameLoopAsync(frames, cts.Token));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.NotNull(recordedChange);
        Assert.Equal(w1, recordedChange.Value.Previous.Width);
        Assert.Equal(h1, recordedChange.Value.Previous.Height);
        Assert.Equal(w2, recordedChange.Value.Current.Width);
        Assert.Equal(h2, recordedChange.Value.Current.Height);

        ProjectionFrame? snapshot = frames.CaptureLatest();
        Assert.NotNull(snapshot);
        Assert.Equal(w2, snapshot.Width);
        Assert.Equal(h2, snapshot.Height);
        Assert.Equal(2, snapshot.Sequence);

        var sample = snapshot.GetPixel(0, 0);
        Assert.Equal(11, sample.B);
        Assert.Equal(22, sample.G);
        Assert.Equal(33, sample.R);
        Assert.Equal(0xFF, sample.A);
    }

    [Fact]
    public async Task 安全协商被服务端拒绝_抛出包含原因的协议异常()
    {
        await using var server = new FakeVncServer(async conn =>
        {
            await conn.SendVersionAsync();
            await conn.ReceiveVersionAsync();
            await conn.SendSecurityRejectedAsync("客户端 IP 不在允许列表");
        });

        var frames = new ProjectionFrameStore(100, 100);
        await using var client = new VncClient();

        XBearException ex = await Assert.ThrowsAsync<XBearException>(
            () => client.ConnectAsync(server.Port, frames));

        Assert.Equal(ErrorCategory.Protocol, ex.Category);
        Assert.Contains("客户端 IP 不在允许列表", ex.Message);
    }

    [Fact]
    public async Task 服务端只支持密码认证_本端明确提示不支持()
    {
        await using var server = new FakeVncServer(async conn =>
        {
            await conn.SendVersionAsync();
            await conn.ReceiveVersionAsync();
            // 2 代表 VncAuth (密码认证)
            await conn.SendSecurityTypesAsync(2);
        });

        var frames = new ProjectionFrameStore(100, 100);
        await using var client = new VncClient();

        XBearException ex = await Assert.ThrowsAsync<XBearException>(
            () => client.ConnectAsync(server.Port, frames));

        Assert.Equal(ErrorCategory.Protocol, ex.Category);
        Assert.Contains("本端仅支持不认证", ex.Message);
    }

    [Fact]
    public async Task 异常断线后_RunFrameLoopAsync抛出Protocol异常()
    {
        await using var server = FakeVncServer.CreateStandard(100, 100, "drop-desktop", async conn =>
        {
            await conn.ReceiveFramebufferUpdateRequestAsync();
            // 直接断开
            conn.Disconnect();
        });

        var frames = new ProjectionFrameStore(100, 100);
        await using var client = new VncClient();
        await client.ConnectAsync(server.Port, frames);

        XBearException ex = await Assert.ThrowsAsync<XBearException>(
            () => client.RunFrameLoopAsync(frames));

        Assert.Equal(ErrorCategory.Protocol, ex.Category);
        Assert.Contains("连接已中断", ex.Message);
    }
}
