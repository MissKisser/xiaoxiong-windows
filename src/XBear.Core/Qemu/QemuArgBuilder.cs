using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Qemu;

/// <summary>
/// 按实例配置生成 QEMU 启动参数，不接触文件系统与进程。
/// </summary>
/// <remarks>
/// 默认走磁盘引导：启动盘以 if=none 挂到 virtio-scsi 控制器上供固件引导，
/// 该模式没有 -kernel，QEMU 会拒绝任何 -append，因此设备标识经 DMI 下发序列号，
/// 镜像保真度相关的 androidboot 参数不在该模式下产出。
/// 实例在 platformConfig 中显式声明 kernelImage 时才切换为内核引导模式。
/// </remarks>
public sealed class QemuArgBuilder : IQemuArgBuilder
{
    /// <summary>回环暴露对应的宿主绑定地址。</summary>
    public const string LoopbackBindAddress = "127.0.0.1";

    /// <summary>局域网与公网暴露对应的宿主绑定地址。</summary>
    public const string AnyBindAddress = "0.0.0.0";

    /// <summary>回环暴露级别标识。</summary>
    public const string LoopbackExposure = "loopback";

    /// <summary>局域网暴露级别标识。</summary>
    public const string LanExposure = "lan";

    /// <summary>公网暴露级别标识。</summary>
    public const string PublicExposure = "public";

    /// <summary>guest 内 adbd 的固定监听端口，作为 adb 转发的目标端口。</summary>
    public const int AdbGuestPort = 5555;

    /// <summary>VNC 显示号的基准端口，显示号为 N 时实际监听该基准加 N。</summary>
    public const int VncDisplayBasePort = 5900;

    /// <summary>实例未声明 CPU 型号时使用的缺省型号，直接透传宿主 CPU 能力。</summary>
    public const string DefaultCpuModel = "host";

    /// <summary>启动盘在 -drive 上的 drive 标识，供 -device scsi-hd 引用。</summary>
    public const string SystemDriveId = "xbsysdisk";

    /// <summary>virtio-scsi 控制器的设备标识。</summary>
    public const string ScsiControllerId = "xbscsi";

    /// <summary>platformConfig 中声明加速器名称的键名。</summary>
    public const string AcceleratorConfigKey = "accelerator";

    /// <summary>platformConfig 中声明加速器附加选项的键名。</summary>
    public const string AcceleratorOptionsConfigKey = "acceleratorOptions";

    /// <summary>platformConfig 中声明内核镜像路径的键名。</summary>
    public const string KernelImageConfigKey = "kernelImage";

    /// <summary>platformConfig 中声明初始 ramdisk 镜像路径的键名。</summary>
    public const string InitrdImageConfigKey = "initrdImage";

    /// <summary>platformConfig 中声明内核命令行骨架的键名。</summary>
    public const string KernelAppendConfigKey = "kernelAppend";

    /// <summary>
    /// 未声明 kernelAppend 时使用的安全默认命令行骨架。
    /// 缺 nomodeset 会退化为无显示输出，缺 root 参数则无法挂载根文件系统，两者都是引导失败。
    /// </summary>
    public const string DefaultKernelAppendSkeleton = "root=/dev/ram0 quiet nomodeset";

    private const string NetworkDeviceId = "net0";
    private const string QemuUserNetPrefix = "user";

    private readonly AcceleratorOptions _accelerator;

    /// <summary>
    /// 构造参数生成器，使用宿主级默认加速器参数。
    /// </summary>
    public QemuArgBuilder()
        : this(null)
    {
    }

    /// <summary>
    /// 构造参数生成器并指定宿主级加速器参数。
    /// </summary>
    /// <param name="accelerator">
    /// 宿主级加速器参数，为 null 时使用 <see cref="AcceleratorOptions.Default"/>。
    /// 实例可通过 platformConfig 覆盖，但只能改名称与附加选项，无法退回更危险的缺省行为。
    /// </param>
    public QemuArgBuilder(AcceleratorOptions? accelerator)
    {
        _accelerator = accelerator ?? AcceleratorOptions.Default;
    }

    /// <summary>CPU 型号允许的字符集，避免取值被拆成额外的 QEMU 参数。</summary>
    private static readonly Regex CpuModelPattern =
        new(@"^[A-Za-z0-9._+,\-=]+$", RegexOptions.CultureInvariant);

    private static readonly Regex FixedAddressPattern =
        new(@"^10\.0\.2\.[0-9]{1,3}$", RegexOptions.CultureInvariant);

    private static readonly string[] BlissDevices =
    {
        "virtio-gpu-pci",
        "virtio-keyboard-pci",
        "virtio-mouse-pci",
        "virtio-tablet-pci",
    };

    /// <summary>生成实例启动参数序列，不含可执行文件路径。</summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="diskPath">实例可写磁盘镜像路径。</param>
    /// <param name="ports">本次分配到的宿主端口。</param>
    /// <returns>可直接拼接为命令行的参数序列。</returns>
    /// <exception cref="XBearException">配置不满足契约时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public IReadOnlyList<string> BuildStartArguments(
        InstanceSpec spec,
        string diskPath,
        AllocatedPorts ports)
        => BuildStartArguments(spec, image: null, diskPath, ports);

    /// <summary>
    /// 生成实例启动参数序列，不含可执行文件路径。
    /// 镜像清单决定保真度相关的引导参数，为空时按镜像未知处理，只下发实例自身的引导参数。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="image">实例引用的镜像清单，可为空，空值不下发镜像保真度相关引导参数。</param>
    /// <param name="diskPath">实例可写磁盘镜像路径。</param>
    /// <param name="ports">本次分配到的宿主端口。</param>
    /// <returns>可直接拼接为命令行的参数序列。</returns>
    /// <exception cref="XBearException">配置不满足契约时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public IReadOnlyList<string> BuildStartArguments(
        InstanceSpec spec,
        ImageSpec? image,
        string diskPath,
        AllocatedPorts ports)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(diskPath))
        {
            throw new XBearException(ErrorCategory.Spec, "实例磁盘路径不能为空。");
        }

        if (spec.Resources.CpuCores < 1)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {spec.Id} 的 cpuCores 必须大于 0，当前为 {spec.Resources.CpuCores}。");
        }

        if (spec.Resources.MemoryMB < 1)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {spec.Id} 的 memoryMB 必须大于 0，当前为 {spec.Resources.MemoryMB}。");
        }

        ValidatePort(ports.Adb, nameof(ports.Adb));
        ValidatePort(ports.Qmp, nameof(ports.Qmp));
        ValidatePort(ports.Vnc, nameof(ports.Vnc));

        var arguments = new List<string>
        {
            "-accel",
            ResolveAccelerator(spec),
            "-cpu",
            ResolveCpuModel(spec),
            "-smp",
            spec.Resources.CpuCores.ToString(CultureInfo.InvariantCulture),
            "-m",
            spec.Resources.MemoryMB.ToString(CultureInfo.InvariantCulture),
            "-drive",
            $"file={EscapeOptionValue(diskPath)},if=none,id={SystemDriveId},format=qcow2",
            "-device",
            $"virtio-scsi-pci,id={ScsiControllerId}",
            "-device",
            $"scsi-hd,drive={SystemDriveId},bus={ScsiControllerId}.0",
            "-boot",
            "menu=off",
            "-no-reboot",
            "-display",
            "none",
            "-qmp",
            $"tcp:{LoopbackBindAddress}:{ports.Qmp},server=on,wait=off",
            "-vnc",
            BuildVncDisplay(spec.Network?.Exposure, ports.Vnc),
        };

        // 磁盘引导没有 -kernel，QEMU 会在启动前直接拒绝 -append，因此标识只经 DMI 下发序列号。
        string? smbiosSerial = DeviceIdentityChannels.BuildSmbiosSerialEntry(spec.DeviceIdentity);
        if (smbiosSerial is not null)
        {
            arguments.Add("-smbios");
            arguments.Add(smbiosSerial);
        }

        AppendKernelBootArguments(spec, image, arguments);

        arguments.Add("-netdev");
        arguments.Add(BuildNetDev(spec.Network, ports.Adb));

        arguments.Add("-device");
        arguments.Add($"virtio-net-pci,netdev={NetworkDeviceId}");

        foreach (var device in BlissDevices)
        {
            arguments.Add("-device");
            arguments.Add(device);
        }

        return arguments;
    }

    /// <summary>
    /// 追加内核引导模式相关参数。只有显式配置了内核镜像时才下发 -kernel，
    /// -initrd 与 -append 同属内核引导参数，
    /// 未配置内核镜像时一律省略，否则 QEMU 会以「-append only allowed with -kernel option」拒绝启动。
    /// -append 的合成顺序为 {kernelAppend 骨架} + {KernelCommandLine 的标识与保真度片段}。
    /// 未声明 kernelAppend 时给出安全默认骨架（至少含 nomodeset 与可引导的 root 参数）。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <param name="image">实例引用的镜像清单，可为空。</param>
    /// <param name="arguments">待追加的参数序列。</param>
    private void AppendKernelBootArguments(
        InstanceSpec spec,
        ImageSpec? image,
        List<string> arguments)
    {
        string? kernelImage = ResolveKernelImagePath(spec);
        if (string.IsNullOrWhiteSpace(kernelImage))
        {
            return;
        }

        arguments.Add("-kernel");
        arguments.Add(kernelImage);

        string? initrdImage = ResolveInitrdImagePath(spec);
        if (!string.IsNullOrWhiteSpace(initrdImage))
        {
            arguments.Add("-initrd");
            arguments.Add(initrdImage);
        }

        string skeleton = ResolveKernelAppendSkeleton(spec);
        var dynamicCommandLine = KernelCommandLine.Build(
            spec.DeviceIdentity,
            image?.Verified?.Fidelity.P2SystemWrite ?? VerificationState.Untested);

        var append = KernelCommandLine.Compose(new[] { skeleton, dynamicCommandLine });
        if (append.Length > 0)
        {
            arguments.Add("-append");
            arguments.Add(append);
        }
    }

    /// <summary>
    /// 解析 -accel 取值。实例在 platformConfig 中显式声明时覆盖宿主级参数；
    /// 未声明时使用宿主级参数，其缺省为 WHPX 且关闭内核 irqchip 直通。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <returns>-accel 参数值。</returns>
    /// <exception cref="XBearException">声明含非法字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private string ResolveAccelerator(InstanceSpec spec)
    {
        var platform = spec.PlatformConfig;

        string accelerator = platform?.Accelerator is { } declaredAccelerator
            ? declaredAccelerator.Trim()
            : _accelerator.Accelerator;

        if (!AcceleratorOptions.IsValidAcceleratorName(accelerator))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {spec.Id} 声明的加速器 {accelerator} 非法。",
                $"platformConfig.{AcceleratorConfigKey} 只允许加速器名称，允许的字符为字母、数字、下划线、点与连字符。");
        }

        // 实例显式声明附加选项时整条替换（含显式置空），未声明时沿用宿主级缺省。
        string? options = platform?.AcceleratorOptions is { } declaredOptions
            ? declaredOptions.Trim()
            : _accelerator.Options;

        if (!AcceleratorOptions.IsValidOptions(options))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {spec.Id} 声明的加速器选项 {options} 非法。",
                $"platformConfig.{AcceleratorOptionsConfigKey} 只允许 key=value 序列，允许的字符为字母、数字、下划线、点、连字符、逗号与等号。");
        }

        return string.IsNullOrWhiteSpace(options)
            ? accelerator
            : $"{accelerator},{options}";
    }

    /// <summary>
    /// 解析内核引导模式使用的内核镜像路径，未声明时返回 null 表示走磁盘引导。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <returns>内核镜像路径，未声明时返回 null。</returns>
    /// <exception cref="XBearException">声明含非法字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string? ResolveKernelImagePath(InstanceSpec spec)
    {
        var declared = spec.PlatformConfig?.KernelImage;
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        string kernelImage = declared.Trim();
        if (kernelImage.Any(static c => char.IsWhiteSpace(c) || c is '"'))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {spec.Id} 声明的内核镜像路径 {kernelImage} 非法。",
                $"platformConfig.{KernelImageConfigKey} 必须是可直达的镜像文件路径，且不得含空白或引号。");
        }

        return kernelImage;
    }

    /// <summary>
    /// 解析内核引导模式使用的初始 ramdisk 路径，未声明时返回 null。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <returns>初始 ramdisk 路径，未声明时返回 null。</returns>
    /// <exception cref="XBearException">声明含非法字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string? ResolveInitrdImagePath(InstanceSpec spec)
    {
        var declared = spec.PlatformConfig?.InitrdImage;
        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        string initrdImage = declared.Trim();
        if (initrdImage.Any(static c => char.IsWhiteSpace(c) || c is '"'))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {spec.Id} 声明的初始 ramdisk 路径 {initrdImage} 非法。",
                $"platformConfig.{InitrdImageConfigKey} 必须是可直达的镜像文件路径，且不得含空白或引号。");
        }

        return initrdImage;
    }

    /// <summary>
    /// 解析内核引导命令行骨架。显式声明时以声明值为准（含显式置空），
    /// 未声明时回退到安全默认骨架，避免实例因缺省骨架而引导失败。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <returns>内核命令行骨架，未声明时返回默认骨架，显式置空时返回空串。</returns>
    private static string ResolveKernelAppendSkeleton(InstanceSpec spec)
    {
        var declared = spec.PlatformConfig?.KernelAppend;

        return declared is null ? DefaultKernelAppendSkeleton : declared.Trim();
    }

    /// <summary>
    /// 解析 -cpu 取值。实例显式声明时按声明下发，
    /// 缺省或空白时回退到宿主 CPU 直通，未实测的实例不得因缺省而丢失镜像要求的指令集。
    /// </summary>
    /// <param name="spec">实例配置。</param>
    /// <returns>-cpu 参数值。</returns>
    /// <exception cref="XBearException">声明的型号含非法字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string ResolveCpuModel(InstanceSpec spec)
    {
        var cpuModel = spec.Resources.CpuModel?.Trim();
        if (string.IsNullOrEmpty(cpuModel))
        {
            return DefaultCpuModel;
        }

        if (!CpuModelPattern.IsMatch(cpuModel))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"实例 {spec.Id} 的 cpuModel {cpuModel} 含非法字符。",
                "cpuModel 只允许字母、数字与 QEMU CPU 型号用的 ._+-,= 组合字符，请修正后重试。");
        }

        return cpuModel;
    }

    /// <summary>组装用户态 NAT 网络设备串，除用户配置的映射外固定附带 adb 转发。</summary>
    /// <param name="network">实例网络配置，可为空，空值按缺省回环暴露处理。</param>
    /// <param name="adbHostPort">宿主侧分配到的 adb 端口。</param>
    /// <returns>-netdev 参数值。</returns>
    /// <exception cref="XBearException">固定地址不符合契约时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string BuildNetDev(NetworkSpec? network, int adbHostPort)
    {
        var parts = new List<string> { QemuUserNetPrefix, $"id={NetworkDeviceId}" };

        var fixedAddress = network?.FixedAddress;
        if (!string.IsNullOrWhiteSpace(fixedAddress))
        {
            var trimmed = fixedAddress.Trim();
            if (!FixedAddressPattern.IsMatch(trimmed))
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"固定地址 {trimmed} 不符合 10.0.2.x 段约定。",
                    "请把 network.fixedAddress 改为 10.0.2.2 至 10.0.2.254 之间的地址。");
            }

            parts.Add($"net={trimmed}/24");
        }

        var forwards = network?.PortForwards;
        if (forwards is not null)
        {
            foreach (var forward in forwards)
            {
                parts.Add(BuildHostForward(network, forward));
            }
        }

        parts.Add(BuildAdbHostForward(network, adbHostPort));
        return string.Join(",", parts);
    }

    /// <summary>
    /// 组装内建的 adb 端口转发，把宿主分配端口接到 guest 内 adbd 的固定监听端口。
    /// 宿主连不上 adbd 时投屏通路与文件回传均不可用，因此该转发不依赖用户是否配置端口映射。
    /// </summary>
    /// <param name="network">实例网络配置，可为空。</param>
    /// <param name="adbHostPort">宿主侧分配到的 adb 端口。</param>
    /// <returns>hostfwd 参数值。</returns>
    private static string BuildAdbHostForward(NetworkSpec? network, int adbHostPort)
    {
        // 绑定地址与用户端口映射共用同一套暴露上限判定，避免另起规则造成越权暴露。
        var bindAddress = ResolveBindAddress(network?.Exposure, null);
        return $"hostfwd=tcp:{bindAddress}:{adbHostPort}-:{AdbGuestPort}";
    }

    /// <summary>
    /// 解析 VNC 显示号参数。禁用本地显示窗口时 VNC 是宿主侧唯一可视通道，
    /// 绑定地址与 adb 转发一致，同样受实例暴露上限约束。
    /// </summary>
    /// <param name="exposure">实例级端口对外可达级别。</param>
    /// <param name="vncPort">宿主侧分配到的 VNC 端口。</param>
    /// <returns>-vnc 参数值。</returns>
    /// <exception cref="XBearException">端口低于显示号基准时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string BuildVncDisplay(string? exposure, int vncPort)
    {
        if (vncPort < VncDisplayBasePort)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"VNC 端口 {vncPort} 低于显示号基准 {VncDisplayBasePort}，无法映射为 VNC 显示号。");
        }

        var bindAddress = ResolveBindAddress(exposure, null);
        return $"{bindAddress}:{vncPort - VncDisplayBasePort}";
    }

    /// <summary>组装单条端口映射，绑定地址由暴露级别上限决定。</summary>
    /// <param name="network">实例网络配置，可为空。</param>
    /// <param name="forward">端口映射条目。</param>
    /// <returns>hostfwd 参数值。</returns>
    /// <exception cref="XBearException">映射配置不满足契约时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string BuildHostForward(NetworkSpec? network, PortForward forward)
    {
        ArgumentNullException.ThrowIfNull(forward);

        ValidatePort(forward.HostPort, "hostPort");
        ValidatePort(forward.GuestPort, "guestPort");

        var protocol = forward.Protocol?.Trim().ToLowerInvariant() ?? string.Empty;
        if (protocol is not ("tcp" or "udp"))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"端口映射协议 {forward.Protocol} 不受支持，仅支持 tcp 与 udp。");
        }

        var bindAddress = ResolveBindAddress(network?.Exposure, forward.Bind);
        return $"hostfwd={protocol}:{bindAddress}:{forward.HostPort}-:{forward.GuestPort}";
    }

    /// <summary>解析实际生效的宿主绑定地址。
    /// 实例级 exposure 决定暴露上限，单条映射的 bind 只允许下调、不允许突破上限；
    /// 缺省、空白或无法识别的取值一律回落为回环，避免因配置疏漏意外对外暴露。</summary>
    /// <param name="exposure">实例级端口对外可达级别。</param>
    /// <param name="bind">单条映射声明的绑定级别，可为空。</param>
    /// <returns>绑定地址，级别为回环时返回 <see cref="LoopbackBindAddress"/>，否则返回 <see cref="AnyBindAddress"/>。</returns>
    private static string ResolveBindAddress(string? exposure, string? bind)
    {
        var upperBound = NormalizeExposure(exposure);
        var requested = NormalizeOptionalExposure(bind);
        var effective = requested is null ? upperBound : Min(upperBound, requested);

        return effective == LoopbackExposure ? LoopbackBindAddress : AnyBindAddress;
    }

    /// <summary>归一化实例级暴露取值，无法识别时回落为回环。</summary>
    /// <param name="exposure">原始暴露取值。</param>
    /// <returns>合法暴露级别标识。</returns>
    private static string NormalizeExposure(string? exposure)
    {
        var normalized = exposure?.Trim().ToLowerInvariant();
        return normalized is (LanExposure or PublicExposure) ? normalized : LoopbackExposure;
    }

    /// <summary>归一化单条映射的绑定取值，空值表示未作声明。</summary>
    /// <param name="bind">原始绑定取值。</param>
    /// <returns>合法暴露级别标识，未声明时返回 null，无法识别时回落为回环。</returns>
    private static string? NormalizeOptionalExposure(string? bind)
    {
        if (string.IsNullOrWhiteSpace(bind))
        {
            return null;
        }

        var normalized = bind.Trim().ToLowerInvariant();
        return normalized is (LoopbackExposure or LanExposure or PublicExposure)
            ? normalized
            : LoopbackExposure;
    }

    /// <summary>取两个暴露级别中暴露面更小的一个。</summary>
    /// <param name="left">左侧级别。</param>
    /// <param name="right">右侧级别。</param>
    /// <returns>暴露面更小的级别标识。</returns>
    private static string Min(string left, string right)
        => Rank(left) <= Rank(right) ? left : right;

    /// <summary>把暴露级别映射为可比较的暴露面次序。</summary>
    /// <param name="exposure">暴露级别标识。</param>
    /// <returns>次序值，回环为 0，局域网为 1，公网为 2。</returns>
    private static int Rank(string exposure)
        => exposure switch
        {
            LoopbackExposure => 0,
            LanExposure => 1,
            _ => 2,
        };

    /// <summary>校验端口处于合法范围。</summary>
    /// <param name="port">待校验端口。</param>
    /// <param name="name">参数名称，用于异常信息。</param>
    /// <exception cref="XBearException">端口越界时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static void ValidatePort(int port, string name)
    {
        if (port is < 1 or > 65535)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"端口 {name}={port} 越界，必须处于 1 至 65535 之间。");
        }
    }

    /// <summary>
    /// 转义 QEMU keyval 格式选项值中的分隔符，避免破坏参数结构。
    /// 逗号是 QEMU 的选项分隔符，值内出现逗号时必须重复输出一个逗号；
    /// 等号只用于分隔选项名与取值，取值内部的等号是字面量、不作任何转义。
    /// </summary>
    /// <param name="value">原始取值。</param>
    /// <returns>转义后的取值，逗号已重复、等号保持原样。</returns>
    private static string EscapeOptionValue(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character == ',')
            {
                builder.Append(character);
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}