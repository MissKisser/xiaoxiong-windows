using System.Text;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Identity;

/// <summary>
/// 属性覆写生成器。按机型族画像与设备身份生成构建属性（build.prop）覆盖片段。
/// </summary>
/// <remarks>
/// ro.* 系统属性在 Android 属性服务中一经 property_load_boot_defaults 载入即不可二次写入。
/// 必须在 initrd 阶段以 bind-mount 方式在属性加载前生效。
/// 覆写属性由画像与身份统一派生，消解跨层与层内矛盾对。
/// </remarks>
public static class PropertyOverlayBuilder
{
    /// <summary>默认 Android 系统主版本号。</summary>
    public const string DefaultReleaseVersion = "11";

    /// <summary>默认构建号。</summary>
    public const string DefaultBuildId = "RSV1.210329.052";

    /// <summary>默认增量版本号。</summary>
    public const string DefaultIncremental = "20241012";

    /// <summary>默认构建类型。</summary>
    public const string DefaultBuildType = "userdebug";

    /// <summary>默认构建标签。</summary>
    public const string DefaultBuildTags = "release-keys";

    /// <summary>默认启动链加锁状态（方向 A 弱校验自洽画像）。</summary>
    public const string DefaultFlashLocked = "0";

    /// <summary>默认 vbmeta 设备状态（方向 A 弱校验自洽画像）。</summary>
    public const string DefaultVbmetaDeviceState = "orange";

    /// <summary>默认已校验启动状态（方向 A 弱校验自洽画像）。</summary>
    public const string DefaultVerifiedBootState = "orange";

    /// <summary>默认 dm-verity 模式（方向 A 弱校验自洽画像）。</summary>
    public const string DefaultVerityMode = "disabled";

    /// <summary>默认 bootloader 标识。</summary>
    public const string DefaultBootloader = "unknown";

    /// <summary>默认 bootmode 标识。</summary>
    public const string DefaultBootmode = "unknown";

    /// <summary>SwiftShader 软件渲染器的 EGL 硬件属性名。</summary>
    public const string SwiftShaderEglHardware = "swiftshader";

    /// <summary>Pastel 软件 Vulkan 驱动的属性名。</summary>
    public const string PastelVulkanHardware = "pastel";

    private static readonly string[] Partitions = { "system", "vendor", "product", "odm" };

    /// <summary>
    /// 从完整身份契约对象生成属性字典。
    /// </summary>
    /// <param name="identitySpec">设备身份契约规格。</param>
    /// <param name="enableRenderSpoofing">是否启用渲染伪装，默认 false（保持与运行库一致，避免制造新矛盾）。</param>
    /// <returns>有序属性键值对字典。</returns>
    /// <exception cref="XBearException">属性值含换行符或非法字符时抛出。</exception>
    public static IReadOnlyDictionary<string, string> BuildProperties(
        IdentitySpec identitySpec,
        bool enableRenderSpoofing = false)
    {
        ArgumentNullException.ThrowIfNull(identitySpec);
        ArgumentNullException.ThrowIfNull(identitySpec.DeviceProfile);

        var profile = DeviceProfile.Create(
            identitySpec.DeviceProfile.ProfileId,
            identitySpec.DeviceProfile.Brand,
            identitySpec.DeviceProfile.Manufacturer,
            identitySpec.DeviceProfile.ProductName,
            identitySpec.DeviceProfile.ProductVersion,
            identitySpec.DeviceProfile.ProductFamily,
            identitySpec.DeviceProfile.ProductSku,
            identitySpec.DeviceProfile.Device ?? identitySpec.DeviceProfile.ProductName.ToLowerInvariant(),
            identitySpec.DeviceProfile.CpuManufacturer ?? "Intel",
            identitySpec.DeviceProfile.CpuVersion ?? "Intel(R) Core(TM)",
            identitySpec.DeviceProfile.CpuArchitecture,
            identitySpec.DeviceProfile.BoardManufacturer ?? identitySpec.DeviceProfile.Manufacturer,
            identitySpec.DeviceProfile.BoardProduct ?? identitySpec.DeviceProfile.ProductName,
            identitySpec.DeviceProfile.ChassisManufacturer ?? identitySpec.DeviceProfile.Manufacturer,
            identitySpec.DeviceProfile.ChassisAsset ?? "0",
            string.IsNullOrWhiteSpace(identitySpec.Mac) ? "52:54:00" : identitySpec.Mac[..8],
            0);

        return BuildProperties(
            profile,
            identitySpec.SerialNo,
            identitySpec.Fingerprint,
            identitySpec.BuildIdentity,
            identitySpec.BootIdentity,
            enableRenderSpoofing);
    }

    /// <summary>
    /// 按机型族画像与身份详细参数生成属性字典。
    /// </summary>
    /// <param name="profile">机型族画像。</param>
    /// <param name="serialNo">设备序列号，可为空。</param>
    /// <param name="fingerprint">构建指纹，为空时按画像与构建身份合成。</param>
    /// <param name="buildIdentity">构建身份规格，可为空。</param>
    /// <param name="bootIdentity">启动链规格，可为空。</param>
    /// <param name="enableRenderSpoofing">是否启用渲染伪装，默认 false（Q-06 默认：保持与运行库一致）。</param>
    /// <returns>有序属性键值对字典。</returns>
    /// <exception cref="XBearException">属性值含换行符或非法字符时抛出。</exception>
    public static IReadOnlyDictionary<string, string> BuildProperties(
        DeviceProfile profile,
        string? serialNo = null,
        string? fingerprint = null,
        BuildIdentitySpec? buildIdentity = null,
        BootIdentitySpec? bootIdentity = null,
        bool enableRenderSpoofing = false)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var props = new Dictionary<string, string>(StringComparer.Ordinal);

        // 1. ro.product.* 全族（消解 F-03、M-15、X-C2、X-C6）
        props["ro.product.brand"] = profile.Brand;
        props["ro.product.manufacturer"] = profile.Manufacturer;
        props["ro.product.model"] = profile.ProductName;
        props["ro.product.name"] = profile.ProductName;
        props["ro.product.device"] = profile.Device;

        foreach (string partition in Partitions)
        {
            props[$"ro.product.{partition}.brand"] = profile.Brand;
            props[$"ro.product.{partition}.manufacturer"] = profile.Manufacturer;
            props[$"ro.product.{partition}.model"] = profile.ProductName;
            props[$"ro.product.{partition}.name"] = profile.ProductName;
            props[$"ro.product.{partition}.device"] = profile.Device;
        }

        // 2. ro.build.* 指纹与构建自洽（消解 M-02、M-02b、X-C4）
        string type = buildIdentity?.Type ?? DefaultBuildType;
        string tags = buildIdentity?.Tags ?? DefaultBuildTags;
        string flavor = buildIdentity?.Flavor ?? $"{profile.Device}-{type}";
        string buildId = buildIdentity?.BuildId ?? DefaultBuildId;
        string incremental = buildIdentity?.Incremental ?? DefaultIncremental;

        string resolvedFingerprint = !string.IsNullOrWhiteSpace(fingerprint)
            ? fingerprint.Trim()
            : $"{profile.Brand}/{profile.ProductName}/{profile.Device}:{DefaultReleaseVersion}/{buildId}/{incremental}:{type}/{tags}";

        props["ro.build.fingerprint"] = resolvedFingerprint;
        foreach (string partition in Partitions)
        {
            props[$"ro.{partition}.build.fingerprint"] = resolvedFingerprint;
        }

        props["ro.build.type"] = type;
        props["ro.build.tags"] = tags;
        props["ro.build.flavor"] = flavor;
        props["ro.build.id"] = buildId;
        props["ro.build.version.incremental"] = incremental;

        // 3. 启动链画像（A-22 方向 A：自洽弱画像）
        props["ro.boot.flash.locked"] = bootIdentity?.FlashLocked ?? DefaultFlashLocked;
        props["ro.boot.vbmeta.device_state"] = bootIdentity?.VbmetaDeviceState ?? DefaultVbmetaDeviceState;
        props["ro.boot.verifiedbootstate"] = bootIdentity?.VerifiedBootState ?? DefaultVerifiedBootState;
        props["ro.boot.veritymode"] = bootIdentity?.VerityMode ?? DefaultVerityMode;
        props["ro.bootloader"] = bootIdentity?.Bootloader ?? DefaultBootloader;
        props["ro.bootmode"] = bootIdentity?.Bootmode ?? DefaultBootmode;

        // 无基带时不碰 IMEI 与 baseband 属性，避免制造矛盾
        if (!string.IsNullOrWhiteSpace(bootIdentity?.Baseband))
        {
            props["ro.baseband"] = bootIdentity.Baseband.Trim();
        }

        // 4. 序列号
        if (!string.IsNullOrWhiteSpace(serialNo))
        {
            string s = serialNo.Trim();
            props["ro.serialno"] = s;
            props["ro.boot.serialno"] = s;
        }

        // 5. 平台与主板自洽（消解 L-05）
        props["ro.board.platform"] = profile.Device;
        props["ro.product.board"] = profile.Device;

        // 6. 渲染栈属性串（A-15 按 Q-06 默认处理）
        if (!enableRenderSpoofing)
        {
            // 默认与底层 SwiftShader 运行时库一致，不制造虚假属性与运行时库冲突
            props["ro.hardware.egl"] = SwiftShaderEglHardware;
            props["ro.hardware.vulkan"] = PastelVulkanHardware;
        }

        // 属性值白名单校验（R-07）
        foreach (var (k, v) in props)
        {
            ValidatePropertyKeyValue(k, v);
        }

        return props;
    }

    /// <summary>
    /// 生成符合 Android build.prop 格式的完整文本内容。
    /// </summary>
    /// <param name="profile">机型族画像。</param>
    /// <param name="serialNo">设备序列号，可为空。</param>
    /// <param name="fingerprint">构建指纹，可为空。</param>
    /// <param name="buildIdentity">构建身份规格，可为空。</param>
    /// <param name="bootIdentity">启动链规格，可为空。</param>
    /// <param name="enableRenderSpoofing">是否启用渲染伪装，默认 false。</param>
    /// <returns>格式化后的属性文本。</returns>
    public static string BuildBuildPropContent(
        DeviceProfile profile,
        string? serialNo = null,
        string? fingerprint = null,
        BuildIdentitySpec? buildIdentity = null,
        BootIdentitySpec? bootIdentity = null,
        bool enableRenderSpoofing = false)
    {
        var props = BuildProperties(
            profile,
            serialNo,
            fingerprint,
            buildIdentity,
            bootIdentity,
            enableRenderSpoofing);

        var builder = new StringBuilder();
        builder.AppendLine("# --- Auto-generated build.prop overlay by XBear ---");
        foreach (var (key, value) in props)
        {
            builder.Append(key).Append('=').AppendLine(value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 从完整身份契约对象生成符合 Android build.prop 格式的完整文本内容。
    /// </summary>
    /// <param name="identitySpec">设备身份契约规格。</param>
    /// <param name="enableRenderSpoofing">是否启用渲染伪装，默认 false。</param>
    /// <returns>格式化后的属性文本。</returns>
    public static string BuildBuildPropContent(
        IdentitySpec identitySpec,
        bool enableRenderSpoofing = false)
    {
        var props = BuildProperties(identitySpec, enableRenderSpoofing);

        var builder = new StringBuilder();
        builder.AppendLine("# --- Auto-generated build.prop overlay by XBear ---");
        foreach (var (key, value) in props)
        {
            builder.Append(key).Append('=').AppendLine(value);
        }

        return builder.ToString();
    }

    private static void ValidatePropertyKeyValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new XBearException(ErrorCategory.Identity, "属性名不得为空。");
        }

        if (key.Contains('=') || key.Contains('\r') || key.Contains('\n'))
        {
            throw new XBearException(ErrorCategory.Identity, $"属性名 {key} 包含非法字符。");
        }

        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new XBearException(ErrorCategory.Identity, $"属性 {key} 的取值包含换行符。");
        }
    }
}
