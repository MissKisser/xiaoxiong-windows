using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using XBear.Core.Diagnostics;

namespace XBear.App.Services;

/// <summary>
/// initrd 定制策略。
///
/// 策略内容会参与产物指纹的计算：任一字段变化都会让指纹变化，从而触发重新定制。
/// </summary>
/// <param name="AdbGuestPort">guest 内 adbd 的监听端口。</param>
/// <param name="AdbPublicKey">写入 guest 授权列表的宿主 ADB 公钥。</param>
/// <param name="AdbApexMountPath">adbd 二进制所在的 APEX 挂载点。</param>
/// <param name="EnableRoot">是否使能 root 权限（local.prop 与 setprop 断言）。</param>
/// <param name="EnableModuleLoader">是否启用开机模块加载器（扫描 /data/adb/modules 并叠加）。</param>
public sealed record InitrdCustomizationPolicy(
    int AdbGuestPort,
    string AdbPublicKey,
    string AdbApexMountPath,
    bool EnableRoot = true,
    bool EnableModuleLoader = true);

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
/// 把从镜像中提取的原版 initrd 加工为带 ADB 调试通路的定制 initrd。
///
/// 定制内容与取舍：
/// <list type="number">
/// <item>scripts/0-auto-detect 的 auto_detect 与 auto_detect_alpine 打桩为空实现。
/// 上游实现按设备 modalias 逐个同步 modprobe，在硬件虚拟化下会卡死引导流程；
/// 模块加载改由 Android 自身的 init 完成。</item>
/// <item>挂载数据分区后写入宿主 ADB 公钥。镜像的 ro.adb.secure 为 1，
/// 未预置授权时每次连接都要在 guest 界面点确认，宿主无法完成握手。</item>
/// <item>APEX 就绪后自动拉起 adbd。该镜像不在 /system/bin 提供 adbd，
/// 也没有对应的 init.rc 服务，调试通路必须由 initrd 显式拉起。</item>
/// <item>root 使能：向 guest 的 /data/local.prop 追加 service.adb.root=1，
/// 并在拉起 adbd 之前再经 setprop 断言一次。adbd 只在自身启动的那一次读该属性，
/// 仅靠持久化文件不足以保证生效，两处缺一不可。</item>
/// <item>模块加载器：开机扫描持久化 /data 上的模块目录，把模块的 system/ 子树
/// 叠加进系统并执行其阶段脚本。该内核的 KernelSU 是残缺实现（对用户态上报 UAPI=0），
/// Magisk 依赖同一内核桩，因此模块机制由 initrd 自身提供。</item>
/// </list>
///
/// 产物文件名与原版保持一致：boot 推荐引用的就是同一个文件名，
/// 只有内容被替换，因此实例侧的配置无需任何改动。
/// 原版另存为 .stock 留档，定制失败或策略变更时可据此重生成。
///
/// 幂等判定内建在产物自身：注入块带有策略指纹，重复运行时先读出指纹再决定是否重做，
/// 不依赖任何旁路文件，因此产物被单独拷贝出去也不会被二次注入。
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
    }

    /// <summary>
    /// 定制指定的 initrd：读取原版留档或就地留存原版，套用定制后写回同一路径。
    ///
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

        return Task.FromResult(Customize(initrdPath, cancellationToken));
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

        string material = string.Join(
            "\n",
            policy.AdbGuestPort.ToString(CultureInfo.InvariantCulture),
            policy.AdbPublicKey,
            policy.AdbApexMountPath,
            policy.EnableRoot ? "root:1" : "root:0",
            policy.EnableModuleLoader ? "modules:1" : "modules:0",
            AutoDetectStubMarker);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    /// <summary>
    /// 执行定制流程。
    ///
    /// 幂等判定顺序：先看目标产物内嵌的指纹是否等于当前策略指纹，
    /// 相等即原样返回。判定完全基于产物内容，不依赖任何旁路文件，
    /// 因此产物被单独拷贝到别处也不会被二次注入。
    /// </summary>
    /// <param name="initrdPath">initrd 文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>定制结果。</returns>
    private InitrdCustomizationResult Customize(string initrdPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(initrdPath))
        {
            throw new XBearException(
                ErrorCategory.Spec,
                $"initrd 文件不存在：{initrdPath}",
                "确认镜像导入已完成引导资产提取，且 initrd 文件名与 boot 推荐一致。");
        }

        string publicKey = ReadHostAdbPublicKey();
        var policy = new InitrdCustomizationPolicy(
            DefaultAdbGuestPort,
            publicKey,
            DefaultAdbApexMountPath,
            _enableRoot,
            _enableModuleLoader);
        string fingerprint = ComputeFingerprint(policy);

        string stockPath = ResolveStockPath(initrdPath);
        bool hasStock = File.Exists(stockPath);

        // 指纹随产物一同写入，判定幂等只需读目标本身，不必依赖任何旁路文件。
        // 代价是一次解压解包，换来的是产物被单独拷走也不会被二次注入。
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

        // 目标已带定制痕迹但留档缺失时，不得拿它冒充原版重新叠加：
        // 那样会在注入块上再叠一层，引导脚本随即无法解析。
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
    /// 对归档套用三处定制，返回被改写的条目名。
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
        var customized = new List<string>(2);

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
    ///
    /// 采用文本级替换而非整文件覆写：上游脚本的其余部分（load_modules 等）
    /// 仍需按原样工作，覆写会在上游小版本变动时静默丢失这些逻辑。
    /// 任一函数找不到即报错，不允许产出打了折扣的产物。
    /// </summary>
    /// <param name="script">脚本原文。</param>
    /// <returns>打桩后的脚本。</returns>
    private static string StubAutoDetect(string script)
    {
        string result = ReplaceShellFunction(script, "auto_detect");
        result = ReplaceShellFunction(result, "auto_detect_alpine");

        // 打桩的产出是引导脚本，少一个左花括号这类错误会让整个 initramfs 起不来，
        // 而它又发生在纯文本处理里、无法在编译期暴露，因此在出口处显式校验。
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
    /// 函数边界以独立的左花括号行与右花括号行界定，与目标脚本的书写风格一致。
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

        // 函数头与左花括号之间的部分原样保留，只有函数体被替换：
// 右花括号落在 closeIndex 上，其后所有行同样原样保留。
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
    /// 找到 shell 函数的头部行，形如 <c>name()</c>。
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
    /// 在根脚本的数据分区挂载之后注入调试通路：预置授权公钥、保持数据挂载点可见、
    /// 在属性服务与 APEX 就绪后断言 root 并拉起 adbd，并在开机时扫描与挂载模块。
    ///
    /// 关机时的页缓存同步自愈（sync）：
    /// 模块加载器在每次应用模块后均显式调用 sync 刷盘；
    /// 而实例运行期间动态写入模块或文件后的关机前 sync，依职责划分归属宿主侧的实例停止流程
    /// （宿主在停止实例前通过调试通道或 ADB 执行 sync），不在此处注入常驻的后台同步循环。
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
            "# --- 调试通路：启用 ADB over TCP 与 root -----------------------",
            "# adbd 在启动时读取 service.adb.tcp.port 与 service.adb.root，",
            "# 该值由 property_load_boot_defaults() 从 /data/local.prop 载入，",
            "# 因此设置可跨重启保留且无需改动 /system。",
            "if [ -d /android/data ]; then",
            $"\techo \"service.adb.tcp.port={adbGuestPort.ToString(CultureInfo.InvariantCulture)}\" > /android/data/local.prop",
        };

        if (policy.EnableRoot)
        {
            lines.Add("\techo \"service.adb.root=1\" >> /android/data/local.prop");
        }

        lines.Add("\tchmod 644 /android/data/local.prop");
        lines.Add("\techo \"service.adb.tcp.port written to /android/data/local.prop\"");
        lines.Add("else");
        lines.Add("\techo \"/android/data missing, ADB over TCP not configured\"");
        lines.Add("fi");
        lines.Add("");
        lines.Add("# 预置宿主 ADB 公钥，使握手无需界面确认。");
        lines.Add("# adbd 以 uid 2000（shell）或 uid 0（root）运行，属主与权限必须与之匹配。");
        lines.Add("mkdir -p /android/data/misc/adb");
        lines.Add($"echo \"{policy.AdbPublicKey}\" > /android/data/misc/adb/adb_keys");
        lines.Add("chown -R 2000:2000 /android/data/misc/adb");
        lines.Add("chmod 0750 /android/data/misc/adb");
        lines.Add("chmod 0640 /android/data/misc/adb/adb_keys");
        lines.Add("echo \"host ADB key planted in /data/misc/adb/adb_keys\"");
        lines.Add("");
        lines.Add("# 以 chroot 而非 switch_root 交接，/android 挂载点继续可见，");
        lines.Add("# 下面的辅助进程才能访问 guest 的 /data。");
        lines.Add("SWITCH=chroot");
        lines.Add("");
        lines.Add("# 辅助进程：等待 APEX 挂载出现后从其中拉起 adbd。");
        lines.Add("# 该镜像不在 /system/bin 提供 adbd，也没有对应的 init.rc 服务；");
        lines.Add("# 二进制只存在于 adbd APEX 挂载点，由 apexd 在属性服务发布之后才拉起。");
        lines.Add("# 辅助进程运行在 initramfs 的命名空间内，因此 guest 路径一律经由 /android 访问，");
        lines.Add("# 二进制也通过 chroot 启动。");

        if (policy.EnableRoot)
        {
            lines.Add("( i=0");
            lines.Add("  while [ $i -lt 300 ]; do");
            lines.Add("\t[ -e /android/dev/socket/property_service ] && break");
            lines.Add("\tsleep 1; i=$((i + 1))");
            lines.Add("  done");
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
            lines.Add($"\tLD_LIBRARY_PATH={apex}/lib64:/system/lib64 \\");
            lines.Add($"\t  chroot /android {apex}/bin/adbd > /android/data/adbd-start.log 2>&1 &");
            lines.Add("\tsleep 5");
            lines.Add("\tcat /android/data/adbd-start.log");
            lines.Add("  else");
            lines.Add($"\techo \"{apex}/bin/adbd not found\"");
            lines.Add("  fi");
            lines.Add(") &");
        }

        if (policy.EnableModuleLoader)
        {
            lines.Add("");
            lines.Add("# 辅助进程：开机加载持久化 /data 分区上的模块 (/data/adb/modules)。");
            lines.Add("# initramfs 以 uid 0 运行，/android 是后续挂载到 / 的 loop mount，");
            lines.Add("# 将模块的 system/ 子树 overlay 进去，并执行各阶段脚本。");
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
    /// 指纹标记位于注入块内，只看文件头无法判定，必须解压解包到根脚本。
    /// 无法解析时返回 null，表示该文件不是定制产物，由调用方按原版处理。
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
            // 结构异常时无法判定是否已定制，交由后续定制流程报出真实原因。
            return null;
        }
        catch (IOException)
        {
            // 同上，读取失败不应在此处转换成另一个语义的错误。
            return null;
        }
    }

    /// <summary>
    /// 读取条目内容并按 UTF-8 解码。
    /// </summary>
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
    /// 读取宿主 ADB 公钥。文件缺失时不代为生成：密钥归属是用户决策，
    /// 产品不得在用户不知情的情况下创建可用身份凭证。
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
    /// 确保原版 initrd 已留档。留档存在即视为可信来源，不重复覆盖：
    /// 覆盖会把定制产物误当作原版，导致后续定制在已定制的产物上二次施加。
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
            // 临时文件残留不影响定制结果，留待系统清理。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }

    /// <summary>
    /// 清除目标文件的只读属性。
    /// 引导资产可能来自带只读位的发行介质，覆盖前必须先解除，否则替换会以权限错误失败。
    /// </summary>
    /// <param name="path">目标文件路径。</param>
    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}