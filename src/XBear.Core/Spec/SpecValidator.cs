using XBear.Core.Diagnostics;

namespace XBear.Core.Spec;

/// <summary>单条校验错误，携带实例路径与失败原因。</summary>
public sealed class ValidationError
{
    /// <summary>出错位置在实例 JSON 中的路径，根节点为空串。</summary>
    public string Path { get; }

    /// <summary>失败原因描述。</summary>
    public string Message { get; }

    /// <summary>触发的校验关键字位置，便于定位规则来源。</summary>
    public string KeywordLocation { get; }

    /// <summary>
    /// 创建一条校验错误。
    /// </summary>
    /// <param name="path">实例 JSON 中的出错路径。</param>
    /// <param name="message">失败原因描述。</param>
    /// <param name="keywordLocation">触发的校验关键字位置。</param>
    public ValidationError(string path, string message, string keywordLocation = "")
    {
        Path = path;
        Message = message;
        KeywordLocation = keywordLocation;
    }

    /// <summary>返回可读的路径与原因组合。</summary>
    /// <returns>形如 <c>/network/exposure: 取值不在枚举内</c> 的文本。</returns>
    public override string ToString() =>
        string.IsNullOrEmpty(Path) ? Message : $"{Path}: {Message}";
}

/// <summary>结构化校验结果，收集全部错误而非只抛一个异常。</summary>
public sealed class ValidationResult
{
    /// <summary>校验所依据的 schema 文件名。</summary>
    public string SchemaFileName { get; }

    /// <summary>全部校验错误，无错误时为空集合。</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>是否通过校验。</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// 创建校验结果。
    /// </summary>
    /// <param name="schemaFileName">校验所依据的 schema 文件名。</param>
    /// <param name="errors">校验错误集合。</param>
    public ValidationResult(string schemaFileName, IReadOnlyList<ValidationError> errors)
    {
        SchemaFileName = schemaFileName;
        Errors = errors ?? Array.Empty<ValidationError>();
    }

    /// <summary>
    /// 校验通过时返回校验通过的实例，否则抛出规格类异常。
    /// </summary>
    /// <returns>同一个校验结果。</returns>
    /// <exception cref="XBearException">校验未通过时抛出。</exception>
    public ValidationResult EnsureValid()
    {
        if (IsValid)
        {
            return this;
        }

        throw ToException();
    }

    /// <summary>
    /// 转换为规格类异常，错误分类为 <see cref="ErrorCategory.Spec"/>。
    /// </summary>
    /// <returns>携带全部错误明细的异常。</returns>
    public XBearException ToException() =>
        new(
            ErrorCategory.Spec,
            $"不符合 {SchemaFileName} 契约，共 {Errors.Count} 处问题：{DescribeErrors()}",
            "跨端契约由共享规格定义，单端不得自行放宽 Schema 或改写样例来迁就。");

    /// <summary>
    /// 返回全部错误的可读摘要。
    /// </summary>
    /// <returns>以分号连接的错误文本。</returns>
    public string DescribeErrors() =>
        string.Join("；", Errors.Select(e => e.ToString()));
}

/// <summary>
/// JSON Schema 求值器抽象。Core 工程不引用任何 Schema 库，
/// 具体求值实现由引用了 Schema 库的装配注入。
/// </summary>
public interface ISchemaEvaluator
{
    /// <summary>
    /// 对照给定 Schema 求值一份 JSON 文本。
    /// </summary>
    /// <param name="schemaJson">Schema 文件文本。</param>
    /// <param name="instanceJson">待校验的 JSON 文本。</param>
    /// <param name="schemaFileName">Schema 文件名，用于错误定位。</param>
    /// <returns>校验错误集合，通过校验时为空集合。</returns>
    IReadOnlyList<ValidationError> Evaluate(
        string schemaJson,
        string instanceJson,
        string schemaFileName);
}

/// <summary>
/// 实例配置、镜像清单、快照元数据、投屏会话、传输任务、模块记录与应用记录的 Schema 校验入口，
/// 并对没有配套 Schema 文件的版本契约与性能基线做结构校验。
/// 校验结果为结构化对象，便于测试与上层逐条展示。
/// </summary>
public sealed class SpecValidator
{
    private readonly ISchemaEvaluator _evaluator;
    private readonly SpecLoader _loader;

    /// <summary>
    /// 创建校验器。
    /// </summary>
    /// <param name="evaluator">Schema 求值器，不可为空。</param>
    public SpecValidator(ISchemaEvaluator evaluator)
        : this(evaluator, SpecLoader.Default)
    {
    }

    /// <summary>
    /// 创建校验器。
    /// </summary>
    /// <param name="evaluator">Schema 求值器，不可为空。</param>
    /// <param name="loader">规格读取器。</param>
    public SpecValidator(ISchemaEvaluator evaluator, SpecLoader loader)
    {
        _evaluator = evaluator ?? throw new XBearException(
            ErrorCategory.Spec,
            "Schema 求值器不能为空。");
        _loader = loader ?? throw new XBearException(
            ErrorCategory.Spec,
            "规格读取器不能为空。");
    }

    /// <summary>
    /// 校验实例配置 JSON。
    /// </summary>
    /// <param name="json">实例配置 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateInstance(string json) =>
        ValidateAgainst(SpecLoader.InstanceSchemaFileName, json);

    /// <summary>
    /// 校验镜像清单 JSON。
    /// </summary>
    /// <param name="json">镜像清单 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateImage(string json) =>
        ValidateAgainst(SpecLoader.ImageSchemaFileName, json);

    /// <summary>
    /// 校验术语表 JSON。
    /// </summary>
    /// <param name="json">术语表 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateTerminology(string json) =>
        ValidateAgainst(SpecLoader.TerminologySchemaFileName, json);

    /// <summary>
    /// 校验快照元数据 JSON。
    /// </summary>
    /// <param name="json">快照元数据 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateSnapshot(string json) =>
        ValidateAgainst(SpecLoader.SnapshotSchemaFileName, json);

    /// <summary>
    /// 校验投屏会话 JSON。
    /// </summary>
    /// <param name="json">投屏会话 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateProjection(string json) =>
        ValidateAgainst(SpecLoader.ProjectionSchemaFileName, json);

    /// <summary>
    /// 校验传输任务 JSON。
    /// </summary>
    /// <param name="json">传输任务 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateFileTransfer(string json) =>
        ValidateAgainst(SpecLoader.FileTransferSchemaFileName, json);

    /// <summary>
    /// 校验模块清单与安装状态 JSON。
    /// </summary>
    /// <param name="json">模块记录 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateModule(string json) =>
        ValidateAgainst(SpecLoader.ModuleSchemaFileName, json);

    /// <summary>
    /// 校验应用记录 JSON。
    /// </summary>
    /// <param name="json">应用记录 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateApplication(string json) =>
        ValidateAgainst(SpecLoader.ApplicationSchemaFileName, json);

    /// <summary>
    /// 校验版本契约 JSON。版本契约未配套 Schema 文件，
    /// 此处按强类型模型核对必填段落与版本号形态。
    /// </summary>
    /// <param name="json">版本契约 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateVersion(string json)
    {
        var errors = new List<ValidationError>();
        var document = _loader.ParseVersion(json);

        RequireText(errors, "/product/id", document.Product.Id, "产品标识");
        RequireText(errors, "/product/nameZh", document.Product.NameZh, "产品中文名");
        RequireText(errors, "/product/nameEn", document.Product.NameEn, "产品英文名");
        RequireSemanticVersion(errors, "/product/version", document.Product.Version);

        RequireSemanticVersion(errors, "/spec/version", document.Spec.Version);
        RequireText(errors, "/spec/description", document.Spec.Description, "规格层版本说明");

        RequireText(errors, "/terminology/versionField", document.Terminology.VersionField, "术语表版本字段位置");
        RequireText(errors, "/terminology/rule", document.Terminology.Rule, "术语表递增规则");

        RequireText(errors, "/build/idFormat", document.Build.IdFormat, "构建标识模板");
        if (document.Build.Fields.Count == 0)
        {
            errors.Add(new ValidationError("/build/fields", "构建标识未声明任何字段来源", "/build/fields"));
        }

        if (document.Rules.Count == 0)
        {
            errors.Add(new ValidationError("/rules", "版本契约未声明任何递增规则", "/rules"));
        }

        for (var i = 0; i < document.Rules.Count; i++)
        {
            var path = $"/rules/{i}";
            var rule = document.Rules[i];

            if (!VersionBumpRules.IsKnownLevel(rule.Level))
            {
                errors.Add(new ValidationError(
                    $"{path}/level",
                    $"递增级别 {rule.Level} 不属于已定义级别",
                    $"{path}/level"));
            }

            RequireText(errors, $"{path}/when", rule.When, "递增规则触发条件");

            if (rule.Effects.Count == 0)
            {
                errors.Add(new ValidationError($"{path}/effects", "递增规则未声明两端动作", $"{path}/effects"));
            }
        }

        RequireText(errors, "/consistency/note", document.Consistency.Note, "版本序列独立性说明");
        RequireText(errors, "/consistency/driftPolicy", document.Consistency.DriftPolicy, "版本防漂移策略");
        RequireText(
            errors,
            "/consistency/schemaCompatibility",
            document.Consistency.SchemaCompatibility,
            "规格层版本兼容性说明");

        return new ValidationResult(SpecLoader.VersionFileName, errors);
    }

    /// <summary>
    /// 校验性能基线 JSON。性能基线未配套 Schema 文件，
    /// 此处按强类型模型核对指标定义、门限运算符与单位声明的一致性。
    /// </summary>
    /// <param name="json">性能基线 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateBaseline(string json)
    {
        var errors = new List<ValidationError>();
        var baseline = _loader.ParseBaseline(json);
        var density = baseline.Density;

        RequireSemanticVersion(errors, "/version", baseline.Version);
        RequireText(errors, "/status", baseline.Status, "基线状态");
        RequireText(errors, "/statusNote", baseline.StatusNote, "基线状态说明");

        foreach (var kind in baseline.Units.Kinds())
        {
            RequireText(errors, $"/units/{kind.Name}", kind.Symbol, "单位符号");
        }

        if (baseline.Metrics.Count == 0)
        {
            errors.Add(new ValidationError("/metrics", "性能基线未声明任何指标", "/metrics"));
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < baseline.Metrics.Count; i++)
        {
            var metric = baseline.Metrics[i];
            var path = $"/metrics/{i}";

            if (string.IsNullOrWhiteSpace(metric.Id))
            {
                errors.Add(new ValidationError($"{path}/id", "指标标识不能为空", $"{path}/id"));
            }
            else if (!seenIds.Add(metric.Id))
            {
                errors.Add(new ValidationError(
                    $"{path}/id",
                    $"指标标识 {metric.Id} 重复",
                    $"{path}/id"));
            }

            RequireText(errors, $"{path}/name", metric.Name, "指标名称");
            RequireText(errors, $"{path}/measure", metric.Measure, "指标测量口径");
            RequireText(errors, $"{path}/owner", metric.Owner, "指标负责端");
            RequireThreshold(errors, $"{path}/target", metric.Target, baseline.Units);
        }

        if (density.Profiles.Count == 0)
        {
            errors.Add(new ValidationError("/density/profiles", "性能基线未声明任何密度档位", "/density/profiles"));
        }

        for (var i = 0; i < density.Profiles.Count; i++)
        {
            var profile = density.Profiles[i];
            var path = $"/density/profiles/{i}";

            RequireText(errors, $"{path}/tier", profile.Tier, "密度档位名");

            if (profile.HostMemoryGB <= 0)
            {
                errors.Add(new ValidationError(
                    $"{path}/hostMemoryGB",
                    "密度档位必须绑定正的宿主内存容量",
                    $"{path}/hostMemoryGB"));
            }

            RequireThreshold(errors, $"{path}/targetInstances", profile.TargetInstances, baseline.Units);
        }

        if (density.Regression.MaxDegradationPercent <= 0)
        {
            errors.Add(new ValidationError(
                "/density/regression/maxDegradationPercent",
                "并行数达标的附加条件必须给出正的劣化上限",
                "/density/regression/maxDegradationPercent"));
        }

        if (baseline.FillPolicy.Count == 0)
        {
            errors.Add(new ValidationError("/fillPolicy", "性能基线未声明实测值回填规则", "/fillPolicy"));
        }

        return new ValidationResult(SpecLoader.BaselineFileName, errors);
    }

    /// <summary>
    /// 校验样例文件，先做 Schema 校验再解析为强类型模型。
    /// </summary>
    /// <param name="fixtureFileName">样例文件名。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateFixture(string fixtureFileName)
    {
        var json = _loader.ReadFixtureText(fixtureFileName);
        var schemaFileName = FixtureSchemaFileName(fixtureFileName);

        var result = ValidateAgainst(schemaFileName, json);
        if (result.IsValid)
        {
            // 解析一次以确认样例可被强类型模型接受，解析失败会抛出规格类异常。
            ParseFixture(schemaFileName, json);
        }

        return result;
    }

    /// <summary>
    /// 按样例所属契约把文本解析为对应的强类型模型。
    /// </summary>
    /// <param name="schemaFileName">该样例对应的 Schema 文件名。</param>
    /// <param name="json">样例 JSON 文本。</param>
    /// <exception cref="XBearException">样例无法被强类型模型接受时抛出。</exception>
    private void ParseFixture(string schemaFileName, string json)
    {
        switch (schemaFileName)
        {
            case SpecLoader.ImageSchemaFileName:
                _loader.ParseImage(json);
                break;

            case SpecLoader.SnapshotSchemaFileName:
                _loader.ParseSnapshot(json);
                break;

            case SpecLoader.ProjectionSchemaFileName:
                _loader.ParseProjection(json);
                break;

            case SpecLoader.FileTransferSchemaFileName:
                _loader.ParseFileTransfer(json);
                break;

            case SpecLoader.ModuleSchemaFileName:
                _loader.ParseModule(json);
                break;

            case SpecLoader.ApplicationSchemaFileName:
                _loader.ParseApplication(json);
                break;

            default:
                _loader.ParseInstance(json);
                break;
        }
    }

    /// <summary>
    /// 对照指定 Schema 文件校验任意 JSON 文本。
    /// </summary>
    /// <param name="schemaFileName">schema 文件名。</param>
    /// <param name="json">待校验的 JSON 文本。</param>
    /// <returns>结构化校验结果。</returns>
    public ValidationResult ValidateAgainst(string schemaFileName, string json)
    {
        var schemaJson = _loader.ReadSchemaText(schemaFileName);
        var errors = _evaluator.Evaluate(schemaJson, json, schemaFileName);
        return new ValidationResult(schemaFileName, errors);
    }

    /// <summary>
    /// 按样例文件名判定其所属的契约 Schema。
    /// </summary>
    /// <param name="fixtureFileName">样例文件名。</param>
    /// <returns>该样例对应的 Schema 文件名。</returns>
    private static string FixtureSchemaFileName(string fixtureFileName)
    {
        if (fixtureFileName.StartsWith("image", StringComparison.Ordinal))
        {
            return SpecLoader.ImageSchemaFileName;
        }

        foreach (var (prefix, schemaFileName) in FixturePrefixSchemaFiles)
        {
            if (fixtureFileName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return schemaFileName;
            }
        }

        return SpecLoader.InstanceSchemaFileName;
    }

    /// <summary>样例文件名前缀与其所属契约 Schema 的对应关系。</summary>
    private static readonly (string Prefix, string SchemaFileName)[] FixturePrefixSchemaFiles =
    {
        ("snapshot", SpecLoader.SnapshotSchemaFileName),
        ("projection", SpecLoader.ProjectionSchemaFileName),
        ("filetransfer", SpecLoader.FileTransferSchemaFileName),
        ("module", SpecLoader.ModuleSchemaFileName),
        ("application", SpecLoader.ApplicationSchemaFileName),
    };

    private static void RequireText(
        List<ValidationError> errors,
        string path,
        string? value,
        string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new ValidationError(path, $"{what}不能为空", path));
        }
    }

    private static void RequireSemanticVersion(
        List<ValidationError> errors,
        string path,
        string? value)
    {
        if (!SemanticVersion.TryParse(value, out _))
        {
            errors.Add(new ValidationError(
                path,
                $"版本号 {value ?? "（空）"} 不是三段式语义版本",
                path));
        }
    }

    private static void RequireThreshold(
        List<ValidationError> errors,
        string path,
        BaselineThreshold threshold,
        BaselineUnits units)
    {
        if (!threshold.HasKnownOperator())
        {
            errors.Add(new ValidationError(
                $"{path}/op",
                $"比较运算符 {threshold.Op} 不属于已定义运算符",
                $"{path}/op"));
        }

        if (threshold.Unit is { } unit && !units.ContainsSymbol(unit))
        {
            errors.Add(new ValidationError(
                $"{path}/unit",
                $"单位符号 {unit} 未在单位表中声明",
                $"{path}/unit"));
        }
    }
}

/// <summary>术语违规项，记录命中的禁用近义词。</summary>
public sealed class TerminologyViolation
{
    /// <summary>所属术语标识。</summary>
    public string TermId { get; }

    /// <summary>命中的禁用近义词。</summary>
    public string ForbiddenWord { get; }

    /// <summary>命中位置在文本中的起始下标。</summary>
    public int Index { get; }

    /// <summary>命中位置前后的上下文片段。</summary>
    public string Context { get; }

    /// <summary>
    /// 创建一条术语违规项。
    /// </summary>
    /// <param name="termId">所属术语标识。</param>
    /// <param name="forbiddenWord">命中的禁用近义词。</param>
    /// <param name="index">命中位置在文本中的起始下标。</param>
    /// <param name="context">命中位置前后的上下文片段。</param>
    public TerminologyViolation(string termId, string forbiddenWord, int index, string context)
    {
        TermId = termId;
        ForbiddenWord = forbiddenWord;
        Index = index;
        Context = context;
    }

    /// <summary>返回可读的违规描述。</summary>
    /// <returns>形如 <c>术语 instance 禁用近义词「分身」，命中于「...」</c> 的文本。</returns>
    public override string ToString() =>
        $"术语 {TermId} 禁用近义词「{ForbiddenWord}」，命中于「{Context}」";
}

/// <summary>术语校验结果，收集全部禁用词命中。</summary>
public sealed class TerminologyCheckResult
{
    /// <summary>全部违规项，无违规时为空集合。</summary>
    public IReadOnlyList<TerminologyViolation> Violations { get; }

    /// <summary>是否没有命中任何禁用近义词。</summary>
    public bool IsValid => Violations.Count == 0;

    /// <summary>
    /// 创建术语校验结果。
    /// </summary>
    /// <param name="violations">违规项集合。</param>
    public TerminologyCheckResult(IReadOnlyList<TerminologyViolation> violations)
    {
        Violations = violations ?? Array.Empty<TerminologyViolation>();
    }

    /// <summary>
    /// 返回全部违规项的可读摘要。
    /// </summary>
    /// <returns>以分号连接的违规文本。</returns>
    public string DescribeViolations() =>
        string.Join("；", Violations.Select(v => v.ToString()));

    /// <summary>
    /// 无违规时返回校验通过的实例，否则抛出规格类异常。
    /// </summary>
    /// <returns>同一个校验结果。</returns>
    /// <exception cref="XBearException">存在违规项时抛出。</exception>
    public TerminologyCheckResult EnsureValid()
    {
        if (IsValid)
        {
            return this;
        }

        throw new XBearException(
            ErrorCategory.Spec,
            $"文本命中 {Violations.Count} 处禁用近义词：{DescribeViolations()}",
            "按术语表统一措辞后重试；禁用词由双端术语表集中定义。");
    }
}

/// <summary>
/// 术语表校验器，禁止使用各术语的禁用近义词。
/// 中文近义词按原文匹配，英文近义词忽略大小写匹配。
/// </summary>
public static class TerminologyValidator
{
    private const int ContextRadius = 12;

    /// <summary>
    /// 检查一段文本是否命中术语表中的禁用近义词。
    /// </summary>
    /// <param name="text">待检查文本。</param>
    /// <param name="terminology">术语表文档。</param>
    /// <returns>术语校验结果。</returns>
    public static TerminologyCheckResult CheckText(string text, TerminologyDocument terminology)
    {
        if (terminology is null)
        {
            throw new XBearException(
                ErrorCategory.Spec,
                "术语表不能为空。");
        }

        if (string.IsNullOrEmpty(text))
        {
            return new TerminologyCheckResult(Array.Empty<TerminologyViolation>());
        }

        var violations = new List<TerminologyViolation>();

        foreach (var term in terminology.Terms)
        {
            foreach (var forbidden in term.Forbidden)
            {
                if (string.IsNullOrWhiteSpace(forbidden))
                {
                    continue;
                }

                var comparison = IsAsciiWord(forbidden)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                var index = text.IndexOf(forbidden, comparison);
                while (index >= 0)
                {
                    violations.Add(new TerminologyViolation(
                        term.Id,
                        forbidden,
                        index,
                        Slice(text, index, forbidden.Length)));
                    index = text.IndexOf(forbidden, index + forbidden.Length, comparison);
                }
            }
        }

        return new TerminologyCheckResult(violations);
    }

    private static bool IsAsciiWord(string value)
    {
        foreach (var c in value)
        {
            if (c > (char)127)
            {
                return false;
            }
        }

        return true;
    }

    private static string Slice(string text, int index, int length)
    {
        var start = Math.Max(0, index - ContextRadius);
        var end = Math.Min(text.Length, index + length + ContextRadius);
        return text[start..end];
    }
}