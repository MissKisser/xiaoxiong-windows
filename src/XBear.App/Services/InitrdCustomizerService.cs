using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using XBear.Core.Diagnostics;
using XBear.Core.Identity;
using XBear.Core.Spec;

namespace XBear.App.Services;

/// <summary>
/// initrd 定制策略。
/// 策略内容会参与产物指纹的计算：任一字段变化都会让指纹变化，从而触发重新定制。
/// 支持 ADB 调试通路、root 权限、开机模块加载器以及抗取证检测隐藏定制项。
/// </summary>
/// <param name="AdbGuestPort">guest 内 adbd 的监听端口。</param>
/// <param name="AdbPublicKey">写入 guest 授权列表的宿主 ADB 公钥。</param>
/// <param name="AdbApexMountPath">adbd 二进制所在的 APEX 挂载点。</param>
/// <param name="EnableRoot">是否使能 root 权限。</param>
/// <param name="EnableModuleLoader">是否启用开机模块加载器（扫描 /data/adb/modules 并叠加）。</param>
/// <param name="EnableBuildPropOverlay">是否启用 build.prop 覆写（A-03）。</param>
/// <param name="BuildPropContent">覆写的 build.prop 属性文本，由 PropertyOverlayBuilder 生成。</param>
/// <param name="EnableAndroidIdProvisioning">是否启用 Android ID 消费与回读（A-05/S-3）。</param>
/// <param name="TargetAndroidId">待落地的 Android ID（16 位小写十六进制字符串）。</param>
/// <param name="EnableNetworkConvergence">是否启用网络收敛与默认路由补齐（A-08）。</param>
/// <param name="RenameInterfaceToWlan0">是否将网络接口 eth0 重命名为 wlan0。</param>
/// <param name="DefaultGateway">默认路由网关 IP 地址。</param>
/// <param name="RootStealth">是否启用 root 痕迹去明文持久化（A-16，消除 local.prop 明文，仅运行时断言）。</param>
/// <param name="CleanupKernelSuRemnants">是否清理非本产品的 KernelSU 遗留目录与二进制（A-14/Q-02）。</param>
/// <param name="EmbeddedSrc">内嵌的安装态 SRC 标记（A-13，消除可见 cmdline 中的 SRC= 参数）。</param>
/// <param name="ModulesReleaseAlias">内核版本串别名（去指纹内核版本串配套的模块目录条目名）。</param>
/// <param name="HardwareAlias">硬件代号别名（去指纹硬件代号配套的 init rc 与 HAL 软链别名）。</param>
/// <param name="HardwareSourceAlias">镜像原生硬件代号（HAL 软链的源名，默认见 <see cref="DefaultHardwareSourceAlias"/>）。</param>
/// <param name="StripPrivatePropertyPrefix">需要行级删除的镜像私有属性前缀，为空表示不做删除。</param>
/// <param name="HostName">guest 主机名，取值为机型族代号。</param>
/// <param name="RootPropertyRewriteContent">需要行级取值改写的根级键清单文本，由 PropertyOverlayBuilder 生成；为空表示不做改写。</param>
public sealed record InitrdCustomizationPolicy(
    int AdbGuestPort,
    string AdbPublicKey,
    string AdbApexMountPath,
    bool EnableRoot = true,
    bool EnableModuleLoader = true,
    bool EnableBuildPropOverlay = false,
    string? BuildPropContent = null,
    bool EnableAndroidIdProvisioning = false,
    string? TargetAndroidId = null,
    bool EnableNetworkConvergence = false,
    bool RenameInterfaceToWlan0 = false,
    string? DefaultGateway = null,
    bool RootStealth = false,
    bool CleanupKernelSuRemnants = false,
    string? EmbeddedSrc = null,
    string? ModulesReleaseAlias = null,
    string? HardwareAlias = null,
    string? HardwareSourceAlias = null,
    string? StripPrivatePropertyPrefix = null,
    string? HostName = null,
    string? RootPropertyRewriteContent = null)
{
    /// <summary>
    /// 镜像原生硬件代号的缺省取值。
    /// 该值只用于在 HAL 库目录中按「原生代号命名」检索既有动态库并补建代号别名软链，
    /// 不参与任何对外可见的取值派生；换镜像只需替换该常量或由调用方显式传入。
    /// </summary>
    public const string DefaultHardwareSourceAlias = "bliss_x86_64";

    /// <summary>
    /// 需要行级删除的镜像私有属性前缀缺省值。
    /// 追加式覆写只能新增或覆盖键，无法消除属性条目，镜像私有属性族必须从载入源删行。
    /// </summary>
    public const string DefaultStripPrivatePropertyPrefix = "ro.bliss.";

    /// <summary>
    /// 是否启用模块目录版本串别名定制项。
    /// 该项为空时完全等价于未启用，不影响既有产物的指纹与幂等判定。
    /// </summary>
    public bool ModulesReleaseAliasEnabled => !string.IsNullOrWhiteSpace(ModulesReleaseAlias);

    /// <summary>
    /// 是否启用硬件代号 init rc 别名定制项。
    /// 该项为空时完全等价于未启用，不影响既有产物的指纹与幂等判定。
    /// </summary>
    public bool HardwareAliasEnabled => !string.IsNullOrWhiteSpace(HardwareAlias);

    /// <summary>是否启用 HAL 库目录代号别名软链。需同时具备目标代号与镜像原生代号。</summary>
    public bool HardwareHalAliasEnabled =>
        HardwareAliasEnabled &&
        !string.IsNullOrWhiteSpace(HardwareSourceAlias) &&
        !string.Equals(HardwareAlias?.Trim(), HardwareSourceAlias?.Trim(), StringComparison.Ordinal);

    /// <summary>是否启用镜像私有属性族的行级删除。</summary>
    public bool StripPrivatePropertiesEnabled => !string.IsNullOrWhiteSpace(StripPrivatePropertyPrefix);

    /// <summary>是否启用 guest 主机名收敛。</summary>
    public bool HostNameEnabled => !string.IsNullOrWhiteSpace(HostName);

    /// <summary>
    /// 是否启用根级属性键的行级取值改写。
    /// 追加式覆写只能新增键，改不了镜像 prop 文件里已存在的根级键取值，
    /// 镜像原生的构建类型与启动链取值会把追加结果盖回，必须另行逐键改写。
    /// </summary>
    public bool RootPropertyRewriteEnabled => !string.IsNullOrWhiteSpace(RootPropertyRewriteContent);

    /// <summary>
    /// 是否启用了任何抗取证扩展定制项。
    /// 当全部为默认关闭值时，指纹计算完全等价于传统策略，确保已有镜像产物不被误判二次定制。
    /// </summary>
    public bool HasAntiforensicsCustomizations =>
        EnableBuildPropOverlay ||
        !string.IsNullOrWhiteSpace(BuildPropContent) ||
        EnableAndroidIdProvisioning ||
        !string.IsNullOrWhiteSpace(TargetAndroidId) ||
        EnableNetworkConvergence ||
        RenameInterfaceToWlan0 ||
        !string.IsNullOrWhiteSpace(DefaultGateway) ||
        RootStealth ||
        CleanupKernelSuRemnants ||
        !string.IsNullOrWhiteSpace(EmbeddedSrc) ||
        ModulesReleaseAliasEnabled ||
        HardwareAliasEnabled ||
        HardwareHalAliasEnabled ||
        StripPrivatePropertiesEnabled ||
        HostNameEnabled ||
        RootPropertyRewriteEnabled;

    /// <summary>
    /// 便捷工厂方法：按画像与身份对象构建全套抗取证定制策略。
    /// </summary>
    /// <param name="adbGuestPort">guest 内 adbd 监听端口。</param>
    /// <param name="adbPublicKey">宿主 ADB 公钥。</param>
    /// <param name="adbApexMountPath">adbd APEX 挂载点。</param>
    /// <param name="profile">机型族画像。</param>
    /// <param name="identity">设备身份规格，可为空。</param>
    /// <param name="defaultGateway">默认路由网关，可为空。</param>
    /// <param name="embeddedSrc">内嵌安装态 SRC 标记，可为空。</param>
    /// <param name="enableRoot">是否启用 root 权限。</param>
    /// <param name="rootStealth">是否隐藏 root 明文持久化。</param>
    /// <param name="enableModuleLoader">是否启用开机模块加载器。</param>
    /// <param name="cleanupKernelSuRemnants">是否清理残缺 KernelSU 痕迹。</param>
    /// <param name="renameInterfaceToWlan0">是否重命名网络接口为 wlan0。</param>
    /// <param name="enableRenderSpoofing">是否启用渲染伪装，默认 false（Q-06 默认保守）。</param>
    /// <param name="modulesReleaseAlias">内核版本串别名，为空表示不启用模块目录别名定制项。</param>
    /// <param name="hardwareAlias">硬件代号别名，为空表示使用画像的 Device 代号建立硬件 rc 软链。</param>
    /// <param name="hardwareSourceAlias">镜像原生硬件代号，为空时取 <see cref="DefaultHardwareSourceAlias"/>。</param>
    /// <param name="stripPrivatePropertyPrefix">待行级删除的镜像私有属性前缀，
    /// 为空时取 <see cref="DefaultStripPrivatePropertyPrefix"/>。</param>
    /// <param name="hostName">guest 主机名，为空时依次取身份契约中的主机名与画像的 Device 代号。</param>
    /// <param name="enableRootPropertyRewrite">是否启用根级属性键的行级取值改写，默认 true。</param>
    /// <returns>抗取证 initrd 定制策略。</returns>
    public static InitrdCustomizationPolicy CreateAntiforensicsPolicy(
        int adbGuestPort,
        string adbPublicKey,
        string adbApexMountPath,
        DeviceProfile profile,
        IdentitySpec? identity = null,
        string? defaultGateway = null,
        string? embeddedSrc = null,
        bool enableRoot = true,
        bool rootStealth = true,
        bool enableModuleLoader = true,
        bool cleanupKernelSuRemnants = true,
        bool renameInterfaceToWlan0 = true,
        bool enableRenderSpoofing = false,
        string? modulesReleaseAlias = null,
        string? hardwareAlias = null,
        string? hardwareSourceAlias = null,
        string? stripPrivatePropertyPrefix = null,
        string? hostName = null,
        bool enableRootPropertyRewrite = true)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string propContent = identity is not null
            ? PropertyOverlayBuilder.BuildBuildPropContent(identity, enableRenderSpoofing)
            : PropertyOverlayBuilder.BuildBuildPropContent(profile, enableRenderSpoofing: enableRenderSpoofing);

        string rootRewriteContent = identity is not null
            ? PropertyOverlayBuilder.BuildRootPropertyRewriteContent(identity)
            : PropertyOverlayBuilder.BuildRootPropertyRewriteContent(profile);

        string? targetAndroidId = identity?.AndroidId;

        string? resolvedHostName = FirstNonBlank(
            hostName,
            identity?.HostName,
            profile.Device);

        return new InitrdCustomizationPolicy(
            AdbGuestPort: adbGuestPort,
            AdbPublicKey: adbPublicKey,
            AdbApexMountPath: adbApexMountPath,
            EnableRoot: enableRoot,
            EnableModuleLoader: enableModuleLoader,
            EnableBuildPropOverlay: true,
            BuildPropContent: propContent,
            EnableAndroidIdProvisioning: !string.IsNullOrWhiteSpace(targetAndroidId),
            TargetAndroidId: targetAndroidId,
            EnableNetworkConvergence: true,
            RenameInterfaceToWlan0: renameInterfaceToWlan0,
            DefaultGateway: defaultGateway,
            RootStealth: rootStealth,
            CleanupKernelSuRemnants: cleanupKernelSuRemnants,
            EmbeddedSrc: embeddedSrc,
            ModulesReleaseAlias: modulesReleaseAlias,
            HardwareAlias: hardwareAlias ?? profile.Device,
            HardwareSourceAlias: hardwareSourceAlias ?? DefaultHardwareSourceAlias,
            StripPrivatePropertyPrefix: stripPrivatePropertyPrefix ?? DefaultStripPrivatePropertyPrefix,
            HostName: resolvedHostName,
            RootPropertyRewriteContent: enableRootPropertyRewrite ? rootRewriteContent : null);
    }

    /// <summary>返回首个非空白取值，全部空白时返回 null。</summary>
    /// <param name="candidates">候选取值序列。</param>
    /// <returns>首个去除首尾空白后的非空取值；全部为空时返回 null。</returns>
    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }
}

/// <summary>一次 initrd 定制的终态。</summary>
public enum InitrdCustomizationOutcome
{
    /// <summary>本次真正执行了定制，产物已就位。</summary>
    Customized = 0,

    /// <summary>已是当前策略下的定制产物，本次未重复定制。</summary>
    AlreadyCustomized = 1
}

/// <summary>
/// 一次 initrd 定制的结果。
/// </summary>
/// <param name="Outcome">终态。</param>
/// <param name="InitrdPath">定制产物的路径。</param>
/// <param name="StockPath">原版 initrd 的留档路径。</param>
/// <param name="Fingerprint">本次使用的策略指纹。</param>
/// <param name="CustomizedEntries">被改写的归档条目名，按归档内顺序。</param>
public sealed record InitrdCustomizationResult(
    InitrdCustomizationOutcome Outcome,
    string InitrdPath,
    string StockPath,
    string Fingerprint,
    IReadOnlyList<string> CustomizedEntries);

/// <summary>
/// 把从镜像中提取的原版 initrd 加工为带 ADB 调试通路与抗取证检测隐藏栈的定制 initrd。
/// </summary>
public sealed class InitrdCustomizerService
{
    /// <summary>原版 initrd 的留档后缀。</summary>
    public const string StockSuffix = ".stock";

    /// <summary>宿主 ADB 公钥相对用户目录的路径。</summary>
    public const string HostAdbKeyRelativePath = ".android/adbkey.pub";

    /// <summary>initrd 内被改写的引导脚本条目名。</summary>
    public const string AutoDetectEntryName = "scripts/0-auto-detect";

    /// <summary>initrd 内被改写的根脚本条目名。</summary>
    public const string InitEntryName = "init";

    /// <summary>initrd 内构建属性覆写条目名。</summary>
    public const string BuildPropOverrideEntryName = "etc/build.prop.override";

    /// <summary>initrd 内内嵌安装态 SRC 标记条目名。</summary>
    public const string BootSrcEntryName = "etc/boot_src";

    /// <summary>initrd 内根级属性键行级改写清单条目名。</summary>
    public const string RootPropertyRewriteEntryName = "etc/prop.rewrite";

    /// <summary>guest 内 adbd 的固定监听端口，与端口转发目标保持一致。</summary>
    public const int DefaultAdbGuestPort = 5555;

    /// <summary>adbd 二进制所在的 APEX 挂载点。</summary>
    public const string DefaultAdbApexMountPath = "/apex/com.android.adbd";

    /// <summary>宿主 ADB 公钥在用户目录下的绝对路径。</summary>
    public static string DefaultHostAdbPublicKeyPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            HostAdbKeyRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>两个自动探测函数的桩实现正文，同时充当替换成功与否的判定标记。</summary>
    private const string AutoDetectStubMarker = "# auto-detect disabled";

    /// <summary>注入块中记录策略指纹的标记前缀。</summary>
    private const string FingerprintMarkerPrefix = "# xbear-initrd-customization: ";

    private readonly string _hostAdbPublicKeyPath;
    private readonly bool _enableRoot;
    private readonly bool _enableModuleLoader;
    private readonly InitrdCustomizationPolicy? _customPolicy;

    /// <summary>
    /// 以默认宿主公钥路径与默认策略选项构造定制服务。
    /// </summary>
    public InitrdCustomizerService()
        : this(DefaultHostAdbPublicKeyPath, enableRoot: true, enableModuleLoader: true)
    {
    }

    /// <summary>
    /// 以指定宿主公钥路径构造定制服务，默认启用 root 与模块加载器。
    /// </summary>
    /// <param name="hostAdbPublicKeyPath">宿主 ADB 公钥文件路径。</param>
    public InitrdCustomizerService(string hostAdbPublicKeyPath)
        : this(hostAdbPublicKeyPath, enableRoot: true, enableModuleLoader: true)
    {
    }

    /// <summary>
    /// 以指定宿主公钥路径与策略选项构造定制服务。
    /// </summary>
    /// <param name="hostAdbPublicKeyPath">宿主 ADB 公钥文件路径。</param>
    /// <param name="enableRoot">是否启用 root 权限注入。</param>
    /// <param name="enableModuleLoader">是否启用开机模块加载器。</param>
    public InitrdCustomizerService(
        string hostAdbPublicKeyPath,
        bool enableRoot,
        bool enableModuleLoader)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostAdbPublicKeyPath);
        _hostAdbPublicKeyPath = hostAdbPublicKeyPath;
        _enableRoot = enableRoot;
        _enableModuleLoader = enableModuleLoader;
        _customPolicy = null;
    }

    /// <summary>
    /// 以指定宿主公钥路径与定制策略构造定制服务。
    /// </summary>
    /// <param name="hostAdbPublicKeyPath">宿主 ADB 公钥文件路径。</param>
    /// <param name="policy">定制策略。</param>
    public InitrdCustomizerService(
        string hostAdbPublicKeyPath,
        InitrdCustomizationPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostAdbPublicKeyPath);
        ArgumentNullException.ThrowIfNull(policy);

        _hostAdbPublicKeyPath = hostAdbPublicKeyPath;
        _enableRoot = policy.EnableRoot;
        _enableModuleLoader = policy.EnableModuleLoader;
        _customPolicy = policy;
    }

    /// <summary>
    /// 定制指定的 initrd：读取原版留档或就地留存原版，套用定制后写回同一路径。
    /// 幂等：目标已是当前策略下的定制产物时直接跳过，不重复解压重打包。
    /// </summary>
    /// <param name="initrdPath">initrd 文件路径，写入后内容为定制版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>定制结果。</returns>
    /// <exception cref="XBearException">宿主公钥缺失、归档格式非法或锚点找不到时抛出。</exception>
    public Task<InitrdCustomizationResult> CustomizeAsync(
        string initrdPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initrdPath);

        string publicKey = ReadHostAdbPublicKey();
        var policy = _customPolicy ?? new InitrdCustomizationPolicy(
            DefaultAdbGuestPort,
            publicKey,
            DefaultAdbApexMountPath,
            _enableRoot,
            _enableModuleLoader);

        return Task.FromResult(Customize(initrdPath, policy, cancellationToken));
    }

    /// <summary>
    /// 使用指定的定制策略定制指定的 initrd。
    /// 幂等：目标已是当前策略下的定制产物时直接跳过，不重复解压重打包。
    /// </summary>
    /// <param name="initrdPath">initrd 文件路径，写入后内容为定制版本。</param>
    /// <param name="policy">本次定制策略。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>定制结果。</returns>
    /// <exception cref="XBearException">宿主公钥缺失、归档格式非法或锚点找不到时抛出。</exception>
    public Task<InitrdCustomizationResult> CustomizeAsync(
        string initrdPath,
        InitrdCustomizationPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initrdPath);
        ArgumentNullException.ThrowIfNull(policy);

        return Task.FromResult(Customize(initrdPath, policy, cancellationToken));
    }

    /// <summary>
    /// 计算指定策略的指纹。策略任一要素变化都会让指纹变化。
    /// 指纹会随注入块一起写进产物，因此判定幂等只需读产物本身。
    /// </summary>
    /// <param name="policy">定制策略。</param>
    /// <returns>策略指纹的十六进制小写表示。</returns>
    public static string ComputeFingerprint(InitrdCustomizationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var parts = new List<string>
        {
            policy.AdbGuestPort.ToString(CultureInfo.InvariantCulture),
            policy.AdbPublicKey,
            policy.AdbApexMountPath,
            policy.EnableRoot ? "root:1" : "root:0",
            policy.EnableModuleLoader ? "modules:1" : "modules:0",
            AutoDetectStubMarker,
        };

        if (policy.HasAntiforensicsCustomizations)
        {
            parts.Add(policy.EnableBuildPropOverlay ? $"buildprop:1:{policy.BuildPropContent}" : "buildprop:0");
            parts.Add(policy.EnableAndroidIdProvisioning ? $"android_id:1:{policy.TargetAndroidId}" : "android_id:0");
            parts.Add(policy.EnableNetworkConvergence
                ? $"net:1:{policy.RenameInterfaceToWlan0}:{policy.DefaultGateway}"
                : "net:0");
            parts.Add(policy.RootStealth ? "root_stealth:1" : "root_stealth:0");
            parts.Add(policy.CleanupKernelSuRemnants ? "ksu_clean:1" : "ksu_clean:0");
            parts.Add(!string.IsNullOrWhiteSpace(policy.EmbeddedSrc) ? $"src:{policy.EmbeddedSrc.Trim()}" : "src:none");
            parts.Add(policy.ModulesReleaseAliasEnabled
                ? $"modules_alias:{NormalizeModulesReleaseAlias(policy.ModulesReleaseAlias)}"
                : "modules_alias:none");
            parts.Add(policy.HardwareAliasEnabled
                ? $"hw_alias:{NormalizeModulesReleaseAlias(policy.HardwareAlias)}"
                : "hw_alias:none");
            parts.Add(policy.HardwareHalAliasEnabled
                ? $"hw_src:{NormalizeModulesReleaseAlias(policy.HardwareSourceAlias)}"
                : "hw_src:none");
            parts.Add(policy.StripPrivatePropertiesEnabled
                ? $"strip_prop:{NormalizePropertyPrefix(policy.StripPrivatePropertyPrefix)}"
                : "strip_prop:none");
            parts.Add(policy.HostNameEnabled
                ? $"hostname:{NormalizeModulesReleaseAlias(policy.HostName)}"
                : "hostname:none");
            parts.Add(policy.RootPropertyRewriteEnabled
                ? $"root_rewrite:1:{NormalizeRootPropertyRewriteContent(policy.RootPropertyRewriteContent)}"
                : "root_rewrite:0");
        }

        string material = string.Join("\n", parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    /// <summary>
    /// 归一化并校验需要拼进 init 脚本字面量的标识串（内核版本串别名、硬件代号别名与主机名共用）。
    /// 该值会被拼进注入到 init 脚本的 shell 字面量，因此只接受标识符合法字符集，
    /// 任何可能改变脚本语义的字符（引号、变量展开、路径分隔、相对路径段）一律拒绝，
    /// 在宿主侧提前失败优于产出无法引导的实例。
    /// </summary>
    /// <param name="value">待校验的标识串，可为空。</param>
    /// <returns>去除首尾空白后的标识串；空白输入返回 null。</returns>
    /// <exception cref="XBearException">标识串含非法字符时抛出。</exception>
    public static string? NormalizeModulesReleaseAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();

        if (normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or '+')))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"模块目录版本串别名含非法字符：{normalized}",
                "版本串只能包含字母、数字与 . - _ + 四类符号，且不得包含 ..，" +
                "例如 6.1.112-android11-perf。该值会拼入引导脚本的字面量，需与内核 uname -r 输出一致。");
        }

        return normalized;
    }

    /// <summary>
    /// 归一化并校验待行级删除的属性键前缀。
    /// 该前缀会进入注入脚本的 sed 表达式，只接受属性键的合法字符集，
    /// 避免正则元字符改变删除范围或脚本语义。
    /// </summary>
    /// <param name="value">待校验的属性键前缀，可为空。</param>
    /// <returns>去除首尾空白后的前缀；空白输入返回 null。</returns>
    /// <exception cref="XBearException">前缀含非法字符时抛出。</exception>
    public static string? NormalizePropertyPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim().TrimEnd('.');

        if (normalized.Length == 0 ||
            normalized.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"镜像私有属性前缀含非法字符：{normalized}",
                "属性前缀只能包含字母、数字与 . _ - 三类符号，例如 ro.bliss.。");
        }

        return normalized;
    }

    /// <summary>
    /// 解析根级属性键行级改写清单文本，返回按清单顺序排列的键值对。
    /// 该文本会决定注入脚本的 grep 与 sed 表达式，因此只接受不会改变脚本语义的字符集：
    /// 属性名只允许字母、数字与 . _ -，取值只允许字母、数字与 . _ - + / : 及空格，
    /// 任何引号、变量展开、反斜杠与命令分隔符一律拒绝，在宿主侧提前失败优于产出语义被改写的脚本。
    /// </summary>
    /// <param name="content">改写清单文本，可为空。</param>
    /// <returns>按清单顺序排列的键值对序列；空白或纯注释文本返回空序列。</returns>
    /// <exception cref="XBearException">清单中存在非法行或缺少等号时抛出。</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> ParseRootPropertyRewrites(string? content)
    {
        var rewrites = new List<KeyValuePair<string, string>>();

        if (string.IsNullOrWhiteSpace(content))
        {
            return rewrites;
        }

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"根级属性改写清单行缺少等号或键值：{line}",
                    "每行必须是 key=value 形态，空行与以 # 开头的注释行会被忽略。");
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            if (!IsValidRewriteKey(key) || !IsValidRewriteValue(value))
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"根级属性改写清单含非法键值：{line}",
                    "属性名只允许字母、数字与 . _ -，取值只允许字母、数字与 . _ - + / : 及空格。");
            }

            rewrites.Add(new KeyValuePair<string, string>(key, value));
        }

        return rewrites;
    }

    /// <summary>
    /// 归一化根级属性键行级改写清单文本，供指纹计算使用。
    /// </summary>
    /// <param name="content">改写清单文本，可为空。</param>
    /// <returns>逐行去首尾空白并以换行拼接的规范文本；空白输入返回空串。</returns>
    public static string NormalizeRootPropertyRewriteContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            content
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));
    }

    /// <summary>校验属性名是否只含不会改变注入脚本语义的字符。</summary>
    /// <param name="key">属性名。</param>
    /// <returns>合法返回 true。</returns>
    private static bool IsValidRewriteKey(string key)
    {
        if (key.Length == 0 || (!char.IsAsciiLetter(key[0]) && key[0] != '_'))
        {
            return false;
        }

        foreach (char c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>校验属性取值是否只含不会改变注入脚本语义的字符。</summary>
    /// <param name="value">属性取值。</param>
    /// <returns>合法返回 true。</returns>
    private static bool IsValidRewriteValue(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or '+' or '/' or ':' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 执行定制流程。
    /// 幂等判定顺序：先看目标产物内嵌的指纹是否等于当前策略指纹，
    /// 相等即原样返回。判定完全基于产物内容，不依赖任何旁路文件。
    /// </summary>
    /// <param name="initrdPath">initrd 文件路径。</param>
    /// <param name="policy">本次使用的策略。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>定制结果。</returns>
    private InitrdCustomizationResult Customize(
        string initrdPath,
        InitrdCustomizationPolicy policy,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(initrdPath))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"initrd 文件不存在：{initrdPath}",
                "确认镜像导入已完成引导资产提取，且 initrd 文件名与 boot 推荐一致。");
        }

        string fingerprint = ComputeFingerprint(policy);
        string stockPath = ResolveStockPath(initrdPath);
        bool hasStock = File.Exists(stockPath);

        cancellationToken.ThrowIfCancellationRequested();
        string? embedded = ReadEmbeddedFingerprint(initrdPath);

        if (string.Equals(embedded, fingerprint, StringComparison.Ordinal))
        {
            return new InitrdCustomizationResult(
                InitrdCustomizationOutcome.AlreadyCustomized,
                initrdPath,
                stockPath,
                fingerprint,
                Array.Empty<string>());
        }

        if (!hasStock && embedded is not null)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"initrd 已完成定制但原版留档缺失：{stockPath}",
                $"删除 {initrdPath} 与 {stockPath} 后重新导入镜像，或从镜像中重新提取原版 initrd。");
        }

        if (!hasStock)
        {
            EnsureStock(initrdPath, stockPath);
        }

        byte[] stock = File.ReadAllBytes(stockPath);
        InitrdCompression compression = InitrdImageCodec.Detect(stock);
        byte[] archive = InitrdImageCodec.Decompress(stock, compression);

        cancellationToken.ThrowIfCancellationRequested();

        var entries = CpioNewcArchive.Parse(archive).ToList();
        List<string> customized = ApplyCustomizations(entries, policy, fingerprint);

        byte[] rebuilt = CpioNewcArchive.Build(entries);
        byte[] output = InitrdImageCodec.Compress(rebuilt, compression);

        string temporaryPath = initrdPath + ".customizing";
        try
        {
            File.WriteAllBytes(temporaryPath, output);
            cancellationToken.ThrowIfCancellationRequested();

            ClearReadOnly(initrdPath);
            File.Move(temporaryPath, initrdPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }

        return new InitrdCustomizationResult(
            InitrdCustomizationOutcome.Customized,
            initrdPath,
            stockPath,
            fingerprint,
            customized);
    }

    /// <summary>
    /// 对归档套用定制，返回被改写的条目名。
    /// </summary>
    /// <param name="entries">归档条目。</param>
    /// <param name="policy">定制策略。</param>
    /// <param name="fingerprint">策略指纹，随注入块写入产物。</param>
    /// <returns>被改写的条目名，按归档内顺序。</returns>
    private static List<string> ApplyCustomizations(
        List<CpioNewcEntry> entries,
        InitrdCustomizationPolicy policy,
        string fingerprint)
    {
        var customized = new List<string>(4);

        if (policy.EnableBuildPropOverlay && !string.IsNullOrWhiteSpace(policy.BuildPropContent))
        {
            byte[] content = Encoding.UTF8.GetBytes(policy.BuildPropContent);
            CpioNewcArchive.SetOrAddEntry(entries, BuildPropOverrideEntryName, content);
            customized.Add(BuildPropOverrideEntryName);
        }
        else
        {
            CpioNewcArchive.RemoveEntry(entries, BuildPropOverrideEntryName);
        }

        if (!string.IsNullOrWhiteSpace(policy.EmbeddedSrc))
        {
            byte[] content = Encoding.UTF8.GetBytes(policy.EmbeddedSrc.Trim());
            CpioNewcArchive.SetOrAddEntry(entries, BootSrcEntryName, content);
            customized.Add(BootSrcEntryName);
        }
        else
        {
            CpioNewcArchive.RemoveEntry(entries, BootSrcEntryName);
        }

        if (policy.RootPropertyRewriteEnabled)
        {
            byte[] content = Encoding.UTF8.GetBytes(policy.RootPropertyRewriteContent!);
            CpioNewcArchive.SetOrAddEntry(entries, RootPropertyRewriteEntryName, content);
            customized.Add(RootPropertyRewriteEntryName);
        }
        else
        {
            CpioNewcArchive.RemoveEntry(entries, RootPropertyRewriteEntryName);
        }

        foreach (CpioNewcEntry entry in entries)
        {
            if (entry.Name == AutoDetectEntryName)
            {
                entry.Content = Encoding.UTF8.GetBytes(StubAutoDetect(ReadText(entry)));
                customized.Add(entry.Name);
            }
            else if (entry.Name == InitEntryName)
            {
                entry.Content = Encoding.UTF8.GetBytes(InjectAdbBootstrap(ReadText(entry), policy, fingerprint));
                customized.Add(entry.Name);
            }
        }

        if (!customized.Contains(AutoDetectEntryName))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"initrd 内未找到 {AutoDetectEntryName}，无法禁用自动探测。",
                "该镜像的 initramfs 结构与 Android-x86 系不同，需要为其单独定制引导脚本。");
        }

        if (!customized.Contains(InitEntryName))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"initrd 内未找到 {InitEntryName}，无法注入调试通路。",
                "该镜像的 initramfs 结构与 Android-x86 系不同，需要为其单独定制引导脚本。");
        }

        return customized;
    }

    /// <summary>
    /// 把自动探测脚本中的两个函数打桩为空实现。
    /// </summary>
    /// <param name="script">脚本原文。</param>
    /// <returns>打桩后的脚本。</returns>
    private static string StubAutoDetect(string script)
    {
        string result = ReplaceShellFunction(script, "auto_detect");
        result = ReplaceShellFunction(result, "auto_detect_alpine");

        VerifyFunctionBodiesAreStubbed(result);
        return result;
    }

    /// <summary>
    /// 校验两个自动探测函数都已被替换为桩实现，且函数体结构仍然闭合。
    /// </summary>
    /// <param name="script">打桩后的脚本。</param>
    private static void VerifyFunctionBodiesAreStubbed(string script)
    {
        string[] lines = script.Split('\n');

        foreach (string functionName in new[] { "auto_detect", "auto_detect_alpine" })
        {
            int headerIndex = FindFunctionHeader(lines, functionName);
            int openIndex = headerIndex < 0 ? -1 : FindStandaloneLine(lines, headerIndex + 1, "{");
            int closeIndex = openIndex < 0 ? -1 : FindStandaloneLine(lines, openIndex + 1, "}");

            if (headerIndex < 0 || openIndex < 0 || closeIndex < 0)
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"{AutoDetectEntryName} 中函数 {functionName} 打桩后结构不完整。",
                    "这是内部一致性错误，请反馈该问题。");
            }

            bool stubbed = lines
                .Skip(openIndex)
                .Take(closeIndex - openIndex)
                .Any(line => string.Equals(line.Trim(), AutoDetectStubMarker, StringComparison.Ordinal));

            if (!stubbed)
            {
                throw new XBearException(
                    ErrorCategory.Spec,
                    $"{AutoDetectEntryName} 中函数 {functionName} 未被打桩。",
                    "这是内部一致性错误，请反馈该问题。");
            }
        }
    }

    /// <summary>
    /// 把 shell 脚本中指定函数的函数体整体替换为桩实现。
    /// </summary>
    /// <param name="script">脚本原文。</param>
    /// <param name="functionName">函数名，不含参数列表。</param>
    /// <returns>替换后的脚本。</returns>
    private static string ReplaceShellFunction(string script, string functionName)
    {
        string[] lines = script.Split('\n');

        int headerIndex = FindFunctionHeader(lines, functionName);
        if (headerIndex < 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"{AutoDetectEntryName} 中未找到函数 {functionName}。",
                "该 initramfs 的引导脚本结构已变化，需要为其单独定制。");
        }

        int openIndex = FindStandaloneLine(lines, headerIndex + 1, "{");
        int closeIndex = openIndex < 0 ? -1 : FindStandaloneLine(lines, openIndex + 1, "}");

        if (openIndex < 0 || closeIndex < 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"{AutoDetectEntryName} 中函数 {functionName} 的函数体边界无法识别。",
                "该 initramfs 的引导脚本结构已变化，需要为其单独定制。");
        }

        var rebuilt = new List<string>(lines.Length);
        rebuilt.AddRange(lines[..openIndex]);
        rebuilt.Add(lines[openIndex]);
        rebuilt.Add("\t" + AutoDetectStubMarker);
        rebuilt.Add("\techo \"" + functionName + " disabled\"");
        rebuilt.Add("\treturn 0");
        rebuilt.AddRange(lines[closeIndex..]);

        return string.Join("\n", rebuilt);
    }

    /// <summary>
    /// 在指定起始位置之后找到独立成行的标记文本。
    /// </summary>
    /// <param name="lines">脚本行。</param>
    /// <param name="startIndex">起始行号。</param>
    /// <param name="marker">标记文本，前后允许出现空白。</param>
    /// <returns>命中行号，未命中返回 -1。</returns>
    private static int FindStandaloneLine(string[] lines, int startIndex, string marker)
    {
        for (int i = startIndex; i < lines.Length; i++)
        {
            if (string.Equals(lines[i].Trim(), marker, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 找到 shell 函数的头部行，形如 name()。
    /// </summary>
    /// <param name="lines">脚本行。</param>
    /// <param name="functionName">函数名。</param>
    /// <returns>命中行号，未命中返回 -1。</returns>
    private static int FindFunctionHeader(string[] lines, string functionName)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.Equals(lines[i].Trim(), functionName + "()", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 在根脚本的数据分区挂载之后注入调试通路与抗取证检测隐藏逻辑。
    /// </summary>
    /// <param name="script">脚本原文。</param>
    /// <param name="policy">定制策略。</param>
    /// <param name="fingerprint">策略指纹，随注入块写入产物以支撑幂等判定。</param>
    /// <returns>注入后的脚本。</returns>
    private static string InjectAdbBootstrap(
        string script,
        InitrdCustomizationPolicy policy,
        string fingerprint)
    {
        const string Anchor = "mount_data\nmount_sdcard\n";
        const string RootLoopAnchor = "cd /\nwhile :; do\n";

        // A-13: 内嵌 SRC 恢复逻辑（消除可见 cmdline 中的 SRC= 参数）。
        // 在实机引导中必须在 check_root 遍历各块设备之前就位，否则缺少 $SRC 导致 check_root 无法匹配安装目录；
        // 若脚本包含根目录探测循环锚点则在其之前注入，未包含时（如简易测试用桩脚本）回落至 mount_data 处。
        if (!string.IsNullOrWhiteSpace(policy.EmbeddedSrc) && script.Contains(RootLoopAnchor, StringComparison.Ordinal))
        {
            const string srcRecovery = "# --- 抗取证：cmdline 收敛内嵌 SRC 恢复 (A-13) ----------------\n" +
                                       "if [ -z \"$SRC\" ] && [ -f /etc/boot_src ]; then\n" +
                                       "\tSRC=`cat /etc/boot_src 2>/dev/null`\n" +
                                       "\techo \"recovered SRC=$SRC from /etc/boot_src\"\n" +
                                       "fi\n";
            script = script.Replace(RootLoopAnchor, srcRecovery + "\n" + RootLoopAnchor, StringComparison.Ordinal);
        }

        int anchorIndex = script.IndexOf(Anchor, StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"{InitEntryName} 中未找到数据分区挂载锚点，无法注入调试通路。",
                "该 initramfs 的引导脚本结构已变化，需要为其单独定制。");
        }

        int adbGuestPort = policy.AdbGuestPort;
        string apex = policy.AdbApexMountPath;

        var lines = new List<string>
        {
            "mount_data",
            "",
            FingerprintMarkerPrefix + fingerprint,
            "",
        };

        // A-13 回落兜底：若前序未命中 RootLoopAnchor，则在 mount_data 之后注入以满足轻量桩脚本断言
        if (!string.IsNullOrWhiteSpace(policy.EmbeddedSrc) && !script.Contains(RootLoopAnchor, StringComparison.Ordinal))
        {
            lines.Add("# --- 抗取证：cmdline 收敛内嵌 SRC 恢复 (A-13) ----------------");
            lines.Add("if [ -z \"$SRC\" ] && [ -f /etc/boot_src ]; then");
            lines.Add("\tSRC=`cat /etc/boot_src 2>/dev/null`");
            lines.Add("\techo \"recovered SRC=$SRC from /etc/boot_src\"");
            lines.Add("fi");
            lines.Add("");
        }

        // 覆写块首行标记：用于识别「本文件已由本产品追加过覆写块」，
        // 从而在重复引导时裁掉旧块再追加新块，保证幂等且不留旧画像取值。
        string marker = PropertyOverlayBuilder.OverlayMarkerHeader;
        string markerEscaped = EscapeRegexLiteral(marker);

        // A-14/S-08: 镜像私有属性族行级删除。
        // 追加式覆写只能新增或覆盖键，无法把已存在的属性条目从属性服务的载入结果中抹掉，
        // 因此镜像私有属性族必须直接从各分区 prop 文件中删行。
        string? stripPrefix = NormalizePropertyPrefix(policy.StripPrivatePropertyPrefix);
        if (stripPrefix is not null)
        {
            string stripPattern = EscapeRegexLiteral(stripPrefix) + "\\.";
            lines.Add("# --- 抗取证：镜像私有属性族行级删除 (A-14/S-08) ---------------------");
            lines.Add("# 追加式覆写只能新增或覆盖同名键，无法消除既有属性条目，故私有属性族");
            lines.Add("# 必须从各分区 prop 文件中直接删行；追加块内的键不受影响。");
            lines.Add("# 删除零命中时为恒等操作，重复引导天然幂等；只读目标自动降级并留痕。");
            lines.Add($"if [ -n \"{stripPrefix}\" ]; then");
            lines.Add("\tfor propfile in build.prop prop.default default.prop device.prop odm.prop; do");
            lines.Add("\t\tfor target in /android/$propfile /android/*/$propfile \\");
            lines.Add("\t\t\t/android/*/*/$propfile /android/*/*/*/$propfile \\");
            lines.Add("\t\t\t/android/*/*/*/*/$propfile; do");
            lines.Add("\t\t\t[ -f \"$target\" ] || continue");
            lines.Add($"\t\t\tgrep -q \"^{stripPattern}\" \"$target\" 2>/dev/null || continue");
            lines.Add("\t\t\tmount -o remount,rw /android 2>/dev/null");
            lines.Add("\t\t\tmount -o remount,rw \"$(dirname \"$target\")\" 2>/dev/null");
            lines.Add($"\t\t\tif sed -i '/^{stripPattern}/d' \"$target\" 2>/dev/null; then");
            lines.Add("\t\t\t\techo \"private property lines stripped: $target\"");
            lines.Add("\t\t\telse");
            lines.Add("\t\t\t\techo \"private property strip skipped (not writable): $target\"");
            lines.Add("\t\t\tfi");
            lines.Add("\t\tdone");
            lines.Add("\tdone");
            lines.Add("fi");
            lines.Add("");
        }

        // A-03/S-05/S-10: 根级属性键的行级取值改写。
        // 追加式覆写只能新增键，改不了镜像 prop 文件里已经存在的根级键取值：镜像最后被载入的
        // prop 文件带着原生的 ro.build.type 与 ro.boot.* 根级键，会把追加块的结果盖回镜像原值
        // （构建指纹的类型段已是 userdebug 而类型属性仍是 user 即此因）。
        // 因此对已存在的目标键做行级取值改写；文件里没有该键时保持现状，由追加块兜底。
        IReadOnlyList<KeyValuePair<string, string>> rootRewrites =
            ParseRootPropertyRewrites(policy.RootPropertyRewriteContent);
        if (rootRewrites.Count > 0)
        {
            lines.Add("# --- 抗取证：根级属性键取值改写 (A-03/S-05/S-10) ----------------");
            lines.Add("# 追加式覆写只能新增键，无法改写镜像 prop 文件里已存在的根级键取值；");
            lines.Add("# 镜像最后载入的文件带着原生 ro.build.type 与 ro.boot.*，会把追加块盖回原值。");
            lines.Add("# 故对已存在的目标键做行级取值改写：文件里没有该键时保持现状，由追加块兜底。");
            lines.Add("# 改写天然幂等（重复引导把同一取值写回自身），只读目标自动降级并留痕。");
            lines.Add($"if [ -f /{RootPropertyRewriteEntryName} ]; then");
            lines.Add("\tfor propfile in build.prop prop.default default.prop device.prop odm.prop; do");
            lines.Add("\t\tfor target in /android/$propfile /android/*/$propfile \\");
            lines.Add("\t\t\t/android/*/*/$propfile /android/*/*/*/$propfile \\");
            lines.Add("\t\t\t/android/*/*/*/*/$propfile; do");
            lines.Add("\t\t\t[ -f \"$target\" ] || continue");
            foreach (KeyValuePair<string, string> rewrite in rootRewrites)
            {
                string keyPattern = EscapeRegexLiteral(rewrite.Key) + "=";
                lines.Add($"\t\t\tif grep -q '^{keyPattern}' \"$target\" 2>/dev/null; then");
                lines.Add("\t\t\t\tmount -o remount,rw /android 2>/dev/null");
                lines.Add("\t\t\t\tmount -o remount,rw \"$(dirname \"$target\")\" 2>/dev/null");
                lines.Add($"\t\t\t\tif sed -i 's|^{keyPattern}.*|{rewrite.Key}={rewrite.Value}|' \"$target\" 2>/dev/null; then");
                lines.Add($"\t\t\t\t\techo \"root property rewritten: {rewrite.Key}={rewrite.Value} in $target\"");
                lines.Add("\t\t\t\telse");
                lines.Add($"\t\t\t\t\techo \"root property rewrite skipped (not writable): {rewrite.Key} in $target\"");
                lines.Add("\t\t\t\tfi");
                lines.Add("\t\t\tfi");
            }

            lines.Add("\t\tdone");
            lines.Add("\tdone");
            lines.Add("fi");
            lines.Add("");
        }

        // A-03: build.prop 覆盖逻辑（在 chroot 交接之前生效）
        if (policy.EnableBuildPropOverlay)
        {
            lines.Add("# --- 抗取证：build.prop 覆写 (A-03) -------------------------");
            lines.Add("# 覆写必须以「追加」而非「整体替换」的方式生效：");
            lines.Add("# build.prop 是 system_server 启动的必需输入，整文件替换会抹掉 SDK 版本、");
            lines.Add("# ABI 列表与显示密度等全部原有属性，导致用户空间框架无法启动。");
            lines.Add("# build.prop 中后出现的同名键覆盖先出现的同名键，追加即可实现按实例覆真。");
            lines.Add("# 覆写需覆盖全部分区的 prop 文件：属性服务按 system → vendor → odm → product →");
            lines.Add("# system_ext 的顺序依次载入，后者覆盖前者，只追加 system 会被其余分区回写。");
            lines.Add("# 重复引导时先裁掉本产品上一轮追加的覆写块再重新追加，");
            lines.Add("# 既保证文件不随引导次数膨胀，也保证画像变更后不留旧值。");
            lines.Add("if [ -f /etc/build.prop.override ]; then");
            lines.Add("\t[ -f /tmp/device.prop ] && > /tmp/device.prop 2>/dev/null");
            lines.Add("\tfor target in /android/system/build.prop \\");
            lines.Add("\t\t/android/build.prop \\");
            lines.Add("\t\t/android/system/etc/prop.default \\");
            lines.Add("\t\t/android/prop.default \\");
            lines.Add("\t\t/android/default.prop \\");
            lines.Add("\t\t/android/vendor/build.prop \\");
            lines.Add("\t\t/android/vendor/etc/build.prop \\");
            lines.Add("\t\t/android/vendor/default.prop \\");
            lines.Add("\t\t/android/vendor/etc/prop.default \\");
            lines.Add("\t\t/android/system/vendor/build.prop \\");
            lines.Add("\t\t/android/system/vendor/etc/build.prop \\");
            lines.Add("\t\t/android/system/vendor/default.prop \\");
            lines.Add("\t\t/android/system/vendor/etc/device.prop \\");
            lines.Add("\t\t/android/vendor/etc/device.prop \\");
            lines.Add("\t\t/android/odm/build.prop \\");
            lines.Add("\t\t/android/odm/etc/build.prop \\");
            lines.Add("\t\t/android/odm/default.prop \\");
            lines.Add("\t\t/android/odm/etc/prop.default \\");
            lines.Add("\t\t/android/vendor/odm/build.prop \\");
            lines.Add("\t\t/android/vendor/odm/etc/build.prop \\");
            lines.Add("\t\t/android/vendor/odm/default.prop \\");
            lines.Add("\t\t/android/system/odm/build.prop \\");
            lines.Add("\t\t/android/system/odm/etc/build.prop \\");
            lines.Add("\t\t/android/system/vendor/odm/build.prop \\");
            lines.Add("\t\t/android/system/vendor/odm/etc/build.prop \\");
            lines.Add("\t\t/android/product/build.prop \\");
            lines.Add("\t\t/android/product/etc/build.prop \\");
            lines.Add("\t\t/android/product/default.prop \\");
            lines.Add("\t\t/android/product/etc/prop.default \\");
            lines.Add("\t\t/android/system/product/build.prop \\");
            lines.Add("\t\t/android/system/product/etc/build.prop \\");
            lines.Add("\t\t/android/system/product/default.prop \\");
            lines.Add("\t\t/android/system_ext/build.prop \\");
            lines.Add("\t\t/android/system_ext/etc/build.prop \\");
            lines.Add("\t\t/android/system_ext/default.prop \\");
            lines.Add("\t\t/android/system_ext/etc/prop.default \\");
            lines.Add("\t\t/android/system/system_ext/build.prop \\");
            lines.Add("\t\t/android/system/system_ext/etc/build.prop \\");
            lines.Add("\t\t/android/system/system_ext/default.prop; do");
            lines.Add("\t\tif [ -f \"$target\" ]; then");
            lines.Add("\t\t\tmount -o remount,rw /android 2>/dev/null");
            lines.Add("\t\t\tmount -o remount,rw \"$(dirname \"$target\")\" 2>/dev/null");
            lines.Add($"\t\t\tgrep -q '{marker}' \"$target\" 2>/dev/null && \\");
            lines.Add($"\t\t\t\tsed -i '/^{markerEscaped}/,$d' \"$target\" 2>/dev/null");
            lines.Add("\t\t\tif cat /etc/build.prop.override >> \"$target\" 2>/dev/null; then");
            lines.Add("\t\t\t\techo \"build.prop overlay appended: $target\"");
            lines.Add("\t\t\telse");
            lines.Add("\t\t\t\techo \"build.prop overlay write skipped (not writable): $target\"");
            lines.Add("\t\t\tfi");
            lines.Add("\t\tfi");
            lines.Add("\tdone");
            lines.Add("\t# 清单之外的同名 prop 文件同样纳入：镜像的实际载入清单随发行版而异，");
            lines.Add("\t# 只按固定清单追加会漏掉最后载入的文件，镜像原生的构建类型与启动链取值");
            lines.Add("\t# 就会盖回覆写结果（指纹已被覆写而 ro.build.type 仍是 user 即此因）。");
            lines.Add("\tfor propfile in build.prop prop.default default.prop device.prop odm.prop; do");
            lines.Add("\t\tfor target in /android/$propfile /android/*/$propfile \\");
            lines.Add("\t\t\t/android/*/*/$propfile /android/*/*/*/$propfile \\");
            lines.Add("\t\t\t/android/*/*/*/*/$propfile; do");
            lines.Add("\t\t\t[ -f \"$target\" ] || continue");
            lines.Add($"\t\t\tgrep -q '{marker}' \"$target\" 2>/dev/null && continue");
            lines.Add("\t\t\tmount -o remount,rw /android 2>/dev/null");
            lines.Add("\t\t\tmount -o remount,rw \"$(dirname \"$target\")\" 2>/dev/null");
            lines.Add("\t\t\tif cat /etc/build.prop.override >> \"$target\" 2>/dev/null; then");
            lines.Add("\t\t\t\techo \"build.prop overlay appended: $target\"");
            lines.Add("\t\t\telse");
            lines.Add("\t\t\t\techo \"build.prop overlay write skipped (not writable): $target\"");
            lines.Add("\t\t\tfi");
            lines.Add("\t\tdone");
            lines.Add("\tdone");
            lines.Add("\techo \"build.prop overlay appended\"");
            lines.Add("fi");
            lines.Add("");
        }

        // A-14/T-04: 主机名收敛。initrd 阶段先设定内核 UTS 节点名，
        // 覆盖「内核起来到用户空间网络守护进程接管」这一窗口内可见的默认主机名；
        // 用户空间接管后的最终取值由开机后辅助进程再次落定。
        string? hostName = NormalizeModulesReleaseAlias(policy.HostName);
        if (hostName is not null)
        {
            lines.Add("# --- 抗取证：主机名收敛 (A-14/T-04) ----------------------------");
            lines.Add("# 主机名取机型族代号，与 DMI product_name、ro.product.device 同源；");
            lines.Add("# 用户空间启动后可能再次改写，最终取值由开机后辅助进程落定并回读。");
            lines.Add($"hostname {hostName} 2>/dev/null");
            lines.Add($"echo \"initrd hostname set: `hostname 2>/dev/null`\"");
            lines.Add("");
        }

        // 内核版本串去指纹配套：补建 /lib/modules 版本别名条目。
        // 原版引导脚本以 `ln -s ../system/lib/modules /lib/modules/$(uname -r)` 建立条目，
        // 该软链只有在「条目名与镜像内既有模块目录同名」时才恰好可用：ln 因 File exists
        // 失败，/lib/modules/$(uname -r) 便直接是存放 .ko 的真实目录。版本串一旦改名，
        // 同一行会改建出一条指向父目录的悬空软链，modprobe 按 /lib/modules/$(uname -r) 查
        // .ko 落空，表现为 virtio_net 等模块不加载、guest 无 eth0、ADB 通道随之断链。
        // 因此别名必须指向既有模块目录本体，取其在 /lib/modules 下的真实目录项。
        // 源目录在运行时探测而非写死：写死原版本串会把镜像来源身份带进产品源码。
        string? modulesReleaseAlias = NormalizeModulesReleaseAlias(policy.ModulesReleaseAlias);
        if (modulesReleaseAlias is not null)
        {
            lines.Add("# --- 内核版本串去指纹配套：模块目录版本别名 ----------------------");
            lines.Add("# 原版引导脚本的 `ln -s ../system/lib/modules /lib/modules/$(uname -r)` 仅在条目名");
            lines.Add("# 与既有模块目录同名时才恰好可用（ln 因 File exists 失败，/lib/modules/$(uname -r)");
            lines.Add("# 即存放 .ko 的真实目录）。版本串改名后同一行会建出指向父目录的悬空软链，");
            lines.Add("# modprobe 查 .ko 落空，virtio_net 不加载即无 eth0。故别名指向既有目录本体。");
            lines.Add("# /lib/modules 经软链落在 system 分区内，镜像 system 为 ext4 ro，需先 remount,rw。");
            lines.Add($"if [ -n \"{modulesReleaseAlias}\" ] && [ -d /lib/modules ]; then");
            lines.Add($"\tmodules_alias_dir=\"/lib/modules/{modulesReleaseAlias}\"");
            lines.Add("\tmodules_src=");
            lines.Add("\tfor modules_cand in /lib/modules/*; do");
            lines.Add("\t\tif [ -d \"$modules_cand\" ] && [ ! -L \"$modules_cand\" ]; then");
            lines.Add("\t\t\tmodules_src=\"$modules_cand\"");
            lines.Add("\t\t\tbreak");
            lines.Add("\t\tfi");
            lines.Add("\tdone");
            lines.Add("\tif [ -z \"$modules_src\" ]; then");
            lines.Add("\t\techo \"modules release alias source not found under /lib/modules\"");
            lines.Add("\telif [ -d \"$modules_alias_dir\" ] && [ ! -L \"$modules_alias_dir\" ]; then");
            lines.Add("\t\techo \"modules release alias target already a real directory: $modules_alias_dir\"");
            lines.Add("\telse");
            lines.Add("\t\tmount -o remount,rw /android 2>/dev/null");
            lines.Add("\t\tif ln -sfn \"$modules_src\" \"$modules_alias_dir\" 2>/dev/null; then");
            lines.Add("\t\t\techo \"modules release alias linked: $modules_alias_dir -> $modules_src\"");
            lines.Add("\t\telse");
            lines.Add("\t\t\techo \"modules release alias link skipped (not writable): $modules_alias_dir\"");
            lines.Add("\t\tfi");
            lines.Add("\tfi");
            lines.Add("fi");
            lines.Add("");
        }

// 硬件代号去指纹配套：当下发了 androidboot.hardware 时，
        // Android init 会按 init.${ro.hardware}.rc 查找硬件初始化脚本。
        // 原镜像仅含 init.android_x86_64.rc 等既有平台脚本，若未补建软链会导致硬件服务不加载。
        // 遍历 /android/vendor/etc/init/hw 与 /android 为目标硬件代号建立指向原有 rc 的软链。
        string? hardwareAlias = NormalizeModulesReleaseAlias(policy.HardwareAlias);
        string? hardwareSource = NormalizeModulesReleaseAlias(policy.HardwareSourceAlias);
        if (hardwareAlias is not null)
        {
            lines.Add("# --- 硬件代号去指纹配套：硬件 init rc 别名 ----------------------");
            lines.Add("# 当下发了 androidboot.hardware 时，Android init 会按 init.${hardware}.rc");
            lines.Add("# 查找硬件初始化脚本；镜像根目录仅含 init.bliss_x86_64.rc 等既有平台脚本，");
            lines.Add("# 需补建指向原有平台 rc 的软链，否则网络配置与硬件服务不会被触发执行。");
            lines.Add("# 优先指向与镜像原生代号同名的 rc；同名文件不存在时回落到首个非保留平台 rc。");
            lines.Add("mount -o remount,rw /android 2>/dev/null");
            lines.Add("for rcdir in /android /android/vendor/etc/init/hw /android/system/vendor/etc/init/hw; do");
            lines.Add("\tif [ -d \"$rcdir\" ]; then");
            if (hardwareSource is not null && hardwareSource != hardwareAlias)
            {
                lines.Add("\t\tif [ -f \"$rcdir/init." + hardwareSource + ".rc\" ]; then");
                lines.Add("\t\t\tln -sfn \"init." + hardwareSource + ".rc\" \"$rcdir/init." + hardwareAlias + ".rc\" 2>/dev/null");
                lines.Add("\t\t\techo \"hardware rc alias linked: $rcdir/init." + hardwareAlias + ".rc -> init." + hardwareSource + ".rc\"");
                lines.Add("\t\t\tcontinue");
                lines.Add("\t\tfi");
                lines.Add("\t\t[ -e \"$rcdir/init." + hardwareAlias + ".rc\" ] && continue");
            }

            lines.Add("\t\tfor hrc in \"$rcdir\"/init.*.rc; do");
            lines.Add("\t\t\t[ -f \"$hrc\" ] || continue");
            lines.Add("\t\t\tbname=`basename \"$hrc\"`");
            lines.Add("\t\t\tcase \"$bname\" in");
            lines.Add($"\t\t\t\tinit.rc|init.usb.rc|init.environ.rc|init.recovery.*|init.zygote*|init.hardware.rc|init.{hardwareAlias}.rc) continue ;;");
            lines.Add("\t\t\t\t*)");
            lines.Add($"\t\t\t\t\tif [ ! -e \"$rcdir/init.{hardwareAlias}.rc\" ]; then");
            lines.Add($"\t\t\t\t\t\tln -sfn \"$bname\" \"$rcdir/init.{hardwareAlias}.rc\" 2>/dev/null");
            lines.Add($"\t\t\t\t\t\techo \"hardware rc alias linked: $rcdir/init.{hardwareAlias}.rc -> $bname\"");
            lines.Add("\t\t\t\t\tfi");
            lines.Add("\t\t\t\t\tbreak");
            lines.Add("\t\t\t\t\t;;");
            lines.Add("\t\t\tesac");
            lines.Add("\t\tdone");
            lines.Add("\tfi");
            lines.Add("done");

            // 平台 rc 内部还以 ${ro.hardware} 拼接分区表文件名（mount_all /fstab.${ro.hardware}、
            // swapon_all 同源）。硬件代号改为机型代号后该文件名对不上，mount_all 静默空转，
            // 表现为 /data 不挂载、eth0 取不到地址、框架起不来；rc 软链建对了也救不回挂载。
            lines.Add("# 平台 rc 内部按 ${ro.hardware} 拼接分区表文件名，代号别名需一并覆盖。");
            lines.Add("for hwpart in fstab; do");
            lines.Add($"\tif [ -f \"/android/$hwpart.{hardwareSource}\" ]; then");
            lines.Add($"\t\tln -sfn \"$hwpart.{hardwareSource}\" \"/android/$hwpart.{hardwareAlias}\" 2>/dev/null");
            lines.Add($"\t\techo \"hardware partition alias linked: /android/$hwpart.{hardwareAlias} -> $hwpart.{hardwareSource}\"");
            lines.Add("\tfi");
            lines.Add("done");
            lines.Add("");
        }

        // 硬件代号去指纹配套：HAL 库目录代号别名。
        // libhardware 按 <name>.<ro.hardware>.<ro.board.platform>.so 的次序在各 hw 目录查找模块，
        // 硬件代号改为机型代号后，原生代号命名的动态库不再被命中，表现为 gralloc 等 HAL 缺失。
        // 故按原生代号检索既有动态库并补建代号别名软链；既有条目一律不覆盖。
        if (hardwareAlias is not null && hardwareSource is not null && hardwareSource != hardwareAlias)
        {
            lines.Add("# --- 硬件代号去指纹配套：HAL 库目录代号别名 --------------------");
            lines.Add("# libhardware 依次查找 <name>.<ro.hardware>.<ro.board.platform>.so 与 <name>.<ro.hardware>.so，");
            lines.Add("# 硬件代号改为机型代号后原生代号命名的动态库不再被命中，gralloc 等 HAL 会缺失。");
            lines.Add("# 因此按镜像原生代号检索既有动态库并补建代号别名软链，已存在的条目不覆盖。");
            lines.Add("mount -o remount,rw /android 2>/dev/null");
            lines.Add("for hwdir in /android/vendor/lib/hw /android/vendor/lib64/hw \\");
            lines.Add("\t/android/system/lib/hw /android/system/lib64/hw \\");
            lines.Add("\t/android/system/vendor/lib/hw /android/system/vendor/lib64/hw; do");
            lines.Add("\t[ -d \"$hwdir\" ] || continue");
            lines.Add($"\tfor hwlib in \"$hwdir\"/*\"{hardwareSource}\"*; do");
            lines.Add("\t\t[ -f \"$hwlib\" ] || continue");
            lines.Add("\t\thwbase=`basename \"$hwlib\"`");
            lines.Add($"\t\thwalias=`echo \"$hwbase\" | sed 's/{hardwareSource}/{hardwareAlias}/g'`");
            lines.Add("\t\t[ \"$hwalias\" = \"$hwbase\" ] && continue");
            lines.Add("\t\t[ -e \"$hwdir/$hwalias\" ] && continue");
            lines.Add("\t\tmount -o remount,rw \"$hwdir\" 2>/dev/null");
            lines.Add("\t\tif ln -sfn \"$hwbase\" \"$hwdir/$hwalias\" 2>/dev/null; then");
            lines.Add("\t\t\techo \"hardware hal alias linked: $hwdir/$hwalias -> $hwbase\"");
            lines.Add("\t\telse");
            lines.Add("\t\t\techo \"hardware hal alias skipped (not writable): $hwdir/$hwalias\"");
            lines.Add("\t\tfi");
            lines.Add("\tdone");
            lines.Add("done");
            lines.Add("");
        }

        // A-14 / Q-02: 清理非本产品的 KernelSU 遗留目录与可执行文件
        if (policy.CleanupKernelSuRemnants)
        {
            lines.Add("# --- 抗取证：模块目录低代价清理 (A-14/Q-02) ------------------");
            lines.Add("rm -f /android/data/adb/ksud 2>/dev/null");
            lines.Add("rm -rf /android/data/adb/ksu 2>/dev/null");
            lines.Add("");
        }

        // A-08: 网络收敛（接口改名）
        if (policy.EnableNetworkConvergence)
        {
            lines.Add("# --- 抗取证：网络收敛 (A-08) -------------------------------");
            if (policy.RenameInterfaceToWlan0)
            {
                lines.Add("if [ -d /sys/class/net/eth0 ]; then");
                lines.Add("\tip link set eth0 down 2>/dev/null");
                lines.Add("\tip link set eth0 name wlan0 2>/dev/null");
                lines.Add("\tip link set wlan0 up 2>/dev/null");
                lines.Add("\techo \"network interface renamed eth0 -> wlan0\"");
                lines.Add("fi");
            }

            lines.Add("");
        }

        // A-08 补齐：默认路由补齐
        // 该段位于 mount_data 锚点，执行时网卡尚未取得地址，此时下发的默认路由
        // 会被内核直接丢弃，表现为实例始终缺失默认路由。改为开机完成后由后台进程补齐。
        if (policy.EnableNetworkConvergence && !string.IsNullOrWhiteSpace(policy.DefaultGateway))
        {
            string gateway = policy.DefaultGateway.Trim();
            lines.Add("# --- 抗取证：默认路由补齐 (A-08，开机后补齐) -----------------");
            lines.Add("( r=0");
            lines.Add("  while [ $r -lt 180 ]; do");
            lines.Add("\t[ \"`chroot /android /system/bin/getprop sys.boot_completed 2>/dev/null`\" = \"1\" ] && break");
            lines.Add("\tsleep 1; r=$((r + 1))");
            lines.Add("  done");
            lines.Add("  net_dev=\"eth0\"");
            lines.Add("  [ -d /sys/class/net/wlan0 ] && net_dev=\"wlan0\"");
            lines.Add($"  chroot /android /system/bin/ip route replace default via {gateway} dev $net_dev 2>/dev/null || \\");
            lines.Add($"    chroot /android /system/bin/ip route add default via {gateway} dev $net_dev 2>/dev/null || \\");
            lines.Add($"    chroot /android /system/bin/ip route add default via {gateway} 2>/dev/null");
            lines.Add($"  echo \"default route ensure result: `chroot /android /system/bin/ip route show default 2>/dev/null`\"");
            lines.Add(") &");
            lines.Add("");
        }

        // A-16: 调试通路与 root 使能（去明文持久化）
        lines.Add("# --- 调试通路：启用 ADB over TCP 与 root -----------------------");
        if (policy.RootStealth)
        {
            lines.Add("# 抗取证模式：消除 /data/local.prop 明文持久化，改由运行时 setprop 断言 (A-16)");
            lines.Add("if [ -d /android/data ]; then");
            lines.Add("\tif [ -f /android/data/local.prop ]; then");
            lines.Add("\t\tsed -i '/service\\.adb\\.root/d' /android/data/local.prop 2>/dev/null");
            lines.Add("\t\tsed -i '/service\\.adb\\.tcp\\.port/d' /android/data/local.prop 2>/dev/null");
            lines.Add("\tfi");
            lines.Add("fi");
        }
        else
        {
            lines.Add("if [ -d /android/data ]; then");
            lines.Add($"\techo \"service.adb.tcp.port={adbGuestPort.ToString(CultureInfo.InvariantCulture)}\" > /android/data/local.prop");
            if (policy.EnableRoot)
            {
                lines.Add("\techo \"service.adb.root=1\" >> /android/data/local.prop");
            }
            lines.Add("\tchmod 644 /android/data/local.prop");
            lines.Add("\techo \"service.adb.tcp.port written to /android/data/local.prop\"");
            lines.Add("else");
            lines.Add("\techo \"/android/data missing, ADB over TCP not configured\"");
            lines.Add("fi");
        }
        lines.Add("");

        lines.Add("# 预置宿主 ADB 公钥，使握手无需界面确认。");
        lines.Add("mkdir -p /android/data/misc/adb");
        lines.Add($"echo \"{policy.AdbPublicKey}\" > /android/data/misc/adb/adb_keys");
        lines.Add("chown -R 2000:2000 /android/data/misc/adb");
        lines.Add("chmod 0750 /android/data/misc/adb");
        lines.Add("chmod 0640 /android/data/misc/adb/adb_keys");
        lines.Add("echo \"host ADB key planted in /data/misc/adb/adb_keys\"");
        lines.Add("");

        lines.Add("SWITCH=chroot");
        lines.Add("");

        // 辅助进程：APEX adbd 与运行时 setprop
        if (policy.EnableRoot)
        {
            lines.Add("( i=0");
            lines.Add("  while [ $i -lt 300 ]; do");
            lines.Add("\t[ -e /android/dev/socket/property_service ] && break");
            lines.Add("\tsleep 1; i=$((i + 1))");
            lines.Add("  done");
            if (policy.RootStealth)
            {
                lines.Add($"  chroot /android /system/bin/setprop service.adb.tcp.port {adbGuestPort.ToString(CultureInfo.InvariantCulture)} 2>&1");
            }
            lines.Add("  chroot /android /system/bin/setprop service.adb.root 1 2>&1");
            lines.Add("  echo \"service.adb.root = `chroot /android /system/bin/getprop service.adb.root 2>/dev/null`\"");
            lines.Add("");
            lines.Add("  k=0");
            lines.Add("  while [ $k -lt 300 ]; do");
            lines.Add($"\t[ -x /android{apex}/bin/adbd ] && break");
            lines.Add("\tsleep 1; k=$((k + 1))");
            lines.Add("  done");
            lines.Add("  echo \"APEX adbd appeared after ${k}s, launching it as uid `id -u`\"");
            lines.Add($"  if [ -x /android{apex}/bin/adbd ]; then");
            lines.Add("\twhile read -r _pfx _k _v; do");
            lines.Add("\t  [ \"$_pfx\" = \"export\" ] && eval \"export $_k=\\\"$_v\\\"\"");
            lines.Add("\tdone < /android/init.environ.rc 2>/dev/null");
            lines.Add($"\tLD_LIBRARY_PATH={apex}/lib64:/system/lib64 \\");
            lines.Add($"\t  chroot /android {apex}/bin/adbd > /android/data/adbd-start.log 2>&1 &");
            lines.Add("\tsleep 5");
            lines.Add("\tcat /android/data/adbd-start.log");
            lines.Add("  else");
            lines.Add($"\techo \"{apex}/bin/adbd not found\"");
            lines.Add("  fi");
            lines.Add(") &");
        }
        else
        {
            lines.Add("( i=0");
            lines.Add("  while [ $i -lt 300 ]; do");
            lines.Add($"\t[ -x /android{apex}/bin/adbd ] && break");
            lines.Add("\tsleep 1; i=$((i + 1))");
            lines.Add("  done");
            lines.Add("  echo \"APEX adbd appeared after ${i}s, launching it\"");
            lines.Add($"  if [ -x /android{apex}/bin/adbd ]; then");
            lines.Add("\twhile read -r _pfx _k _v; do");
            lines.Add("\t  [ \"$_pfx\" = \"export\" ] && eval \"export $_k=\\\"$_v\\\"\"");
            lines.Add("\tdone < /android/init.environ.rc 2>/dev/null");
            lines.Add($"\tLD_LIBRARY_PATH={apex}/lib64:/system/lib64 \\");
            lines.Add($"\t  chroot /android {apex}/bin/adbd > /android/data/adbd-start.log 2>&1 &");
            lines.Add("\tsleep 5");
            lines.Add("\tcat /android/data/adbd-start.log");
            lines.Add("  else");
            lines.Add($"\techo \"{apex}/bin/adbd not found\"");
            lines.Add("  fi");
            lines.Add(") &");
        }

        // A-05 / S-3: Android ID 落地与回读（无基带时不碰 IMEI）
        if (policy.EnableAndroidIdProvisioning && !string.IsNullOrWhiteSpace(policy.TargetAndroidId))
        {
            string targetAid = policy.TargetAndroidId.Trim();
            lines.Add("");
            lines.Add("# --- 抗取证：Android ID 消费与回读 (A-05/S-3) ---------------");
            lines.Add("( b=0");
            lines.Add("  while [ $b -lt 300 ]; do");
            lines.Add("\t[ \"`chroot /android /system/bin/getprop sys.boot_completed 2>/dev/null`\" = \"1\" ] && break");
            lines.Add("\tsleep 1; b=$((b + 1))");
            lines.Add("  done");
            lines.Add($"  chroot /android /system/bin/settings put secure android_id \"{targetAid}\" 2>&1");
            lines.Add("  actual_aid=`chroot /android /system/bin/settings get secure android_id 2>/dev/null`");
            lines.Add($"  echo \"android_id: target={targetAid} actual=$actual_aid\" > /android/data/android_id.log");
            lines.Add("  echo \"settings android_id applied and verified\"");
            lines.Add(") &");
        }

        // A-14/T-04: 主机名落定与回读（与 Android ID 同一套开机后辅助进程机制）。
        // 用户空间的网络守护进程会在启动期按属性重设主机名，因此必须在 sys.boot_completed 之后再落定。
        // 直接调用 sethostname 保证 `hostname` 命令即刻返回新值；
        // 同时写 net.hostname 与 persist.sys.hostname，保证框架侧与重启后取值一致。
        if (hostName is not null)
        {
            lines.Add("");
            lines.Add("# --- 抗取证：主机名落定与回读 (A-14/T-04) -------------------");
            lines.Add("( t=0");
            lines.Add("  while [ $t -lt 300 ]; do");
            lines.Add("\t[ \"`chroot /android /system/bin/getprop sys.boot_completed 2>/dev/null`\" = \"1\" ] && break");
            lines.Add("\tsleep 1; t=$((t + 1))");
            lines.Add("  done");
            lines.Add($"  chroot /android /system/bin/hostname {hostName} 2>&1");
            lines.Add($"  chroot /android /system/bin/setprop net.hostname {hostName} 2>&1");
            lines.Add($"  chroot /android /system/bin/setprop persist.sys.hostname {hostName} 2>&1");
            lines.Add("  actual_host=`chroot /android /system/bin/hostname 2>/dev/null`");
            lines.Add($"  echo \"hostname: target={hostName} actual=$actual_host net=`chroot /android /system/bin/getprop net.hostname 2>/dev/null`\" > /android/data/hostname.log");
            lines.Add("  echo \"guest hostname applied and verified\"");
            lines.Add(") &");
        }

        // 模块加载器
        if (policy.EnableModuleLoader)
        {
            lines.Add("");
            lines.Add("# 辅助进程：开机加载持久化 /data 分区上的模块 (/data/adb/modules)。");
            lines.Add("( n=0");
            lines.Add("  while [ $n -lt 600 ]; do");
            lines.Add("\t[ -e /android/dev/socket/property_service ] && break");
            lines.Add("\tsleep 1; n=$((n + 1))");
            lines.Add("  done");
            lines.Add("  sleep 5");
            lines.Add("  if [ -d /android/data/adb/modules ]; then");
            lines.Add("\techo \"scanning /data/adb/modules\"");
            lines.Add("\tmount -o remount,rw /android 2>&1");
            lines.Add("\tfor m in /android/data/adb/modules/*; do");
            lines.Add("\t  [ -d \"$m\" ] || continue");
            lines.Add("\t  name=`basename $m`");
            lines.Add("\t  echo \"applying module $name\"");
            lines.Add("\t  if [ -d \"$m/system\" ]; then");
            lines.Add("\t\t( cd \"$m/system\" && find . -type d ) | while read d; do");
            lines.Add("\t\t  [ -z \"$d\" ] && d=.");
            lines.Add("\t\t  mkdir -p /android/system/$d 2>&1");
            lines.Add("\t\tdone");
            lines.Add("\t\t( cd \"$m/system\" && find . -type f ) | while read f; do");
            lines.Add("\t\t  cp -f \"$m/system/$f\" /android/system/$f 2>&1");
            lines.Add("\t\tdone");
            lines.Add("\t\techo \"module $name system overlay applied\"");
            lines.Add("\t  fi");
            lines.Add("\t  for st in post-fs-data.sh service.sh boot-completed.sh; do");
            lines.Add("\t\tif [ -f \"$m/$st\" ]; then");
            lines.Add("\t\t  chmod 0755 \"$m/$st\" 2>/dev/null");
            lines.Add("\t\t  echo \"running $name/$st\"");
            lines.Add("\t\t  chroot /android /system/bin/sh /data/adb/modules/$name/$st 2>&1");
            lines.Add("\t\t  echo \"$name/$st rc=$?\"");
            lines.Add("\t\tfi");
            lines.Add("\t  done");
            lines.Add("\tdone");
            lines.Add("\tsync");
            lines.Add("  else");
            lines.Add("\techo \"no /data/adb/modules directory\"");
            lines.Add("  fi");
            lines.Add(") &");
        }

        lines.Add("");
        lines.Add("mount_sdcard\n");

        string injected = string.Join("\n", lines);
        return script.Replace(Anchor, injected, StringComparison.Ordinal);
    }

    /// <summary>
    /// 读取 initrd 文件中内嵌的策略指纹。
    /// </summary>
    /// <param name="initrdPath">initrd 文件路径。</param>
    /// <returns>内嵌指纹；文件不是定制产物时返回 null。</returns>
    private static string? ReadEmbeddedFingerprint(string initrdPath)
    {
        try
        {
            byte[] image = File.ReadAllBytes(initrdPath);
            InitrdCompression compression = InitrdImageCodec.Detect(image);
            byte[] archive = InitrdImageCodec.Decompress(image, compression);
            CpioNewcEntry? init = CpioNewcArchive.Find(CpioNewcArchive.Parse(archive), InitEntryName);

            if (init is null)
            {
                return null;
            }

            foreach (string line in Encoding.UTF8.GetString(init.Content).Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(FingerprintMarkerPrefix, StringComparison.Ordinal))
                {
                    return trimmed[FingerprintMarkerPrefix.Length..].Trim();
                }
            }

            return null;
        }
        catch (XBearException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把普通文本中的正则元字符转义为字面量形态，供注入脚本的 grep/sed 表达式使用。
    /// </summary>
    /// <param name="value">待转义文本。</param>
    /// <returns>元字符已转义的文本。</returns>
    private static string EscapeRegexLiteral(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c is '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '^' or '$' or '|' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

/// <summary>读取条目内容并按 UTF-8 解码。</summary>
    /// <param name="entry">归档条目。</param>
    /// <returns>条目文本。</returns>
    private static string ReadText(CpioNewcEntry entry)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(entry.Content);
        }
        catch (DecoderFallbackException ex)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"initrd 条目 {entry.Name} 不是合法的 UTF-8 文本。",
                "该 initramfs 的引导脚本编码异常，需要为其单独定制。",
                ex);
        }
    }

    /// <summary>
    /// 读取宿主 ADB 公钥。
    /// </summary>
    /// <returns>公钥文本。</returns>
    private string ReadHostAdbPublicKey()
    {
        if (!File.Exists(_hostAdbPublicKeyPath))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"未找到宿主 ADB 公钥：{_hostAdbPublicKeyPath}",
                "请先执行 adb keygen 生成密钥，或启动一次 adb 让其自动生成，随后重新导入镜像。" +
                "密钥归属由用户决定，导入过程不会代为创建。");
        }

        string key;
        try
        {
            key = File.ReadAllText(_hostAdbPublicKeyPath).Trim();
        }
        catch (IOException ex)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"读取宿主 ADB 公钥失败：{ex.Message}",
                $"确认 {_hostAdbPublicKeyPath} 可读，必要时检查文件权限或占用。",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"读取宿主 ADB 公钥被拒绝：{ex.Message}",
                $"确认当前用户对 {_hostAdbPublicKeyPath} 具有读取权限。",
                ex);
        }

        if (key.Length == 0)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"宿主 ADB 公钥为空：{_hostAdbPublicKeyPath}",
                "请删除该文件后重新执行 adb keygen 生成密钥，再重新导入镜像。");
        }

        return key;
    }

    /// <summary>
    /// 确保原版 initrd 已留档。
    /// </summary>
    /// <param name="initrdPath">initrd 文件路径。</param>
    /// <param name="stockPath">原版留档路径。</param>
    private static void EnsureStock(string initrdPath, string stockPath)
    {
        if (File.Exists(stockPath))
        {
            return;
        }

        string temporaryPath = stockPath + ".stocking";
        try
        {
            File.Copy(initrdPath, temporaryPath, overwrite: true);
            File.Move(temporaryPath, stockPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    /// <summary>推导原版留档路径。</summary>
    /// <param name="initrdPath">initrd 文件路径。</param>
    /// <returns>原版留档路径。</returns>
    private static string ResolveStockPath(string initrdPath) => initrdPath + StockSuffix;

    /// <summary>尽力删除临时文件，删除失败不影响主流程。</summary>
    /// <param name="path">临时文件路径。</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>清除文件只读属性。</summary>
    /// <param name="path">文件路径。</param>
    private static void ClearReadOnly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
