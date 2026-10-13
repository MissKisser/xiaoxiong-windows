using System.Text;
using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Spec;

namespace XBear.Core.Qemu;

/// <summary>
/// 设备标识与镜像保真度到内核引导命令行的映射。
/// 全部为纯函数，不接触文件系统与进程，便于单元测试与逐项核对。
/// </summary>
/// <remarks>
/// AOSP 系（含 Bliss OS 与 Android-x86 系的 androidboot 派生内核）在 init 阶段
/// 解析 /proc/cmdline 中以 <see cref="AndroidBootPropertyPrefix"/> 开头的键值对，
/// 并映射为只读的 ro.boot.* 属性，随后由 guest 侧框架消费。
/// 该通道只覆盖「引导期属性」，因此并非每一项设备标识都能经此生效。
/// QEMU 只在传了 -kernel 的内核引导模式下接受 -append，
/// 因此本合成器的调用方必须在磁盘引导模式下跳过 -append，
/// 序列号改由 <see cref="DeviceIdentityChannels"/> 的 SMBIOS 通道下发。
/// </remarks>
public static class KernelCommandLine
{
    /// <summary>内核引导属性的统一前缀。</summary>
    public const string AndroidBootPropertyPrefix = "androidboot.";

    /// <summary>
    /// 序列号对应的引导键。该键是标识通道中唯一具备标准 guest 侧消费方的取值，
    /// AOSP init 会据此设置 ro.serialno 与 ro.boot.serialno。
    /// </summary>
    public const string SerialNoKey = AndroidBootPropertyPrefix + "serialno";

    /// <summary>
    /// Android ID 对应的引导键。AOSP 未为该标识定义内核引导期消费方，
    /// 系统内的实际取值由 secure 设置库持有，此处按下发约定输出作为扩展点。
    /// </summary>
    public const string AndroidIdKey = AndroidBootPropertyPrefix + "android_id";

    /// <summary>
    /// IMEI 对应的引导键。AOSP 未为该标识定义内核引导期消费方，
    /// 系统内的实际取值由基带侧持有，此处按下发约定输出作为扩展点。
    /// </summary>
    public const string ImeiKey = AndroidBootPropertyPrefix + "imei";

    /// <summary>启动链加锁状态对应的引导键。</summary>
    public const string FlashLockedKey = AndroidBootPropertyPrefix + "flash.locked";

    /// <summary>vbmeta 设备状态对应的引导键。</summary>
    public const string VbmetaDeviceStateKey = AndroidBootPropertyPrefix + "vbmeta.device_state";

    /// <summary>已校验启动状态对应的引导键。</summary>
    public const string VerifiedBootStateKey = AndroidBootPropertyPrefix + "verifiedbootstate";

    /// <summary>dm-verity 模式对应的引导键。</summary>
    public const string VerityModeKey = AndroidBootPropertyPrefix + "veritymode";

    /// <summary>引导加载程序标识对应的引导键。</summary>
    public const string BootloaderKey = AndroidBootPropertyPrefix + "bootloader";

    /// <summary>基带版本标识对应的引导键。</summary>
    public const string BasebandKey = AndroidBootPropertyPrefix + "baseband";

    /// <summary>引导模式对应的引导键。</summary>
    public const string BootmodeKey = AndroidBootPropertyPrefix + "bootmode";

    /// <summary>硬件代号对应的引导键。实机已证实下发该键会击穿镜像的按代号解析，当前不下发。</summary>
    public const string HardwareKey = AndroidBootPropertyPrefix + "hardware";

    /// <summary>
    /// 系统分区可写对应的引导键。仅在镜像保真度实测通过时下发，
    /// 未实测不得臆测为可写。
    /// </summary>
    public const string WritableSystemKey = AndroidBootPropertyPrefix + "writable_system";

    /// <summary>系统分区可写开关的取值。</summary>
    public const string WritableSystemEnabledValue = "1";

    /// <summary>命令行片段之间的分隔符，内核按空白切分命令行。</summary>
    public const string FragmentSeparator = " ";

    /// <summary>设备标识各字段到引导键的固定映射顺序。</summary>
    private static readonly (string Key, Func<DeviceIdentity, string?> Selector)[] IdentityBindings =
    {
        (SerialNoKey, identity => identity.SerialNo),
        (AndroidIdKey, identity => identity.AndroidId),
        (ImeiKey, identity => identity.Imei),
    };

    /// <summary>
    /// 把设备标识映射为内核引导命令行片段，空字段不产出片段。
    /// IMEI 为条件通道：镜像无 telephony 能力时该项恒为空，不产出片段，
    /// 向无基带的实例下发该键只会制造「声明存在却无人消费」的矛盾。
    /// </summary>
    /// <param name="identity">实例设备标识，可为空，空值不产出任何片段。</param>
    /// <returns>按固定顺序排列的引导命令行片段集合。</returns>
    /// <exception cref="XBearException">标识取值含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static IReadOnlyList<string> BuildIdentityFragments(DeviceIdentity? identity)
    {
        if (identity is null)
        {
            return Array.Empty<string>();
        }

        var fragments = new List<string>(IdentityBindings.Length);
        foreach (var (key, selector) in IdentityBindings)
        {
            var value = selector(identity);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            fragments.Add(ComposeFragment(key, value.Trim()));
        }

        return fragments;
    }

    /// <summary>
    /// 把系统可写保真度映射为内核引导命令行片段。
    /// 仅实测通过时产出片段，未实测与实测失败均视为不可写。
    /// </summary>
    /// <param name="systemWrite">镜像保真度中 P2 系统可写的实测结论。</param>
    /// <returns>实测通过时返回可写开关片段，否则返回 null。</returns>
    public static string? BuildWritableSystemFragment(VerificationState systemWrite)
        => systemWrite == VerificationState.Pass
            ? ComposeFragment(WritableSystemKey, WritableSystemEnabledValue)
            : null;

    /// <summary>
    /// 把启动链与机型族弱校验画像映射为内核引导命令行片段。
    /// 未指定画像且未指定启动链规格时不产出任何片段。
    /// </summary>
    /// <param name="profile">机型族画像，可为空。</param>
    /// <param name="bootIdentity">启动链规格，可为空。</param>
    /// <returns>按固定顺序排列的引导命令行片段集合。</returns>
    /// <exception cref="XBearException">取值含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static IReadOnlyList<string> BuildBootFragments(
        DeviceProfile? profile,
        BootIdentitySpec? bootIdentity = null)
    {
        if (profile is null && bootIdentity is null)
        {
            return Array.Empty<string>();
        }

        var fragments = new List<string>(8);

        string flashLocked = bootIdentity?.FlashLocked ?? PropertyOverlayBuilder.DefaultFlashLocked;
        fragments.Add(ComposeFragment(FlashLockedKey, flashLocked));

        string vbmetaState = bootIdentity?.VbmetaDeviceState ?? PropertyOverlayBuilder.DefaultVbmetaDeviceState;
        fragments.Add(ComposeFragment(VbmetaDeviceStateKey, vbmetaState));

        string verifiedBootState = bootIdentity?.VerifiedBootState ?? PropertyOverlayBuilder.DefaultVerifiedBootState;
        fragments.Add(ComposeFragment(VerifiedBootStateKey, verifiedBootState));

        string verityMode = bootIdentity?.VerityMode ?? PropertyOverlayBuilder.DefaultVerityMode;
        fragments.Add(ComposeFragment(VerityModeKey, verityMode));

        string bootloader = bootIdentity?.Bootloader
            ?? (profile is not null ? $"{profile.Device}-boot-1.0" : PropertyOverlayBuilder.DefaultBootloader);
        fragments.Add(ComposeFragment(BootloaderKey, bootloader));

        if (!string.IsNullOrWhiteSpace(bootIdentity?.Baseband))
        {
            fragments.Add(ComposeFragment(BasebandKey, bootIdentity.Baseband.Trim()));
        }

        string bootmode = bootIdentity?.Bootmode ?? "normal";
        fragments.Add(ComposeFragment(BootmodeKey, bootmode));

        // androidboot.hardware 不在内核引导参数中下发（实机实测结论，见注释）。
        //
        // 该键属于内核级硬件平台标识：guest 的 ro.hardware / ro.boot.hardware 由内核命令行通道
        // 直接钉死，下发非镜像原生代号会击穿镜像内部的按代号解析，实机复测表现为
        // GraphicBufferMapper 找不到 gralloc mapper、surfaceflinger SIGABRT，
        // 框架起不来 → sys.boot_completed 永不置位 → eth0 不取址 → ADB 不可达，整机不可用。
        //
        // initrd 侧的代号别名（init.<代号>.rc、/fstab.<代号>、HAL 目录软链）只能覆盖
        // 以代号命名的文件条目，覆盖不了镜像内以原生代号硬编码的 HAL/VINTF 解析路径。
        // 硬件代号压制必须由镜像侧先做代号适配，属已知取舍：S-07 据此维持 fail。

        return fragments;
    }

    /// <summary>
    /// 合成完整的内核引导命令行。仅内核引导模式（已配置 -kernel）可使用。
    /// </summary>
    /// <param name="identity">实例设备标识，可为空。</param>
    /// <param name="systemWrite">镜像保真度中 P2 系统可写的实测结论。</param>
    /// <param name="profile">机型族画像，可为空；未传入时尝试按 identity.DeviceProfileId 自动解析。</param>
    /// <param name="bootIdentity">启动链规格，可为空。</param>
    /// <returns>以空白分隔的引导命令行，无任何片段时返回空串。</returns>
    /// <exception cref="XBearException">标识取值含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    public static string Build(
        DeviceIdentity? identity,
        VerificationState systemWrite,
        DeviceProfile? profile = null,
        BootIdentitySpec? bootIdentity = null)
    {
        var resolvedProfile = profile ?? (identity?.DeviceProfileId is not null
            ? DeviceProfileCatalog.Find(identity.DeviceProfileId)
            : null);

        var fragments = new List<string>(BuildIdentityFragments(identity));
        fragments.AddRange(BuildBootFragments(resolvedProfile, bootIdentity));

        var writable = BuildWritableSystemFragment(systemWrite);
        if (writable is not null)
        {
            fragments.Add(writable);
        }

        return Compose(fragments);
    }

    /// <summary>
    /// 按固定分隔符拼接命令行片段，空集合与全空白片段均不参与拼接，空集合得到空串。
    /// </summary>
    /// <param name="fragments">待拼接的片段集合，可为空。</param>
    /// <returns>拼接后的命令行文本。</returns>
    public static string Compose(IEnumerable<string> fragments)
    {
        ArgumentNullException.ThrowIfNull(fragments);

        var builder = new StringBuilder();
        foreach (var fragment in fragments)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(FragmentSeparator);
            }

            builder.Append(fragment);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 清洗内核引导命令行：提取并消除 SRC= 等安装态标记（便于等价迁移至 initrd 内嵌），
    /// 并按设计默认处置移除串口控制台参数（如 console=ttyS0）。
    /// </summary>
    /// <param name="commandLine">原始引导命令行。</param>
    /// <param name="extractedSrc">提取出的 SRC 参数取值，未声明时为 null。</param>
    /// <returns>清洗收敛后的引导命令行。</returns>
    public static string SanitizeKernelAppend(string commandLine, out string? extractedSrc)
    {
        extractedSrc = null;
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        string[] tokens = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var retained = new List<string>(tokens.Length);

        foreach (string token in tokens)
        {
            if (token.StartsWith("SRC=", StringComparison.OrdinalIgnoreCase))
            {
                extractedSrc = token[4..].Trim();
                continue;
            }

            if (token.StartsWith("console=ttyS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            retained.Add(token);
        }

        return Compose(retained);
    }

    /// <summary>拼接单个键值对形式的引导命令行片段。</summary>
    /// <param name="key">引导键。</param>
    /// <param name="value">引导取值。</param>
    /// <returns>形如 键=值 的片段文本。</returns>
    /// <exception cref="XBearException">取值含白名单外字符时抛出 <see cref="ErrorCategory.Spec"/>。</exception>
    private static string ComposeFragment(string key, string value)
    {
        if (!DeviceIdentityChannels.IsAllowedValue(value))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"引导命令行取值 {value} 含非法字符。",
                "设备标识只能使用字母、数字、点、下划线与连字符；请修正实例配置中的该标识后重试。");
        }

        return $"{key}={value}";
    }
}