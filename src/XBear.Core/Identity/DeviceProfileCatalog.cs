using System.Globalization;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Identity;

/// <summary>
/// 内置机型族表。表中的每一项即一份机型族画像，是 DMI、系统属性与构建指纹的唯一来源。
/// </summary>
/// <remarks>
/// 机型族取自虚构厂商族，不使用真实在售机型的完整画像：
/// 照搬真机画像会与设备数据库撞库，反而成为一条确定的暴露。
/// 网卡 OUI 段取自本产品自有的实验段，既不落入宿主仿真器的默认段，
/// 也不落入真实厂商段——离线无法权威判定某段是否已分配，
/// 声称「取真实厂商段」只会把不确定性伪装成保真度。
/// 表的扩展只改本文件，不需要改任何消费方代码。
/// </remarks>
public static class DeviceProfileCatalog
{
    /// <summary>机型族标识 <c>xb-vibe12-11</c>。</summary>
    public const string Vibe12ProfileId = "xb-vibe12-11";

    /// <summary>机型族标识 <c>xb-nova8-11</c>。</summary>
    public const string Nova8ProfileId = "xb-nova8-11";

    /// <summary>机型族标识 <c>xb-edge7-11</c>。</summary>
    public const string Edge7ProfileId = "xb-edge7-11";

    /// <summary>实例未声明机型族时按实例序号确定性选取的默认机型族标识。</summary>
    public const string DefaultProfileId = Vibe12ProfileId;

    private static readonly DeviceProfile Vibe12 = DeviceProfile.Create(
        profileId: Vibe12ProfileId,
        brand: "Xion",
        manufacturer: "Xion Devices",
        productName: "Xion Vibe 12 Pro 5G",
        productVersion: "12.1",
        productFamily: "Xion Vibe",
        productSku: "XV12P-512-12",
        device: "vibe12pro",
        cpuManufacturer: "Xion",
        cpuVersion: "Xion XQ-7210",
        cpuArchitecture: CpuArchitectures.X86_64,
        boardManufacturer: "Xion Devices",
        boardProduct: "XV12-MB-A3",
        chassisManufacturer: "Xion Devices",
        chassisAsset: "ASSET-000184",
        macOui: "3a:1f:0c",
        profileSeed: 0x11);

    private static readonly DeviceProfile Nova8 = DeviceProfile.Create(
        profileId: Nova8ProfileId,
        brand: "Noru",
        manufacturer: "Noru Systems",
        productName: "Noru Nova 8",
        productVersion: "8.0",
        productFamily: "Noru Nova",
        productSku: "NV8-256-8",
        device: "nova8",
        cpuManufacturer: "Noru",
        cpuVersion: "Noru NX-820",
        cpuArchitecture: CpuArchitectures.X86_64,
        boardManufacturer: "Noru Systems",
        boardProduct: "NV8-MB-C1",
        chassisManufacturer: "Noru Systems",
        chassisAsset: "ASSET-000512",
        macOui: "3a:1f:0d",
        profileSeed: 0x23);

    private static readonly DeviceProfile Edge7 = DeviceProfile.Create(
        profileId: Edge7ProfileId,
        brand: "Lumo",
        manufacturer: "Lumo Mobile",
        productName: "Lumo Edge 7",
        productVersion: "7.4",
        productFamily: "Lumo Edge",
        productSku: "LE7-128-6",
        device: "edge7",
        cpuManufacturer: "Lumo",
        cpuVersion: "Lumo LE-U540",
        cpuArchitecture: CpuArchitectures.X86_64,
        boardManufacturer: "Lumo Mobile",
        boardProduct: "LE7-MB-B2",
        chassisManufacturer: "Lumo Mobile",
        chassisAsset: "ASSET-000731",
        macOui: "3a:1f:0e",
        profileSeed: 0x37);

    /// <summary>内置机型族表，按稳定顺序枚举。</summary>
    public static IReadOnlyList<DeviceProfile> Profiles { get; } =
        new[] { Vibe12, Nova8, Edge7 };

    private static readonly IReadOnlyDictionary<string, DeviceProfile> ById =
        Profiles.ToDictionary(profile => profile.ProfileId, StringComparer.Ordinal);

    /// <summary>
    /// 按标识查机型族画像。
    /// </summary>
    /// <param name="profileId">机型族标识。</param>
    /// <returns>匹配的机型族画像，未找到时返回 null。</returns>
    public static DeviceProfile? Find(string? profileId) =>
        profileId is not null && ById.TryGetValue(profileId, out var profile) ? profile : null;

    /// <summary>
    /// 按标识查机型族画像，查不到时抛出异常。
    /// </summary>
    /// <param name="profileId">机型族标识。</param>
    /// <returns>匹配的机型族画像。</returns>
    /// <exception cref="XBearException">标识不在内置机型族表内时抛出 <see cref="ErrorCategory.Identity"/>。</exception>
    public static DeviceProfile Require(string profileId) =>
        Find(profileId) ?? throw new XBearException(
            ErrorCategory.Identity,
            $"机型族标识 {profileId} 不在内置机型族表内。",
            "机型族表由产品内置，实例配置的 deviceProfileId 只能取表内取值；请修正后重试。");

    /// <summary>
    /// 按实例序号确定性选取机型族画像。实例未声明机型族时走此路径：
    /// 取值只与实例序号有关，因此同一实例跨重启稳定，多实例之间天然分散。
    /// </summary>
    /// <param name="instanceOrdinal">实例在在册列表中的序号。</param>
    /// <returns>按序号确定性选出的机型族画像。</returns>
    public static DeviceProfile Select(int instanceOrdinal)
    {
        int count = Profiles.Count;
        int index = ((instanceOrdinal % count) + count) % count;
        return Profiles[index];
    }

    /// <summary>
    /// 解析实例应绑定的机型族画像：已声明时按标识查表，未声明时按序号确定性选取。
    /// </summary>
    /// <param name="declaredProfileId">实例声明的机型族标识，可为空。</param>
    /// <param name="instanceOrdinal">实例在在册列表中的序号。</param>
    /// <returns>应绑定的机型族画像。</returns>
    /// <exception cref="XBearException">声明的机型族标识不在表内时抛出 <see cref="ErrorCategory.Identity"/>。</exception>
    public static DeviceProfile Resolve(string? declaredProfileId, int instanceOrdinal)
        => string.IsNullOrWhiteSpace(declaredProfileId)
            ? Select(instanceOrdinal)
            : Require(declaredProfileId.Trim());

    /// <summary>
    /// 取机型族画像在表中的序号，用于把画像与实例序号互相换算。
    /// </summary>
    /// <param name="profile">机型族画像。</param>
    /// <returns>表内序号，不在表内时返回 -1。</returns>
    public static int IndexOf(DeviceProfile profile) =>
        Profiles.ToList().FindIndex(candidate => candidate.ProfileId == profile.ProfileId);

    /// <summary>
    /// 由机型族画像与实例序号派生该实例绑定的机型族标识。
    /// </summary>
    /// <param name="instanceOrdinal">实例在在册列表中的序号。</param>
    /// <returns>机型族标识。</returns>
    public static string ProfileIdForOrdinal(int instanceOrdinal) =>
        Select(instanceOrdinal).ProfileId;

    /// <summary>
    /// 由实例标识派生稳定的机型族选型序号，使机型族绑定不随实例创建顺序漂移。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <returns>机型族表内序号。</returns>
    public static int OrdinalForInstanceId(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        // FNV-1a：跨进程与跨运行稳定的确定性散列，避免机型族绑定随加载顺序变化。
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;

        uint hash = OffsetBasis;
        foreach (var character in instanceId)
        {
            hash ^= character;
            hash *= Prime;
        }

        int count = Profiles.Count;
        return (int)(hash % (uint)count);
    }

    /// <summary>
    /// 取机型族画像对应的 Android 版本号，参与构建指纹的机型段派生。
    /// </summary>
    /// <param name="profile">机型族画像。</param>
    /// <returns>Android 版本号。</returns>
    /// <exception cref="XBearException">该机型族未登记 Android 版本号时抛出。</exception>
    public static string AndroidVersionFor(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.ProfileId switch
        {
            Vibe12ProfileId => "11",
            Nova8ProfileId => "11",
            Edge7ProfileId => "11",
            _ => throw new XBearException(
                ErrorCategory.Identity,
                $"机型族 {profile.ProfileId} 未登记 Android 版本号。",
                "Android 版本号参与构建指纹派生，必须与机型族画像同源登记。"),
        };
    }

    /// <summary>
    /// 取机型族画像对应的构建类型。保留 Root 权限与可写系统时只能取 userdebug，
    /// 取 user 会与实际能力矛盾。
    /// </summary>
    /// <param name="profile">机型族画像。</param>
    /// <returns>构建类型标识。</returns>
    /// <exception cref="XBearException">该机型族未登记构建类型时抛出。</exception>
    public static string BuildTypeFor(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.ProfileId switch
        {
            Vibe12ProfileId => "userdebug",
            Nova8ProfileId => "userdebug",
            Edge7ProfileId => "userdebug",
            _ => throw new XBearException(
                ErrorCategory.Identity,
                $"机型族 {profile.ProfileId} 未登记构建类型。",
                "构建类型必须与机型族画像同源登记，否则构建指纹与 ro.build.type 会互相矛盾。"),
        };
    }

    /// <summary>
    /// 取机型族画像对应的构建标签。
    /// </summary>
    /// <param name="profile">机型族画像。</param>
    /// <returns>构建标签。</returns>
    /// <exception cref="XBearException">该机型族未登记构建标签时抛出。</exception>
    public static string BuildTagsFor(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.ProfileId switch
        {
            Vibe12ProfileId => "release-keys",
            Nova8ProfileId => "release-keys",
            Edge7ProfileId => "dev-keys",
            _ => throw new XBearException(
                ErrorCategory.Identity,
                $"机型族 {profile.ProfileId} 未登记构建标签。",
                "构建标签必须与机型族画像同源登记，否则会与构建指纹的标签段矛盾。"),
        };
    }

    /// <summary>
    /// 取机型族画像对应的机型代号的小写形态，用于主机名与产品代号。
    /// </summary>
    /// <param name="profile">机型族画像。</param>
    /// <returns>产品代号。</returns>
    public static string DeviceCodenameFor(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Device.ToLowerInvariant();
    }

    /// <summary>
    /// 把机型族标识与实例序号格式化为稳定的字符串，供持久化与回读比对使用。
    /// </summary>
    /// <param name="profile">机型族画像。</param>
    /// <param name="instanceOrdinal">实例序号。</param>
    /// <returns>形如 画像标识@序号 的稳定字符串。</returns>
    public static string FormatBinding(DeviceProfile profile, int instanceOrdinal) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{profile.ProfileId}@{instanceOrdinal}");
}