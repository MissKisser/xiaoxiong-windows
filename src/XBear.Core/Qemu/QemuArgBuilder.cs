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

    /// <summary>平台默认 CPU 型号选型：已验证可引导 Android-x86 系镜像的型号。</summary>
    public const string PlatformDefaultCpuModel = "Skylake-Client";

    /// <summary>直传宿主 CPU 的 QEMU 型号写法，在本产品支持的平台上已被实测判定不可用。</summary>
    public const string HostCpuModel = "host";

    /// <summary>Windows Hypervisor Platform 加速器标识，为首选加速路径。</summary>
    public const string WhpxAccelerator = "whpx";

    /// <summary>纯软件模拟加速器标识，硬件加速不可用时的回退路径。</summary>
    public const string TcgAccelerator = "tcg";

    /// <summary>Android-x86 系镜像引导程序要求的最低 CPU 指令集。</summary>
    public const string RequiredCpuFeature = "SSE4.2";

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

    /// <summary>
    /// 已实测验证可引导目标镜像的 CPU 型号集合。
    /// 集合之外一律拒绝，避免把未经验证的选型交给用户在启动失败后自行排查。
    /// </summary>
    private static readonly HashSet<string> VerifiedCpuModels =
        new(StringComparer.OrdinalIgnoreCase) { PlatformDefaultCpuModel };

    /// <summary>已确认缺少 <see cref="RequiredCpuFeature"/> 的型号，这些型号会被引导程序判定为不支持而主动中止引导。</summary>
    private static readonly HashSet<string> ModelsMissingRequiredFeature =
        new(StringComparer.OrdinalIgnoreCase) { "qemu64" };

    /// <summary>按加速路径可用性选出 <c>-accel</c> 取值。</summary>
    /// <param name="whpxAvailable">硬件加速是否可用。</param>
    /// <returns>硬件加速可用时返回 <see cref="WhpxAccelerator"/>，否则返回 <see cref="TcgAccelerator"/>。</returns>
    public static string ResolveAccelerator(bool whpxAvailable)
        => whpxAvailable ? WhpxAccelerator : TcgAccelerator;

    /// <summary>解析实例将实际使用的 CPU 型号，未声明时回落到平台默认选型。</summary>
    /// <param name="declaredCpuModel">实例声明的 CPU 型号，可为空。</param>
    /// <returns>去掉首尾空白后的型号；声明为空或仅含空白时返回 <see cref="PlatformDefaultCpuModel"/>。</returns>
    public static string ResolveCpuModel(string? declaredCpuModel)
        => string.IsNullOrWhiteSpace(declaredCpuModel)
            ? PlatformDefaultCpuModel
            : declaredCpuModel.Trim();

    /// <summary>
    /// 校验 CPU 型号是否满足目标镜像的启动前置条件，为不依赖文件系统与进程的纯函数。
    /// 判定顺序为：拒绝直传宿主型号、拒绝显式关闭必需指令集、拒绝缺少必需指令集的型号、拒绝未验证型号。
    /// </summary>
    /// <param name="cpuModel">待校验的 CPU 型号，可带 <c>+特性</c> 与 <c>-特性</c> 后缀。</param>
    /// <exception cref="XBearException">
    /// 型号不可用时抛出 <see cref="ErrorCategory.Spec"/>：
    /// 使用 <see cref="HostCpuModel"/>、显式关闭 <see cref="RequiredCpuFeature"/>、
    /// 缺少 <see cref="RequiredCpuFeature"/>、或不在已验证集合内。
    /// </exception>
    public static void ValidateCpuModel(string cpuModel)
    {
        if (string.IsNullOrWhiteSpace(cpuModel))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "CPU 型号不能为空。",
                $"请把 resources.cpuModel 设为 {PlatformDefaultCpuModel}，或删除该字段使用平台默认选型。");
        }

        var declaration = cpuModel.Trim();
        var separator = declaration.IndexOf(',', StringComparison.Ordinal);
        var model = (separator < 0 ? declaration : declaration[..separator]).Trim();
        var features = separator < 0
            ? Array.Empty<string>()
            : declaration[(separator + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries);

        if (string.Equals(model, HostCpuModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"CPU 型号 {model} 不可用：本产品支持的平台上直传宿主处理器会导致加速器初始化失败。",
                $"请把 resources.cpuModel 改为 {PlatformDefaultCpuModel}，该型号已验证可引导目标镜像。");
        }

        if (features.Any(feature =>
                string.Equals(feature.Trim(), "-" + RequiredCpuFeature, StringComparison.OrdinalIgnoreCase)))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"CPU 型号 {declaration} 显式关闭了 {RequiredCpuFeature}，镜像引导程序会判定为不支持并中止引导。",
                $"请从 resources.cpuModel 中去掉 -{RequiredCpuFeature} 相关特性后缀。");
        }

        if (ModelsMissingRequiredFeature.Contains(model))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"CPU 型号 {model} 不支持 {RequiredCpuFeature}，镜像引导程序会判定为不支持并中止引导。",
                $"请把 resources.cpuModel 改为 {PlatformDefaultCpuModel} 或其他含 {RequiredCpuFeature} 的已验证型号。");
        }

        if (!VerifiedCpuModels.Contains(model))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"CPU 型号 {model} 未在已验证列表内，无法确认其含 {RequiredCpuFeature} 且可引导目标镜像。",
                $"请把 resources.cpuModel 改为 {PlatformDefaultCpuModel}，或在完成实测引导后扩充已验证列表。");
        }
    }


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

        var cpuModel = ResolveCpuModel(spec.Resources.CpuModel);
        ValidateCpuModel(cpuModel);

        var arguments = new List<string>
        {
            "-accel",
            ResolveAccelerator(spec),
            "-cpu",
            cpuModel,
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