using System.Text.RegularExpressions;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Identity;

/// <summary>
/// 机型族画像。DMI/SMBIOS 字符串、系统属性与构建指纹的唯一来源，三者不得各写一份。
/// </summary>
/// <remarks>
/// 画像在设备的全部可读通道上必须呈现同一个故事：DMI 的机型串与
/// <c>ro.product.*</c> 的品牌厂商、Build 指纹的品牌段若互相矛盾，
/// 会被检测方逐层交叉校验直接命中。因此本对象是「一次派生、多处消费」的派生根，
/// 各消费方只读不改，不得在下游就地拼装字段。
/// </remarks>
public sealed partial class DeviceProfile
{
    /// <summary>机型族标识字符集，契约限定为小写字母、数字与连字符。</summary>
    private const string ProfileIdPattern = "^[a-z0-9][a-z0-9-]{1,62}$";

    /// <summary>品牌名允许的字符集，契约与真实品牌串形态一致。</summary>
    private const string BrandPattern = "^[A-Za-z][A-Za-z0-9 ._-]{1,31}$";

    /// <summary>厂商全称允许的字符集，厂商全称内可含逗号。</summary>
    private const string ManufacturerPattern = "^[A-Za-z][A-Za-z0-9 ._,-]{1,63}$";

    /// <summary>机型销售名允许的字符集。</summary>
    private const string ProductNamePattern = "^[A-Za-z0-9][A-Za-z0-9 ._+-]{1,63}$";

    /// <summary>产品代号允许的字符集，无空白。</summary>
    private const string DevicePattern = "^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$";

    /// <summary>处理器版本串中禁止出现的 ARM 系列词，大小写两写逐字符列出。</summary>
    private const string ArmCpuSeriesPattern =
        "[Aa][Rr][Mm]|[Aa][Aa][Rr][Cc][Hh]64|[Cc][Oo][Rr][Tt][Ee][Xx]|[Ee][Xx][Yy][Nn][Oo][Ss]"
        + "|[Ss][Nn][Aa][Pp][Dd][Rr][Aa][Gg][Oo][Nn]|[Dd][Ii][Mm][Ee][Nn][Ss][Ii][Tt][Yy]"
        + "|[Kk][Ii][Rr][Ii][Nn]|[Tt][Ee][Gg][Rr][Aa]|[Qq][Uu][Aa][Ll][Cc][Oo][Mm][Mm]";

    /// <summary>网卡 OUI 段字符集，六字节小写十六冒号分隔。</summary>
    private const string OuiPattern = "^([0-9a-f]{2}:){2}[0-9a-f]{2}$";

    [GeneratedRegex(ProfileIdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdRegex();

    [GeneratedRegex(BrandPattern, RegexOptions.CultureInvariant)]
    private static partial Regex BrandRegex();

    [GeneratedRegex(ManufacturerPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ManufacturerRegex();

    [GeneratedRegex(ProductNamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex ProductNameRegex();

    [GeneratedRegex(DevicePattern, RegexOptions.CultureInvariant)]
    private static partial Regex DeviceRegex();

    [GeneratedRegex(ArmCpuSeriesPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ArmCpuSeriesRegex();

    [GeneratedRegex(OuiPattern, RegexOptions.CultureInvariant)]
    private static partial Regex OuiRegex();

    private DeviceProfile(
        string profileId,
        string brand,
        string manufacturer,
        string productName,
        string productVersion,
        string productFamily,
        string productSku,
        string device,
        string cpuManufacturer,
        string cpuVersion,
        string cpuArchitecture,
        string boardManufacturer,
        string boardProduct,
        string chassisManufacturer,
        string chassisAsset,
        string macOui,
        int profileSeed)
    {
        ProfileId = profileId;
        Brand = brand;
        Manufacturer = manufacturer;
        ProductName = productName;
        ProductVersion = productVersion;
        ProductFamily = productFamily;
        ProductSku = productSku;
        Device = device;
        CpuManufacturer = cpuManufacturer;
        CpuVersion = cpuVersion;
        CpuArchitecture = cpuArchitecture;
        BoardManufacturer = boardManufacturer;
        BoardProduct = boardProduct;
        ChassisManufacturer = chassisManufacturer;
        ChassisAsset = chassisAsset;
        MacOui = macOui;
        ProfileSeed = profileSeed;
    }

    /// <summary>机型族标识，取自内置机型族表。</summary>
    public string ProfileId { get; }

    /// <summary>品牌名，派生各分区的 brand 字段。</summary>
    public string Brand { get; }

    /// <summary>厂商全称，派生 ro.product.manufacturer 与 SMBIOS type=1 的 manufacturer。</summary>
    public string Manufacturer { get; }

    /// <summary>机型销售名，派生 SMBIOS type=1 的 product 与 ro.product.model。</summary>
    public string ProductName { get; }

    /// <summary>机型版本串，派生 SMBIOS type=1 的 version。</summary>
    public string ProductVersion { get; }

    /// <summary>机型族名，派生 SMBIOS type=1 的 family，必须是 <see cref="ProductName"/> 的前缀。</summary>
    public string ProductFamily { get; }

    /// <summary>机型 SKU，派生 SMBIOS type=1 的 sku。</summary>
    public string ProductSku { get; }

    /// <summary>产品代号，派生 ro.product.device、指纹串的产品段与实例内主机名。</summary>
    public string Device { get; }

    /// <summary>处理器厂商，派生 SMBIOS type=4 的 manufacturer。</summary>
    public string CpuManufacturer { get; }

    /// <summary>处理器版本串，派生 SMBIOS type=4 的 version。</summary>
    public string CpuVersion { get; }

    /// <summary>处理器架构，本产品唯一支持 x86_64。</summary>
    public string CpuArchitecture { get; }

    /// <summary>主板厂商，派生 SMBIOS type=2 的 manufacturer。</summary>
    public string BoardManufacturer { get; }

    /// <summary>主板型号，派生 SMBIOS type=2 的 product。</summary>
    public string BoardProduct { get; }

    /// <summary>机箱厂商，派生 SMBIOS type=3 的 manufacturer。</summary>
    public string ChassisManufacturer { get; }

    /// <summary>机箱资产号，派生 SMBIOS type=3 的 asset。</summary>
    public string ChassisAsset { get; }

    /// <summary>
    /// 网卡 OUI 段，取自本产品自有且不落入宿主仿真器默认段的实验段。
    /// 末三字节由序列号确定性派生，不使用独立随机源。
    /// </summary>
    public string MacOui { get; }

    /// <summary>机型族选型的确定性盐，用于末三字节派生时区分不同机型族。</summary>
    public int ProfileSeed { get; }

    /// <summary>
    /// 按内置机型族表的条目构造画像。构造即完成三条跨字段一致性校验，
    /// 任一条不成立即拒绝构造：不合规的画像一旦进入下发链路，
    /// 各通道会各自带出一半矛盾信息，比不伪装更容易被交叉校验命中。
    /// </summary>
    /// <param name="profileId">机型族标识。</param>
    /// <param name="brand">品牌名。</param>
    /// <param name="manufacturer">厂商全称。</param>
    /// <param name="productName">机型销售名。</param>
    /// <param name="productVersion">机型版本串。</param>
    /// <param name="productFamily">机型族名，必须是销售名的前缀。</param>
    /// <param name="productSku">机型 SKU。</param>
    /// <param name="device">产品代号，无空白。</param>
    /// <param name="cpuManufacturer">处理器厂商。</param>
    /// <param name="cpuVersion">处理器版本串，不得带 ARM 系列词。</param>
    /// <param name="cpuArchitecture">处理器架构。</param>
    /// <param name="boardManufacturer">主板厂商。</param>
    /// <param name="boardProduct">主板型号。</param>
    /// <param name="chassisManufacturer">机箱厂商。</param>
    /// <param name="chassisAsset">机箱资产号。</param>
    /// <param name="macOui">网卡 OUI 段，六字节小写十六冒号分隔。</param>
    /// <param name="profileSeed">机型族选型的确定性盐。</param>
    /// <returns>通过全部一致性校验的机型族画像。</returns>
    /// <exception cref="XBearException">任一字段格式或跨字段一致性不成立时抛出 <see cref="ErrorCategory.Identity"/>。</exception>
    public static DeviceProfile Create(
        string profileId,
        string brand,
        string manufacturer,
        string productName,
        string productVersion,
        string productFamily,
        string productSku,
        string device,
        string cpuManufacturer,
        string cpuVersion,
        string cpuArchitecture,
        string boardManufacturer,
        string boardProduct,
        string chassisManufacturer,
        string chassisAsset,
        string macOui,
        int profileSeed)
    {
        var errors = new List<string>();

        if (!ProfileIdRegex().IsMatch(profileId))
        {
            errors.Add($"机型族标识 {profileId} 不满足契约字符集");
        }

        if (!BrandRegex().IsMatch(brand))
        {
            errors.Add($"品牌名 {brand} 不满足契约字符集");
        }

        if (!ManufacturerRegex().IsMatch(manufacturer))
        {
            errors.Add($"厂商全称 {manufacturer} 不满足契约字符集");
        }

        if (!ProductNameRegex().IsMatch(productName))
        {
            errors.Add($"机型销售名 {productName} 不满足契约字符集");
        }

        if (!DeviceRegex().IsMatch(device))
        {
            errors.Add($"产品代号 {device} 不满足契约字符集");
        }

        // 机型族自洽第一条：品牌与厂商必须同源。
        // 厂商全称以品牌名开头是最常见的真实形态，二者不同源会被属性一致性校验直接命中。
        if (!manufacturer.StartsWith(brand, StringComparison.Ordinal))
        {
            errors.Add($"厂商全称 {manufacturer} 与品牌 {brand} 不同源");
        }

        // 机型族自洽第二条：机型族名必须是销售名的前缀。
        if (!productName.StartsWith(productFamily, StringComparison.Ordinal))
        {
            errors.Add($"机型族名 {productFamily} 不是机型销售名 {productName} 的前缀");
        }

        // 机型族自洽第三条：架构与处理器版本串不得互相矛盾。
        // x86_64 机型配 ARM 处理器条目会与实例内处理器信息直接矛盾。
        if (ArmCpuSeriesRegex().IsMatch(cpuVersion))
        {
            errors.Add($"处理器版本串 {cpuVersion} 与架构 {cpuArchitecture} 矛盾");
        }

        if (!OuiRegex().IsMatch(macOui))
        {
            errors.Add($"网卡 OUI 段 {macOui} 不是六字节小写十六冒号分隔形式");
        }

        RequireText(productVersion, "机型版本串", errors);
        RequireText(productSku, "机型 SKU", errors);
        RequireText(cpuManufacturer, "处理器厂商", errors);
        RequireText(boardManufacturer, "主板厂商", errors);
        RequireText(boardProduct, "主板型号", errors);
        RequireText(chassisManufacturer, "机箱厂商", errors);
        RequireText(chassisAsset, "机箱资产号", errors);

        if (errors.Count > 0)
        {
            throw new XBearException(
                ErrorCategory.Identity,
                $"机型族画像 {profileId} 未通过一致性校验：{string.Join("；", errors)}。",
                "机型族表由产品维护，修复需让机型族、销售名、厂商与处理器信息回到同一虚构厂商族下。");
        }

        return new DeviceProfile(
            profileId,
            brand,
            manufacturer,
            productName,
            productVersion,
            productFamily,
            productSku,
            device,
            cpuManufacturer,
            cpuVersion,
            cpuArchitecture,
            boardManufacturer,
            boardProduct,
            chassisManufacturer,
            chassisAsset,
            macOui,
            profileSeed);
    }

    /// <summary>
    /// 把机型族画像投影为契约形态的 <see cref="DeviceProfileSpec"/>，
    /// 供实例配置的 deviceProfileId 反查后完整还原。
    /// </summary>
    /// <returns>与契约逐字段对应的机型族画像。</returns>
    public DeviceProfileSpec ToSpec() => new()
    {
        ProfileId = ProfileId,
        Brand = Brand,
        Manufacturer = Manufacturer,
        ProductName = ProductName,
        ProductVersion = ProductVersion,
        ProductFamily = ProductFamily,
        ProductSku = ProductSku,
        Device = Device,
        CpuManufacturer = CpuManufacturer,
        CpuVersion = CpuVersion,
        CpuArchitecture = CpuArchitecture,
        BoardManufacturer = BoardManufacturer,
        BoardProduct = BoardProduct,
        ChassisManufacturer = ChassisManufacturer,
        ChassisAsset = ChassisAsset,
    };

    private static void RequireText(string value, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{fieldName}不能为空");
        }
    }
}