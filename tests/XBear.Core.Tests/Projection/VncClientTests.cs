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

    [Fact]
    public async Task 常驻挂起请求_首帧非增量之后每帧都立即补发下一条增量请求()
    {
        const int width = 8;
        const int height = 4;
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0x20);

        var requests = new List<(byte Incremental, int X, int Y, int Width, int Height)>();
        var allDone = new TaskCompletionSource<bool>();

        await using var server = FakeVncServer.CreateStandard(width, height, "pending-desktop", async conn =>
        {
            for (int index = 0; index < 4; index++)
            {
                var request = await conn.ReceiveFramebufferUpdateRequestAsync();
                lock (requests)
                {
                    requests.Add(request);
                }

                if (index == 0)
                {
                    // 首帧必须是整屏非增量请求。
                    Assert.Equal(0, request.Incremental);
                }
                else
                {
                    Assert.Equal(1, request.Incremental);
                }

                await conn.SendRawFrameAsync(width, height, pixels);
            }

            // 第五次读取应立刻拿到客户端读完第四帧后补发的请求。
            var fifth = await conn.ReceiveFramebufferUpdateRequestAsync();
            lock (requests)
            {
                requests.Add(fifth);
            }

            allDone.TrySetResult(true);
        });

        var frames = new ProjectionFrameStore(width, height);
        await using var client = new VncClient();
        await client.ConnectAsync(server.Port, frames);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loop = Task.Run(() => client.RunFrameLoopAsync(frames, cts.Token));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }

        (byte Incremental, int X, int Y, int Width, int Height)[] snapshot;
        lock (requests)
        {
            snapshot = requests.ToArray();
        }

        Assert.Equal(5, snapshot.Length);
        for (int index = 1; index < snapshot.Length; index++)
        {
            Assert.Equal(1, snapshot[index].Incremental);
            Assert.Equal(0, snapshot[index].X);
            Assert.Equal(0, snapshot[index].Y);
            Assert.Equal(width, snapshot[index].Width);
            Assert.Equal(height, snapshot[index].Height);
        }

        // 画面完整性从未异常，因此不应产生任何全帧重同步。
        Assert.Equal(0, client.FullResyncCount);
        Assert.Equal(0, client.IntegrityFaultCount);
    }

    [Fact]
    public async Task 常驻挂起请求_服务端静默时客户端不再补发且请求保持挂起()
    {
        const int width = 16;
        const int height = 16;
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0x11);

        var secondRequestArrived = new TaskCompletionSource<bool>();
        var idleWindowElapsed = new TaskCompletionSource<bool>();

        await using var server = FakeVncServer.CreateStandard(width, height, "idle-desktop", async conn =>
        {
            var first = await conn.ReceiveFramebufferUpdateRequestAsync();
            Assert.Equal(0, first.Incremental);
            await conn.SendRawFrameAsync(width, height, pixels);

            await conn.ReceiveFramebufferUpdateRequestAsync();
            secondRequestArrived.TrySetResult(true);

            // 服务端此后静默：挂起请求若被正确保持，客户端不应再发出任何请求。
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            idleWindowElapsed.TrySetResult(true);
        });

        var frames = new ProjectionFrameStore(width, height);
        await using var client = new VncClient();
        await client.ConnectAsync(server.Port, frames);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loop = Task.Run(() => client.RunFrameLoopAsync(frames, cts.Token));

        await idleWindowElapsed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(secondRequestArrived.Task.IsCompletedSuccessfully);
        Assert.Equal(1, frames.Sequence);
    }

    [Fact]
    public async Task 解码矩形越界_消费像素保持流同步并补发非增量请求做全帧重同步()
    {
        const int width = 8;
        const int height = 8;
        byte[] pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0x33);

        var requests = new List<byte>();
        var sessionAlive = new TaskCompletionSource<bool>();

        await using var server = FakeVncServer.CreateStandard(width, height, "bounds-desktop", async conn =>
        {
            // 首帧：矩形右边界越界一列。
            requests.Add((await conn.ReceiveFramebufferUpdateRequestAsync()).Incremental);
            await conn.SendRawFrameAsync(new FakeVncConnection.RawRect(0, 0, width, height, 0x33));
            await conn.SendRawFrameAsync(new FakeVncConnection.RawRect(width - 1, 0, 2, height, 0x44));

            // 会话必须继续：随后仍能读到客户端补发的请求与正常帧。
            requests.Add((await conn.ReceiveFramebufferUpdateRequestAsync()).Incremental);
            await conn.SendRawFrameAsync(width, height, pixels);
            requests.Add((await conn.ReceiveFramebufferUpdateRequestAsync()).Incremental);

            sessionAlive.TrySetResult(true);
        });

        var frames = new ProjectionFrameStore(width, height);
        await using var client = new VncClient();
        await client.ConnectAsync(server.Port, frames);

        var lastFrameDecoded = new TaskCompletionSource<bool>();
        client.FrameDecoded += (_, args) =>
        {
            if (args.Sequence == 2)
            {
                lastFrameDecoded.TrySetResult(true);
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loop = Task.Run(() => client.RunFrameLoopAsync(frames, cts.Token));

        await sessionAlive.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await lastFrameDecoded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }

        byte[] observed;
        lock (requests)
        {
            observed = requests.ToArray();
        }

        Assert.Equal(new byte[] { 0, 1, 0 }, observed);
        Assert.Equal(1, client.IntegrityFaultCount);
        Assert.Equal(1, client.FullResyncCount);
        Assert.NotNull(client.LastIntegrityFault);
        Assert.Contains("超出画面范围", client.LastIntegrityFault);

        // 越界帧没有任何像素落进帧缓冲，因此不占用画面序号；序号只由前后两帧合法矩形推进。
        ProjectionFrame? snapshot = frames.CaptureLatest();
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Sequence);
    }

    [Fact]
    public async Task 单帧多矩形越界_只补发一次全帧重同步并累计异常次数()
    {
        const int width = 8;
        const int height = 8;
        byte[] pixels = new byte[width * height * 4];

        var requests = new List<byte>();
        var done = new TaskCompletionSource<bool>();

        await using var server = FakeVncServer.CreateStandard(width, height, "multibounds-desktop", async conn =>
        {
            requests.Add((await conn.ReceiveFramebufferUpdateRequestAsync()).Incremental);

            await conn.SendRawFrameAsync(
                new FakeVncConnection.RawRect(0, 0, width, height),
                new FakeVncConnection.RawRect(0, height, 1, 1),
                new FakeVncConnection.RawRect(width, 0, 1, 1));

            requests.Add((await conn.ReceiveFramebufferUpdateRequestAsync()).Incremental);
            await conn.SendRawFrameAsync(width, height, pixels);
            requests.Add((await conn.ReceiveFramebufferUpdateRequestAsync()).Incremental);
            done.TrySetResult(true);
        });

        var frames = new ProjectionFrameStore(width, height);
        await using var client = new VncClient();
        await client.ConnectAsync(server.Port, frames);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loop = Task.Run(() => client.RunFrameLoopAsync(frames, cts.Token));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }

        byte[] observed;
        lock (requests)
        {
            observed = requests.ToArray();
        }

        Assert.Equal(new byte[] { 0, 0, 1 }, observed);
        Assert.Equal(2, client.IntegrityFaultCount);
        Assert.Equal(1, client.FullResyncCount);
    }

    [Fact]
    public async Task 分辨率变化后_下一次请求为非增量以重建整屏画面()
    {
        const int w1 = 8;
        const int h1 = 8;
        const int w2 = 12;
        const int h2 = 10;
        byte[] pixels = new byte[w2 * h2 * 4];

        var requests = new List<(byte Incremental, int Width, int Height)>();
        var done = new TaskCompletionSource<bool>();

        await using var server = FakeVncServer.CreateStandard(w1, h1, "resize-resync-desktop", async conn =>
        {
            var first = await conn.ReceiveFramebufferUpdateRequestAsync();
            lock (requests)
            {
                requests.Add((first.Incremental, first.Width, first.Height));
            }

            await conn.SendResolutionChangeAsync(w2, h2);

            var second = await conn.ReceiveFramebufferUpdateRequestAsync();
            lock (requests)
            {
                requests.Add((second.Incremental, second.Width, second.Height));
            }

            await conn.SendRawFrameAsync(w2, h2, pixels);
            await conn.ReceiveFramebufferUpdateRequestAsync();
            done.TrySetResult(true);
        });

        var frames = new ProjectionFrameStore(w1, h1);
        await using var client = new VncClient();
        await client.ConnectAsync(server.Port, frames);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loop = Task.Run(() => client.RunFrameLoopAsync(frames, cts.Token));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }

        (byte Incremental, int Width, int Height)[] observed;
        lock (requests)
        {
            observed = requests.ToArray();
        }

        Assert.Equal(2, observed.Length);
        Assert.Equal(0, observed[0].Incremental);
        Assert.Equal(0, observed[1].Incremental);
        Assert.Equal(w2, observed[1].Width);
        Assert.Equal(h2, observed[1].Height);
        Assert.Equal(1, client.FullResyncCount);
        Assert.Equal(0, client.IntegrityFaultCount);
    }
}
