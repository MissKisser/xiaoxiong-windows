using System.Net;
using System.Net.Sockets;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Qemu;

namespace XBear.Core.Tests.Qemu;

/// <summary>端口分配、占用探测与释放行为的单元测试。</summary>
public sealed class PortAllocatorTests
{
    /// <summary>取出三个当前确定空闲的端口，测试期间保持监听以免端口被其他进程抢占。</summary>
    /// <returns>三个互不相同的端口及其监听器。</returns>
    private static (int Adb, int Qmp, int Vnc, TcpListener[] Listeners) HoldThreeFreePorts()
    {
        var listeners = new TcpListener[3];
        var ports = new int[3];

        for (var index = 0; index < listeners.Length; index++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            listeners[index] = listener;
            ports[index] = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        return (ports[0], ports[1], ports[2], listeners);
    }

    /// <summary>
    /// 回归测试：三个端口必须互不相同且保持 adb &lt; qmp &lt; vnc 的次序。
    /// 被测属性是分配结果的形状，而不是具体端口号——
    /// 起点端口被本机其他进程占用时分配器会向后扫描，
    /// 断言死端口号会随环境偶发失败。
    /// </summary>
    [Fact]
    public async Task 分配的三个端口互不相同且递增()
    {
        var allocator = new PortAllocator();
        try
        {
            var ports = await allocator.AcquireAsync("instance-1");

            Assert.True(ports.Adb < ports.Qmp, $"adb {ports.Adb} 应小于 qmp {ports.Qmp}。");
            Assert.True(ports.Qmp < ports.Vnc, $"qmp {ports.Qmp} 应小于 vnc {ports.Vnc}。");
            Assert.True(
                ports.Adb >= PortAllocator.DefaultAdbStartPort,
                $"adb {ports.Adb} 不应早于起点端口 {PortAllocator.DefaultAdbStartPort}。");

            Assert.True(IsPortFree(ports.Adb), $"分配出的端口 {ports.Adb} 应当确实可绑定。");
            Assert.True(IsPortFree(ports.Qmp), $"分配出的端口 {ports.Qmp} 应当确实可绑定。");
            Assert.True(IsPortFree(ports.Vnc), $"分配出的端口 {ports.Vnc} 应当确实可绑定。");
        }
        finally
        {
            allocator.Release("instance-1");
        }
    }

    [Fact]
    public async Task 不同实例分得不同端口且释放后可再分配()
    {
        var allocator = new PortAllocator(21000, 21001, 21002);
        try
        {
            var first = await allocator.AcquireAsync("instance-1");
            var second = await allocator.AcquireAsync("instance-2");

            Assert.NotEqual(first, second);
            Assert.DoesNotContain(first.Adb, new[] { second.Adb, second.Qmp, second.Vnc });
            Assert.DoesNotContain(second.Adb, new[] { first.Adb, first.Qmp, first.Vnc });

            allocator.Release("instance-1");
            var third = await allocator.AcquireAsync("instance-3");

            Assert.Equal(first, third);
        }
        finally
        {
            allocator.Release("instance-1");
            allocator.Release("instance-2");
            allocator.Release("instance-3");
        }
    }

    [Fact]
    public async Task 同一实例重复申请返回同一组端口()
    {
        var allocator = new PortAllocator(22000, 22001, 22002);
        try
        {
            var first = await allocator.AcquireAsync("instance-1");
            var second = await allocator.AcquireAsync("instance-1");

            Assert.Equal(first, second);
        }
        finally
        {
            allocator.Release("instance-1");
        }
    }

    [Fact]
    public async Task 能识别已被占用的端口()
    {
        var (adb, qmp, vnc, listeners) = HoldThreeFreePorts();
        try
        {
            var allocator = new PortAllocator();

            // 监听器持有期间端口应被判定为占用。
            Assert.False(await allocator.AreAvailableAsync(new AllocatedPorts(adb, qmp, vnc)));

            foreach (var listener in listeners)
            {
                listener.Stop();
            }

            // 监听器释放后同一组端口应被判定为空闲。
            Assert.True(await allocator.AreAvailableAsync(new AllocatedPorts(adb, qmp, vnc)));
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }
        }
    }

    /// <summary>
    /// 占用一段连续端口，供「起点被占用」类测试使用。
    /// 从给定区段起向后扫描，返回首个能完整绑定的连续端口块。
    /// </summary>
    /// <param name="count">需要占用的连续端口个数。</param>
    /// <param name="searchFrom">扫描起点，须避开系统动态端口区段。</param>
    /// <returns>端口块起点与对应的监听器数组。</returns>
    /// <exception cref="InvalidOperationException">扫描范围内找不到可绑定的连续端口块时抛出。</exception>
    private static (int Base, TcpListener[] Listeners) HoldContiguousPorts(int count, int searchFrom = 26000)
    {
        for (var basePort = searchFrom; basePort < 30000; basePort++)
        {
            var listeners = new TcpListener[count];
            var bound = 0;

            for (var offset = 0; offset < count; offset++)
            {
                var listener = new TcpListener(IPAddress.Loopback, basePort + offset);
                try
                {
                    listener.Start();
                }
                catch (SocketException)
                {
                    listener.Stop();
                    break;
                }

                listeners[offset] = listener;
                bound++;
            }

            if (bound == count)
            {
                return (basePort, listeners);
            }

            for (var offset = 0; offset < bound; offset++)
            {
                listeners[offset].Stop();
            }
        }

        throw new InvalidOperationException("扫描范围内找不到可绑定的连续端口块。");
    }

    /// <summary>探测指定端口当前是否可绑定。</summary>
    /// <param name="port">待探测端口。</param>
    /// <returns>可绑定返回 true。</returns>
    private static bool IsPortFree(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 占用端口不会重复分配给其他实例()
    {
        var (basePort, listeners) = HoldContiguousPorts(2);

        try
        {
            var allocator = new PortAllocator(basePort, basePort + 10, basePort + 11, maxCandidateOffsets: 16);
            var ports = await allocator.AcquireAsync("instance-1");

            // 被测属性是「已占用端口绝不被返回」，而不是「一定返回哪一个空闲端口」——
            // 后者取决于探测时刻哪些端口恰好空闲，断言具体端口号会随环境偶发失败。
            Assert.NotEqual(basePort, ports.Adb);
            Assert.NotEqual(basePort + 1, ports.Adb);
            Assert.True(IsPortFree(ports.Adb), $"分配出的端口 {ports.Adb} 应当确实可绑定。");

            allocator.Release("instance-1");
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }
        }
    }

    [Fact]
    public async Task 端口耗尽时抛出端口错误()
    {
        var (basePort, listeners) = HoldContiguousPorts(3);

        try
        {
            var allocator = new PortAllocator(basePort, basePort + 1, basePort + 2, maxCandidateOffsets: 1);

            var exception = await Assert.ThrowsAsync<XBearException>(() => allocator.AcquireAsync("instance-1"));

            Assert.Equal(ErrorCategory.Port, exception.Category);
            Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
        }
        finally
        {
            foreach (var listener in listeners)
            {
                listener.Stop();
            }
        }
    }

    [Fact]
    public async Task 并发申请不会分配到重复端口()
    {
        var allocator = new PortAllocator(24000, 24001, 24002);
        const int instanceCount = 8;

        try
        {
            var acquired = await Task.WhenAll(
                Enumerable.Range(0, instanceCount).Select(index => allocator.AcquireAsync($"instance-{index}")));

            var allPorts = acquired
                .SelectMany(ports => new[] { ports.Adb, ports.Qmp, ports.Vnc })
                .ToArray();

            Assert.Equal(instanceCount * 3, allPorts.Length);
            Assert.Equal(allPorts.Length, allPorts.Distinct().Count());
            Assert.Equal(instanceCount, acquired.Distinct().Count());
        }
        finally
        {
            for (var index = 0; index < instanceCount; index++)
            {
                allocator.Release($"instance-{index}");
            }
        }
    }

    [Fact]
    public void 起始端口越界或重复时抛出端口错误()
    {
        Assert.Equal(
            ErrorCategory.Port,
            Assert.Throws<XBearException>(() => new PortAllocator(70000, 5556, 5900)).Category);
        Assert.Equal(
            ErrorCategory.Port,
            Assert.Throws<XBearException>(() => new PortAllocator(5555, 5555, 5900)).Category);
    }
}