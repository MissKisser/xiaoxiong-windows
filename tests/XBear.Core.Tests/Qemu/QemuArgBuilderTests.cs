using System.Text.Json;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Qemu;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Qemu;

/// <summary>参数生成与端口暴露安全规则的单元测试。</summary>
public sealed class QemuArgBuilderTests
{
    private const string DiskPath = @"D:\xbear\instance-1\overlay.qcow2";
    private const string KernelImagePath = @"D:\xbear\boot\vmlinuz";

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

    private static void SetIdentity(
        InstanceSpec spec,
        string? serialNo = "XBSN0123456789AB",
        string? androidId = "a3f9c2e17b8d4056",
        string? imei = "861234567890123")
    {
        spec.DeviceIdentity = new DeviceIdentity
        {
            SerialNo = serialNo,
            AndroidId = androidId,
            Imei = imei,
        };
    }

    /// <summary>构造仅携带指定 P2 系统可写实测结论的镜像清单。</summary>
    private static ImageSpec CreateImage(VerificationState systemWrite)
        => new()
        {
            Id = "bliss-os-17-x86_64",
            Verified = new ImageVerification { Fidelity = new FidelitySet { P2SystemWrite = systemWrite } },
        };    /// <summary>把 platformConfig 逃生舱装配为 JSON 对象。</summary>
    private static void SetPlatformConfig(InstanceSpec spec, IDictionary<string, string?> values)
    {
        string payload = "{" + string.Join(
            ",",
            values.Select(pair =>
                $"\"{pair.Key}\":{(pair.Value is null ? "null" : JsonSerializer.Serialize(pair.Value))}")) + "}";

        spec.PlatformConfig = JsonSerializer.Deserialize<PlatformConfig>(payload, SpecLoader.SerializerOptions);
    }

    private static string ReadValue(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        Assert.True(index >= 0, $"参数中缺少 {flag}：{string.Join(" ", arguments)}");
        Assert.True(index + 1 < arguments.Count, $"{flag} 之后缺少取值。");
        return arguments[index + 1];
    }

    /// <summary>取内核引导命令行，未下发该参数时返回 null。</summary>
    private static string? ReadKernelCommandLine(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().IndexOf("-append");
        return index < 0 ? null : arguments[index + 1];
    }

    /// <summary>取序列号对应的 SMBIOS 条目，未下发该参数时返回 null。</summary>
    private static string? ReadSmbiosSerialEntry(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().IndexOf("-smbios");
        return index < 0 ? null : arguments[index + 1];
    }

    /// <summary>构造已声明内核镜像的实例，作为内核引导模式的最小前置。</summary>
    private static InstanceSpec CreateKernelBootSpec(
        string? initrdImage = null,
        string? kernelAppend = null,
        bool declareKernelAppend = true)
    {
        var spec = CreateSpec();

        var platform = new PlatformConfig
        {
            KernelImage = KernelImagePath,
            InitrdImage = initrdImage,
            KernelAppend = declareKernelAppend ? kernelAppend : null,
        };

        spec.PlatformConfig = platform;
        return spec;
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

        Assert.Contains("virtio-gpu-pci", arguments);
        Assert.Contains("virtio-keyboard-pci", arguments);
        Assert.Contains("virtio-mouse-pci", arguments);
        Assert.Contains("virtio-tablet-pci", arguments);
        Assert.Contains("virtio-net-pci,netdev=net0", arguments);
    }

    /// <summary>
    /// 回归测试：if=virtio 的 qcow2 启动盘在 SeaBIOS 下没有引导驱动，实例永远黑屏。
    /// 启动盘必须经 virtio-scsi 控制器挂载，固件才会去读它。
    /// </summary>
    [Fact]
    public void 启动盘经virtioScsi控制器挂载而不是裸virtio()
    {
        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        string drive = ReadValue(arguments, "-drive");
        Assert.Contains($"if=none,id={QemuArgBuilder.SystemDriveId}", drive, StringComparison.Ordinal);
        Assert.Contains("format=qcow2", drive, StringComparison.Ordinal);
        Assert.DoesNotContain("if=virtio", drive, StringComparison.Ordinal);

        Assert.Contains($"virtio-scsi-pci,id={QemuArgBuilder.ScsiControllerId}", arguments);
        Assert.Contains(
            $"scsi-hd,drive={QemuArgBuilder.SystemDriveId},bus={QemuArgBuilder.ScsiControllerId}.0",
            arguments);
    }

    /// <summary>
    /// 回归测试：裸 -accel whpx 在部分宿主上会导致客户机 CPU 完全不执行，
    /// 缺省必须带上关闭内核 irqchip 直通的选项。
    /// </summary>
    [Fact]
    public void 默认加速器为Whpx且关闭内核IrqChip()
    {
        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            $"{AcceleratorOptions.WhpxAccelerator},{AcceleratorOptions.DefaultWhpxOptions}",
            ReadValue(arguments, "-accel"));
    }

    [Fact]
    public void 宿主级加速器参数可覆盖缺省()
    {
        var builder = new QemuArgBuilder(new AcceleratorOptions
        {
            Accelerator = "tcg",
            Options = "thread=multi",
        });

        var arguments = builder.BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal("tcg,thread=multi", ReadValue(arguments, "-accel"));
    }

    [Fact]
    public void 宿主级加速器可声明不带任何附加选项()
    {
        var builder = new QemuArgBuilder(new AcceleratorOptions { Options = string.Empty });

        var arguments = builder.BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(AcceleratorOptions.WhpxAccelerator, ReadValue(arguments, "-accel"));
    }

    [Fact]
    public void 实例可覆盖加速器名称()
    {
        var spec = CreateSpec();
        SetPlatformConfig(spec, new Dictionary<string, string?> { [QemuArgBuilder.AcceleratorConfigKey] = "tcg" });

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal($"tcg,{AcceleratorOptions.DefaultWhpxOptions}", ReadValue(arguments, "-accel"));
    }

    /// <summary>实例显式置空附加选项时整条替换，而不是回落到宿主缺省。</summary>
    [Fact]
    public void 实例可清空加速器附加选项()
    {
        var spec = CreateSpec();
        SetPlatformConfig(spec, new Dictionary<string, string?>
        {
            [QemuArgBuilder.AcceleratorOptionsConfigKey] = string.Empty,
        });

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(AcceleratorOptions.WhpxAccelerator, ReadValue(arguments, "-accel"));
    }

    [Theory]
    [InlineData("whpx -enable-kvm")]
    [InlineData("whpx\"")]
    [InlineData("whpx;rm")]
    public void 加速器声明含非法字符时抛出规格错误(string accelerator)
    {
        var spec = CreateSpec();
        SetPlatformConfig(spec, new Dictionary<string, string?> { [QemuArgBuilder.AcceleratorConfigKey] = accelerator });

        var exception = Assert.Throws<XBearException>(() =>
            CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900)));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
    }

    [Fact]
    public void 加速器附加选项含非法字符时抛出规格错误()
    {
        var spec = CreateSpec();
        SetPlatformConfig(spec, new Dictionary<string, string?>
        {
            [QemuArgBuilder.AcceleratorOptionsConfigKey] = "kernel-irqchip=off -no-reboot",
        });

        var exception = Assert.Throws<XBearException>(() =>
            CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900)));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
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

    /// <summary>取 -drive 参数中 file= 取值部分，不含尾部固定的 if、id 与 format 段。</summary>
    private static string ReadDriveFileValue(IReadOnlyList<string> arguments)
    {
        const string prefix = "file=";
        var suffix = $",if=none,id={QemuArgBuilder.SystemDriveId},format=qcow2";

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
    /// 之后的取值全部交给 get_opt_value，因此值内的等号是字面量、不作任何转义。
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
            $@"file={DiskPath},if=none,id={QemuArgBuilder.SystemDriveId},format=qcow2",
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
            $@"file=D:\xbear,,odd\overlay.qcow2,if=none,id={QemuArgBuilder.SystemDriveId},format=qcow2",
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
            $@"file=D:\xbear\odd\dir\a=b.qcow2,if=none,id={QemuArgBuilder.SystemDriveId},format=qcow2",
            ReadValue(arguments, "-drive"));
        Assert.Equal(
            "user,id=net0,hostfwd=tcp:127.0.0.1:5555-:5555",
            ReadNetDev(arguments));
    }

    /// <summary>
    /// 黄金向量：磁盘路径同时含逗号与等号。
    /// 依据 get_opt_value 只重复逗号、依据 get_opt_name_value 取值内的等号为字面量，
    /// 因此逗号被重复而等于号保持单个。
    /// </summary>
    [Fact]
    public void 黄金向量_磁盘路径同时含逗号与等号()
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(), @"D:\xbear,odd\dir\a=b.qcow2", new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            $@"file=D:\xbear,,odd\dir\a=b.qcow2,if=none,id={QemuArgBuilder.SystemDriveId},format=qcow2",
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
            $@"file={DiskPath},if=none,id={QemuArgBuilder.SystemDriveId},format=qcow2",
            ReadValue(arguments, "-drive"));
        Assert.Equal(
            "user,id=net0,net=10.0.2.15/24,hostfwd=tcp:0.0.0.0:6000-:6001,hostfwd=tcp:0.0.0.0:15555-:5555",
            ReadNetDev(arguments));
    }

    /// <summary>
    /// 黄金向量：磁盘引导、不带任何标识与镜像保真度结论的实例参数序列。
    /// 该形态是产品默认路径，整条序列必须与修复后的启动形态逐项对齐。
    /// </summary>
    [Fact]
    public void 黄金向量_磁盘引导默认形态的完整参数序列()
    {
        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            new[]
            {
                "-accel",
                $"{AcceleratorOptions.WhpxAccelerator},{AcceleratorOptions.DefaultWhpxOptions}",
                "-cpu",
                "host",
                "-smp",
                "4",
                "-m",
                "4096",
                "-drive",
                $@"file={DiskPath},if=none,id={QemuArgBuilder.SystemDriveId},format=qcow2",
                "-device",
                $"virtio-scsi-pci,id={QemuArgBuilder.ScsiControllerId}",
                "-device",
                $"scsi-hd,drive={QemuArgBuilder.SystemDriveId},bus={QemuArgBuilder.ScsiControllerId}.0",
                "-boot",
                "menu=off",
                "-no-reboot",
                "-display",
                "none",
                "-qmp",
                "tcp:127.0.0.1:5556,server=on,wait=off",
                "-vnc",
                "127.0.0.1:0",
                "-netdev",
                "user,id=net0,hostfwd=tcp:127.0.0.1:5555-:5555",
                "-device",
                "virtio-net-pci,netdev=net0",
                "-device",
                "virtio-gpu-pci",
                "-device",
                "virtio-keyboard-pci",
                "-device",
                "virtio-mouse-pci",
                "-device",
                "virtio-tablet-pci",
            },
            arguments);
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
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(exposure), DiskPath, new AllocatedPorts(5555, 5556, 5901));

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

    [Fact]
    public void 实例声明cpuModel时透传给cpu参数()
    {
        var spec = CreateSpec();
        spec.Resources.CpuModel = "qemu64";

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal("qemu64", ReadValue(arguments, "-cpu"));
    }

    [Fact]
    public void 未声明cpuModel时回退到宿主cpu直通()
    {
        var spec = CreateSpec();
        spec.Resources.CpuModel = null;

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(QemuArgBuilder.DefaultCpuModel, ReadValue(arguments, "-cpu"));
        Assert.Equal("host", ReadValue(arguments, "-cpu"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空白cpuModel回退到宿主cpu直通(string cpuModel)
    {
        var spec = CreateSpec();
        spec.Resources.CpuModel = cpuModel;

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(QemuArgBuilder.DefaultCpuModel, ReadValue(arguments, "-cpu"));
    }

    [Fact]
    public void cpuModel两侧空白被裁剪后下发()
    {
        var spec = CreateSpec();
        spec.Resources.CpuModel = "  max  ";

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal("max", ReadValue(arguments, "-cpu"));
    }

    /// <summary>CPU 型号允许带 QEMU 的特性开关后缀，取型号与开关用逗号连接。</summary>
    [Fact]
    public void cpuModel保留型号与特性开关的组合写法()
    {
        var spec = CreateSpec();
        spec.Resources.CpuModel = "qemu64,+ssse3";

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal("qemu64,+ssse3", ReadValue(arguments, "-cpu"));
    }

    /// <summary>
    /// 回归测试：型号一旦掺入空白或引号就会被命令行解析拆成额外参数，
    /// 必须在生成阶段拒绝，避免出现实际生效的 cpu 参数并非实例声明值的情况。
    /// </summary>
    [Theory]
    [InlineData("host -enable-kvm")]
    [InlineData("host\"")]
    [InlineData("host;rm")]
    public void cpuModel含非法字符时抛出规格错误(string cpuModel)
    {
        var spec = CreateSpec();
        spec.Resources.CpuModel = cpuModel;

        var exception = Assert.Throws<XBearException>(() =>
            CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900)));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
    }

    /// <summary>
    /// 回归测试：产品正常路径创建实例必然生成标识，
    /// 而磁盘引导没有 -kernel，QEMU 会以「-append only allowed with -kernel option」直接拒绝启动。
    /// 因此携带标识的磁盘引导实例必须改走 SMBIOS 序列号通道，且不得产出 -append。
    /// </summary>
    [Fact]
    public void 携带标识的磁盘引导实例下发smbios序列号且不产出append()
    {
        var spec = CreateSpec();
        SetIdentity(spec);

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal("type=1,serial=XBSN0123456789AB", ReadSmbiosSerialEntry(arguments));
        Assert.Null(ReadKernelCommandLine(arguments));
        Assert.DoesNotContain("-append", arguments);
        Assert.DoesNotContain("-kernel", arguments);
    }

    /// <summary>
    /// 回归测试：androidId 与 imei 在磁盘引导模式下没有标准下发通道，
    /// 不得为了「看起来标识已下发」而借 -append 硬塞，那会让整个实例起不来。
    /// </summary>
    [Fact]
    public void 磁盘引导下不按标识字段逐项下发命令行参数()
    {
        var spec = CreateSpec();
        SetIdentity(spec, serialNo: null);

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Null(ReadSmbiosSerialEntry(arguments));
        Assert.Null(ReadKernelCommandLine(arguments));
    }

    [Fact]
    public void 标识字段全空时不生成任何标识参数()
    {
        var spec = CreateSpec();
        SetIdentity(spec, serialNo: null, androidId: null, imei: null);

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Null(ReadSmbiosSerialEntry(arguments));
        Assert.Null(ReadKernelCommandLine(arguments));
    }

    [Fact]
    public void 不携带标识的实例不下发smbios()
    {
        var arguments = CreateBuilder().BuildStartArguments(CreateSpec(), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Null(ReadSmbiosSerialEntry(arguments));
    }

    [Theory]
    [InlineData("XBSN 0001")]
    [InlineData("XBSN,0001")]
    [InlineData("XBSN=0001")]
    [InlineData("a3f9c2e1 78b8d4056")]
    [InlineData("8612 34567890123")]
    public void 标识含非法字符时抛出规格错误(string illegalValue)
    {
        foreach (var configure in new Action<InstanceSpec>[]
                 {
                     spec => SetIdentity(spec, serialNo: illegalValue),
                     spec => SetIdentity(spec, androidId: illegalValue),
                     spec => SetIdentity(spec, imei: illegalValue),
                 })
        {
            var spec = CreateSpec();
            configure(spec);

            var exception = Assert.Throws<XBearException>(() =>
                CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900)));

            Assert.Equal(ErrorCategory.Spec, exception.Category);
        }
    }

    [Fact]
    public void 序列号两侧空白被裁剪后下发()
    {
        var spec = CreateSpec();
        SetIdentity(spec, serialNo: "  XBSN0123456789AB  ", androidId: null, imei: null);

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal("type=1,serial=XBSN0123456789AB", ReadSmbiosSerialEntry(arguments));
    }

    /// <summary>显式配置内核镜像后切到内核引导模式，才允许下发 -append。</summary>
    [Fact]
    public void 配置内核镜像后按内核引导模式下发引导命令行()
    {
        var spec = CreateSpec();
        SetIdentity(spec);
        SetPlatformConfig(spec, new Dictionary<string, string?>
        {
            [QemuArgBuilder.KernelImageConfigKey] = KernelImagePath,
        });

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Pass), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(KernelImagePath, ReadValue(arguments, "-kernel"));
        Assert.Equal(
            QemuArgBuilder.DefaultKernelAppendSkeleton
            + " androidboot.serialno=XBSN0123456789AB"
            + " androidboot.android_id=a3f9c2e17b8d4056"
            + " androidboot.imei=861234567890123"
            + " androidboot.writable_system=1",
            ReadKernelCommandLine(arguments));
    }

    /// <summary>内核引导模式下未声明命令行骨架且无可下发标识时下发默认骨架。</summary>
    [Fact]
    public void 内核引导模式下未声明骨架且无标识时下发默认骨架()
    {
        var spec = CreateSpec();
        SetPlatformConfig(spec, new Dictionary<string, string?>
        {
            [QemuArgBuilder.KernelImageConfigKey] = KernelImagePath,
        });

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Untested), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(KernelImagePath, ReadValue(arguments, "-kernel"));
        Assert.Equal(QemuArgBuilder.DefaultKernelAppendSkeleton, ReadKernelCommandLine(arguments));
    }

    /// <summary>内核引导模式下显式置空骨架且无可下发内容时省略 -append，但 -kernel 仍然保留。</summary>
    [Fact]
    public void 内核引导模式下显式置空骨架且无可下发内容时省略append()
    {
        var spec = CreateSpec();
        SetPlatformConfig(spec, new Dictionary<string, string?>
        {
            [QemuArgBuilder.KernelImageConfigKey] = KernelImagePath,
            [QemuArgBuilder.KernelAppendConfigKey] = string.Empty,
        });

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Untested), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(KernelImagePath, ReadValue(arguments, "-kernel"));
        Assert.Null(ReadKernelCommandLine(arguments));
    }

    /// <summary>空白内核镜像声明按未配置处理，不得产出空路径的 -kernel。</summary>
    [Fact]
    public void 空白内核镜像声明按磁盘引导处理()
    {
        var spec = CreateSpec();
        SetPlatformConfig(spec, new Dictionary<string, string?>
        {
            [QemuArgBuilder.KernelImageConfigKey] = "   ",
        });

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.DoesNotContain("-kernel", arguments);
        Assert.DoesNotContain("-append", arguments);
    }

    [Fact]
    public void 内核镜像路径含非法字符时抛出规格错误()
    {
        var spec = CreateSpec();
        SetPlatformConfig(spec, new Dictionary<string, string?>
        {
            [QemuArgBuilder.KernelImageConfigKey] = KernelImagePath + " -append ro",
        });

        var exception = Assert.Throws<XBearException>(() =>
            CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900)));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
    }

    /// <summary>
    /// 回归测试：androidboot.writable_system 与标识同属内核命令行，
    /// 磁盘引导下产出它同样会被 QEMU 拒绝启动。
    /// </summary>
    [Theory]
    [InlineData(VerificationState.Untested)]
    [InlineData(VerificationState.Fail)]
    [InlineData(VerificationState.Pass)]
    public void 磁盘引导下不因镜像可写结论产出引导命令行(VerificationState state)
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(), CreateImage(state), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Null(ReadKernelCommandLine(arguments));
        Assert.DoesNotContain("-append", arguments);
    }

    [Fact]
    public void 未提供镜像清单时不追加可写引导参数()
    {
        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(), image: null, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Null(ReadKernelCommandLine(arguments));
    }

    [Fact]
    public void 镜像未提供保真度结论时不追加可写引导参数()
    {
        var image = new ImageSpec { Id = "bliss-os-17-x86_64", Verified = new ImageVerification() };

        var arguments = CreateBuilder()
            .BuildStartArguments(CreateSpec(), image, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Null(ReadKernelCommandLine(arguments));
    }

    /// <summary>
    /// 黄金向量：内核直启的最小可引导形态。
    /// 内核、初始 ramdisk 与命令行骨架三项齐全时逐项对齐已实测通过的参数。
    /// </summary>
    [Fact]
    public void 黄金向量_内核直启最小可引导形态()
    {
        var spec = CreateKernelBootSpec(
            initrdImage: @"D:\xbear\boot\initrd.img",
            kernelAppend: "root=/dev/ram0 quiet nomodeset");

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Untested), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(KernelImagePath, ReadValue(arguments, "-kernel"));
        Assert.Equal(@"D:\xbear\boot\initrd.img", ReadValue(arguments, "-initrd"));
        Assert.Equal("root=/dev/ram0 quiet nomodeset", ReadKernelCommandLine(arguments));
        Assert.Equal(
            new[] { "-kernel", "-initrd", "-append" },
            arguments.Where(a => a is "-kernel" or "-initrd" or "-append").ToArray());
    }

    /// <summary>声明初始 ramdisk 后才产出 -initrd，未声明时整条省略。</summary>
    [Fact]
    public void 声明初始ramdisk时产出initrd参数()
    {
        var spec = CreateKernelBootSpec(initrdImage: @"D:\xbear\boot\initrd.img");

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Untested), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(@"D:\xbear\boot\initrd.img", ReadValue(arguments, "-initrd"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 未声明或空白初始ramdisk时省略initrd参数(string? initrdImage)
    {
        var spec = CreateKernelBootSpec(initrdImage: initrdImage);

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Untested), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.DoesNotContain("-initrd", arguments);
    }

    /// <summary>
    /// 回归测试：ramdisk 路径掺入空白或引号会被命令行解析拆成额外参数，
    /// 必须在生成阶段拒绝，而不是原样透传。
    /// </summary>
    [Theory]
    [InlineData(@"D:\xbear\boot\initrd.img -append ro")]
    [InlineData("\"D:\\xbear\\boot\\initrd.img\"")]
    public void 初始ramdisk路径含非法字符时抛出规格错误(string initrdImage)
    {
        var spec = CreateKernelBootSpec(initrdImage: initrdImage);

        var exception = Assert.Throws<XBearException>(() =>
            CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900)));

        Assert.Equal(ErrorCategory.Spec, exception.Category);
        Assert.False(string.IsNullOrWhiteSpace(exception.Remediation));
    }

    [Fact]
    public void 初始ramdisk路径两侧空白被裁剪后下发()
    {
        var spec = CreateKernelBootSpec(initrdImage: @"  D:\xbear\boot\initrd.img  ");

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Untested), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(@"D:\xbear\boot\initrd.img", ReadValue(arguments, "-initrd"));
    }

    /// <summary>磁盘引导模式下 -initrd 与 -append 同属内核引导参数，一律不得产出。</summary>
    [Fact]
    public void 磁盘引导下不产出initrd参数()
    {
        var spec = CreateSpec();
        spec.PlatformConfig = new PlatformConfig { InitrdImage = @"D:\xbear\boot\initrd.img" };

        var arguments = CreateBuilder().BuildStartArguments(spec, DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.DoesNotContain("-initrd", arguments);
        Assert.DoesNotContain("-append", arguments);
    }

    /// <summary>
    /// 回归测试：命令行骨架必须先于标识片段下发，
    /// 否则骨架中的 root 与 nomodeset 会被标识片段挤到后面而丢失引导语义。
    /// </summary>
    [Fact]
    public void 显式骨架覆盖默认值且标识片段追加其后()
    {
        var spec = CreateKernelBootSpec(kernelAppend: "root=/dev/vda1 console=ttyS0 nomodeset HWACCEL=0");
        SetIdentity(spec);

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Pass), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            "root=/dev/vda1 console=ttyS0 nomodeset HWACCEL=0"
            + " androidboot.serialno=XBSN0123456789AB"
            + " androidboot.android_id=a3f9c2e17b8d4056"
            + " androidboot.imei=861234567890123"
            + " androidboot.writable_system=1",
            ReadKernelCommandLine(arguments));
    }

    [Fact]
    public void 显式骨架两侧空白被裁剪后下发()
    {
        var spec = CreateKernelBootSpec(kernelAppend: "  root=/dev/ram0 quiet nomodeset  ");

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Untested), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal("root=/dev/ram0 quiet nomodeset", ReadKernelCommandLine(arguments));
    }

    /// <summary>
    /// 默认骨架是直启可引导的底线：缺 nomodeset 会退化为黑屏，
    /// 缺 root 参数则无法挂载根文件系统，两者任一缺失都不得作为缺省值。
    /// </summary>
    [Fact]
    public void 默认骨架包含可引导的根参数与禁用模式切换()
    {
        var skeleton = QemuArgBuilder.DefaultKernelAppendSkeleton;

        Assert.Contains("root=", skeleton, StringComparison.Ordinal);
        Assert.Contains("nomodeset", skeleton, StringComparison.Ordinal);
        Assert.Equal("root=/dev/ram0 quiet nomodeset", skeleton);
    }

    /// <summary>内核引导模式下，镜像声明系统可写实测通过时追加可写开关到骨架后。</summary>
    [Fact]
    public void 内核引导模式下镜像声明系统可写时追加可写引导参数()
    {
        var spec = CreateSpec();
        SetPlatformConfig(spec, new Dictionary<string, string?>
        {
            [QemuArgBuilder.KernelImageConfigKey] = KernelImagePath,
        });

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Pass), DiskPath, new AllocatedPorts(5555, 5556, 5900));

        Assert.Equal(
            $"{QemuArgBuilder.DefaultKernelAppendSkeleton} androidboot.writable_system=1",
            ReadKernelCommandLine(arguments));
    }

    [Fact]
    public void 黄金向量_cpuModel与标识同时生效()
    {
        var spec = CreateSpec("lan");
        spec.Resources.CpuModel = "qemu64";
        SetIdentity(spec);

        var arguments = CreateBuilder()
            .BuildStartArguments(spec, CreateImage(VerificationState.Pass), DiskPath, new AllocatedPorts(15555, 15556, 5901));

        Assert.Equal("qemu64", ReadValue(arguments, "-cpu"));
        Assert.Equal("type=1,serial=XBSN0123456789AB", ReadSmbiosSerialEntry(arguments));
        Assert.Null(ReadKernelCommandLine(arguments));
        Assert.Equal("0.0.0.0:1", ReadValue(arguments, "-vnc"));
        Assert.Equal(
            "user,id=net0,hostfwd=tcp:0.0.0.0:15555-:5555",
            ReadNetDev(arguments));
    }

    [Fact]
    public void 参数生成是纯函数且对磁盘引导形态可重复调用()
    {
        var spec = CreateSpec();
        SetIdentity(spec);
        var builder = CreateBuilder();
        var image = CreateImage(VerificationState.Pass);
        var ports = new AllocatedPorts(5555, 5556, 5900);

        var first = builder.BuildStartArguments(spec, image, DiskPath, ports);
        var second = builder.BuildStartArguments(spec, image, DiskPath, ports);

        Assert.Equal(first, second);
    }
}