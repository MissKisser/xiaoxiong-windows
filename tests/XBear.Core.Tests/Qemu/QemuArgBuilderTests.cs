using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Qemu;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Qemu;

/// <summary>参数生成与端口暴露安全规则的单元测试。</summary>
public sealed class QemuArgBuilderTests
{
    private const string DiskPath = @"D:\xbear\instance-1\overlay.qcow2";

    private static QemuArgBuilder CreateBuilder() => new();

    private static InstanceSpec CreateSpec(string? exposure = QemuArgBuilder.LoopbackExposure)
    {
        var spec = new InstanceSpec
        {
            Id = "instance-1",
            Resources = new ResourceSpec { CpuCores = 4, MemoryMB = 4096 },
        };

        if (exposure is not null)
        {
            spec.Network = new NetworkSpec { Exposure = exposure };
        }

        return spec;
    }

    private static void AddForward(InstanceSpec spec, int hostPort, int guestPort, string protocol, string? bind)
    {
        spec.Network ??= new NetworkSpec();
        spec.Network.PortForwards.Add(new PortForward
        {
            HostPort = hostPort,
            GuestPort = guestPort,
            Protocol = protocol,
            Bind = bind,
        });
    }

    private static string ReadValue(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        Assert.True(index >= 0, $"参数中缺少 {flag}：{string.Join(" ", arguments)}");
        Assert.True(index + 1 < arguments.Count, $"{flag} 之后缺少取值。");
        return arguments[index + 1];
    }

    private static string ReadNetDev(IReadOnlyList<string> arguments) => ReadValue(arguments, "-netdev");

    private static string[] ReadHostForwards(IReadOnlyList<string> arguments)
        => ReadNetDev(arguments)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part.StartsWith("hostfwd=", StringComparison.Ordinal))
            .ToArray();

    /// <summary>取用户配置的端口映射，不含内建的 adb 转发。</summary>
    private static string[] ReadUserHostForwards(IReadOnlyList<string> arguments)
        => ReadHostForwards(arguments)
            .Where(part => !part.EndsWith("-:" + QemuArgBuilder.AdbGuestPort, StringComparison.Ordinal))
            .ToArray();

    /// <summary>取内建的 adb 转发。</summary>
    private static string ReadAdbHostForward(IReadOnlyList<string> arguments)
    {
        string? found = ReadHostForwards(arguments)
            .SingleOrDefault(part => part.EndsWith("-:" + QemuArgBuilder.AdbGuestPort, StringComparison.Ordinal));

        Assert.True(found is not null, $"参数中缺少 adb 转发：{string.Join(" ", arguments)}");
        return found;
    }

    /// <summary>取用户配置端口映射的绑定地址，不含内建的 adb 转发。</summary>
    private static string[] ReadBindAddresses(IReadOnlyList<string> arguments)
        => ReadUserHostForwards(arguments)
            .Select(part => part.Split(':')[1])
            .ToArray();

    [Fact]
    public void 基础参数取值正确()
    {
        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal("whpx", ReadValue(arguments, "-accel"));
        Assert.Equal("host", ReadValue(arguments, "-cpu"));
        Assert.Equal("4", ReadValue(arguments, "-smp"));
        Assert.Equal("4096", ReadValue(arguments, "-m"));
        Assert.Equal("none", ReadValue(arguments, "-display"));
        Assert.Equal("menu=off", ReadValue(arguments, "-boot"));
        Assert.Contains("-no-reboot", arguments);
        Assert.Equal("tcp:127.0.0.1:5556,server=on,wait=off", ReadValue(arguments, "-qmp"));
    }

    [Fact]
    public void 磁盘与设备参数覆盖Bliss所需项()
    {
        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Contains($"file={DiskPath},if=virtio,format=qcow2", arguments);
        Assert.Contains("virtio-gpu-pci", arguments);
        Assert.Contains("virtio-keyboard-pci", arguments);
        Assert.Contains("virtio-mouse-pci", arguments);
        Assert.Contains("virtio-tablet-pci", arguments);
        Assert.Contains("virtio-net-pci,netdev=net0", arguments);
    }

    [Theory]
    [InlineData("lan")]
    [InlineData("public")]
    public void 回环暴露下绑定地址强制回环且不被映射绑定突破(string bind)
    {
        var spec = CreateSpec(QemuArgBuilder.LoopbackExposure);
        AddForward(spec, 6000, 6000, "tcp", bind);

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        var binds = ReadBindAddresses(arguments);
        Assert.NotEmpty(binds);
        Assert.All(binds, address => Assert.Equal(QemuArgBuilder.LoopbackBindAddress, address));
    }

    [Fact]
    public void 缺省网络配置时按回环处理()
    {
        var spec = CreateSpec(exposure: null);
        AddForward(spec, 6000, 6000, "tcp", "public");

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.All(ReadBindAddresses(arguments), address => Assert.Equal(QemuArgBuilder.LoopbackBindAddress, address));
    }

    [Fact]
    public void 无法识别的暴露取值回落为回环()
    {
        var spec = CreateSpec("lan");
        AddForward(spec, 6000, 6000, "tcp", "not-a-level");

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.All(ReadBindAddresses(arguments), address => Assert.Equal(QemuArgBuilder.LoopbackBindAddress, address));
    }

    [Theory]
    [InlineData("lan")]
    [InlineData("public")]
    public void 局域网与公网暴露允许任意地址(string exposure)
    {
        var spec = CreateSpec(exposure);
        AddForward(spec, 6000, 6000, "tcp", null);

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.All(ReadBindAddresses(arguments), address => Assert.Equal(QemuArgBuilder.AnyBindAddress, address));
    }

    [Fact]
    public void 映射自身声明回环时只会下调暴露面()
    {
        var spec = CreateSpec("lan");
        AddForward(spec, 6000, 6000, "tcp", "loopback");

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.All(ReadBindAddresses(arguments), address => Assert.Equal(QemuArgBuilder.LoopbackBindAddress, address));
    }

    [Fact]
    public void 同一实例可按条目分别收紧暴露面()
    {
        var spec = CreateSpec("lan");
        AddForward(spec, 6000, 6000, "tcp", "lan");
        AddForward(spec, 6001, 6001, "tcp", "loopback");

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        var binds = ReadUserHostForwards(arguments)
            .Select(part => part.Split(':')[1])
            .ToArray();
        Assert.Equal(2, binds.Length);
        Assert.Equal(QemuArgBuilder.AnyBindAddress, binds[0]);
        Assert.Equal(QemuArgBuilder.LoopbackBindAddress, binds[1]);
    }

    [Fact]
    public void 固定地址输出对应网段且宿主侧仍为用户态NAT()
    {
        var spec = CreateSpec();
        spec.Network!.FixedAddress = "10.0.2.15";

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        var netDev = ReadNetDev(arguments);
        Assert.StartsWith("user,id=net0", netDev, StringComparison.Ordinal);
        Assert.Contains("net=10.0.2.15/24", netDev, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge", netDev, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tap", netDev, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 固定地址不符合约定时抛出规格错误()
    {
        var spec = CreateSpec();
        spec.Network!.FixedAddress = "192.168.1.20";

        var exception = Assert.Throws<XBearException>(() =>
            CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900)));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
    }

    [Fact]
    public void Udp映射生成udp前缀()
    {
        var spec = CreateSpec();
        AddForward(spec, 7000, 7001, "udp", null);

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        var hostForwards = ReadUserHostForwards(arguments);
        Assert.Single(hostForwards);
        Assert.StartsWith("hostfwd=udp:", hostForwards[0], StringComparison.Ordinal);
        Assert.Contains("-:7001", hostForwards[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 不受支持的协议抛出规格错误()
    {
        var spec = CreateSpec();
        AddForward(spec, 7000, 7000, "sctp", null);

        var exception = Assert.Throws<XBearException>(() =>
            CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900)));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
    }

    [Fact]
    public void 参数生成是纯函数可重复调用()
    {
        var spec = CreateSpec("lan");
        AddForward(spec, 6000, 6000, "tcp", null);
        var builder = CreateBuilder();
        var ports = new AllocatedPorts(5555, 5556, 5900);

        var first = builder.BuildStartArguments(spec, DiskPath, ports);
        var second = builder.BuildStartArguments(spec, DiskPath, ports);

        Assert.Equal(first, second);
    }

    [Fact]
    public void 资源配额越界时抛出规格错误()
    {
        var spec = CreateSpec();
        spec.Resources = new ResourceSpec { CpuCores = 0, MemoryMB = 4096 };

        var exception = Assert.Throws<XBearException>(() =>
            CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900)));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
    }

    /// <summary>取 -drive 参数中 file= 取值部分，不含尾部固定的 if 与 format 段。</summary>
    private static string ReadDriveFileValue(IReadOnlyList<string> arguments)
    {
        const string prefix = "file=";
        const string suffix = ",if=virtio,format=qcow2";

        var drive = ReadValue(arguments, "-drive");
        Assert.StartsWith(prefix, drive, StringComparison.Ordinal);
        Assert.EndsWith(suffix, drive, StringComparison.Ordinal);

        var length = drive.Length - prefix.Length - suffix.Length;
        return drive.Substring(prefix.Length, length);
    }

    /// <summary>
    /// 磁盘路径里的逗号与等号按 QEMU keyval 规则处理。
    /// QEMU 的 get_opt_value 只处理逗号：值内出现逗号时必须重复输出一个逗号，
    /// 否则被当成新选项的分隔符。get_opt_name_value 用 strcspn(params, "=,")
    /// 把选项名截到第一个等号或逗号，随后恰好跳过一个等号，
    /// 之后的取值全部交给 get_opt_value，因此值内的等号是纯字面量、不重复。
    /// </summary>
    [Theory]
    [InlineData(@"D:\xbear,odd\overlay.qcow2", @"D:\xbear,,odd\overlay.qcow2")]
    [InlineData(@"D:\xbear\odd\a=b.qcow2", @"D:\xbear\odd\a=b.qcow2")]
    [InlineData(@"D:\xbear,odd\dir\a=b.qcow2", @"D:\xbear,,odd\dir\a=b.qcow2")]
    [InlineData(@"D:\xbear\instance-1\overlay.qcow2", @"D:\xbear\instance-1\overlay.qcow2")]
    public void 磁盘路径转义只重复逗号且等号保持单个(string diskPath, string expectedFileValue)
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(), diskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(expectedFileValue, ReadDriveFileValue(arguments));
    }

    /// <summary>逗号被重复后不得连带产生重复等号，否则 QEMU 会取到不存在的路径。</summary>
    [Fact]
    public void 磁盘路径含等号时等号不被重复()
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(), @"D:\xbear\odd\dir\a=b.qcow2", new AllocatedPorts(5555, 5556, 5900));

        Assert.DoesNotContain("==", ReadValue(arguments, "-drive"), StringComparison.Ordinal);
    }

    /// <summary>
    /// 黄金向量：不含分隔符的磁盘路径，回环暴露且无端口映射。
    /// 依据 QEMU get_opt_name_value，file 取值在第一个逗号处结束，等号原样保留；
    /// 依据 get_opt_value，值内不含逗号时无需重复。
    /// </summary>
    [Fact]
    public void 黄金向量_无分隔符磁盘路径与回环网络()
    {
        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            @"file=D:\xbear\instance-1\overlay.qcow2,if=virtio,format=qcow2",
            ReadValue(arguments, "-drive"));
        Assert.Equal(
            "user,id=net0,hostfwd=tcp:127.0.0.1:5555-:5555",
            ReadNetDev(arguments));
    }

    /// <summary>
    /// 黄金向量：磁盘路径含逗号。
    /// 依据 QEMU get_opt_value 的注释，值内逗号必须重复输出一个逗号，
    /// 否则逗号会被读成新选项的分隔符，取值在逗号处被截断。
    /// </summary>
    [Fact]
    public void 黄金向量_磁盘路径含逗号时重复逗号()
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(), @"D:\xbear,odd\overlay.qcow2", new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            @"file=D:\xbear,,odd\overlay.qcow2,if=virtio,format=qcow2",
            ReadValue(arguments, "-drive"));
        Assert.Equal(
            "user,id=net0,hostfwd=tcp:127.0.0.1:5555-:5555",
            ReadNetDev(arguments));
    }

    /// <summary>
    /// 黄金向量：磁盘路径含等号。
    /// 依据 QEMU get_opt_name_value，选项名之后的取值由 get_opt_value 读取，
    /// 而 get_opt_value 只重复逗号、不处理等号，所以等于号必须保持单个，
    /// 重复等号会让 QEMU 取到 a==b 这样不存在的文件名。
    /// </summary>
    [Fact]
    public void 黄金向量_磁盘路径含等号时等号保持单个()
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(), @"D:\xbear\odd\dir\a=b.qcow2", new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            @"file=D:\xbear\odd\dir\a=b.qcow2,if=virtio,format=qcow2",
            ReadValue(arguments, "-drive"));
        Assert.Equal(
            "user,id=net0,hostfwd=tcp:127.0.0.1:5555-:5555",
            ReadNetDev(arguments));
    }

    /// <summary>
    /// 黄金向量：磁盘路径同时含逗号与等号。
    /// 依据 QEMU get_opt_value 只重复逗号、依据 get_opt_name_value 取值内的等号为字面量，
    /// 因此逗号被重复而等于号保持单个。
    /// </summary>
    [Fact]
    public void 黄金向量_磁盘路径同时含逗号与等号()
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(), @"D:\xbear,odd\dir\a=b.qcow2", new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            @"file=D:\xbear,,odd\dir\a=b.qcow2,if=virtio,format=qcow2",
            ReadValue(arguments, "-drive"));
        Assert.Equal(
            "user,id=net0,hostfwd=tcp:127.0.0.1:5555-:5555",
            ReadNetDev(arguments));
    }

    /// <summary>
    /// 黄金向量：固定地址、用户端口映射与内建 adb 转发共存。
    /// -netdev 取值以逗号分隔各个子选项，固定地址与各条 hostfwd 的取值内不含逗号，
    /// 因此整体拼接后可直接交由 QEMU get_opt_value 逐段解析，无需额外重复。
    /// </summary>
    [Fact]
    public void 黄金向量_固定地址与端口映射共存()
    {
        var spec = CreateSpec("lan");
        spec.Network!.FixedAddress = "10.0.2.15";
        AddForward(spec, 6000, 6001, "tcp", null);

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(15555, 15556, 5901));

        Assert.Equal(
            @"file=D:\xbear\instance-1\overlay.qcow2,if=virtio,format=qcow2",
            ReadValue(arguments, "-drive"));
        Assert.Equal(
            "user,id=net0,net=10.0.2.15/24,hostfwd=tcp:0.0.0.0:6000-:6001,hostfwd=tcp:0.0.0.0:15555-:5555",
            ReadNetDev(arguments));
    }

    [Fact]
    public void 回环暴露下adb转发绑定回环且指向guest固定端口()
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            $"hostfwd=tcp:{QemuArgBuilder.LoopbackBindAddress}:5555-:{QemuArgBuilder.AdbGuestPort}",
            ReadAdbHostForward(arguments));
    }

    [Theory]
    [InlineData("lan")]
    [InlineData("public")]
    public void 局域网与公网暴露下adb转发绑定任意地址(string exposure)
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(exposure), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            $"hostfwd=tcp:{QemuArgBuilder.AnyBindAddress}:5555-:{QemuArgBuilder.AdbGuestPort}",
            ReadAdbHostForward(arguments));
    }

    [Fact]
    public void adb转发取自分配端口而非固定取值()
    {
        var ports = new AllocatedPorts(15555, 15556, 5903);

        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, ports);

        Assert.Equal(
            $"hostfwd=tcp:{QemuArgBuilder.LoopbackBindAddress}:{ports.Adb}-:{QemuArgBuilder.AdbGuestPort}",
            ReadAdbHostForward(arguments));
        Assert.DoesNotContain("hostfwd=tcp:127.0.0.1:5555-", ReadNetDev(arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void 未配置任何端口映射时仍生成adb转发()
    {
        var spec = CreateSpec();
        spec.Network!.PortForwards.Clear();

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            $"hostfwd=tcp:{QemuArgBuilder.LoopbackBindAddress}:5555-:{QemuArgBuilder.AdbGuestPort}",
            ReadAdbHostForward(arguments));
    }

    [Fact]
    public void 缺省网络配置时adb转发同样绑定回环()
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(exposure: null), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            $"hostfwd=tcp:{QemuArgBuilder.LoopbackBindAddress}:5555-:{QemuArgBuilder.AdbGuestPort}",
            ReadAdbHostForward(arguments));
    }

    [Fact]
    public void 用户端口映射与adb转发共存()
    {
        var spec = CreateSpec("lan");
        AddForward(spec, 6000, 6000, "tcp", null);

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(15555, 15556, 5900));

        Assert.Equal(2, ReadHostForwards(arguments).Length);
        Assert.Equal(
            $"hostfwd=tcp:{QemuArgBuilder.AnyBindAddress}:15555-:{QemuArgBuilder.AdbGuestPort}",
            ReadAdbHostForward(arguments));
    }

    [Theory]
    [InlineData(5900, 0)]
    [InlineData(5903, 3)]
    public void 回环暴露下vnc显示号绑定回环(int vncPort, int display)
    {
        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, vncPort));

        Assert.Equal(
            $"{QemuArgBuilder.LoopbackBindAddress}:{display}",
            ReadValue(arguments, "-vnc"));
    }

    [Theory]
    [InlineData("lan")]
    [InlineData("public")]
    public void 局域网与公网暴露下vnc显示号绑定任意地址(string exposure)
    {
        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(exposure), DiskPath, new AllocatedPorts(5555, 5556, 5901));

        Assert.Equal(
            $"{QemuArgBuilder.AnyBindAddress}:1",
            ReadValue(arguments, "-vnc"));
    }

    [Fact]
    public void vnc端口低于显示号基准时抛出规格错误()
    {
        var exception = Assert.Throws<XBearException>(() =>
            CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5000)));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
    }

    [Fact]
    public void 禁用本地显示窗口时vnc是唯一生成的可视通道()
    {
        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal("none", ReadValue(arguments, "-display"));
        Assert.Contains("-vnc", arguments);
    }
}