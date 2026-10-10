using System.Text.Json;
using System.Text.Json.Nodes;
using XBear.Core.Spec;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.Compat;

/// <summary>
/// 投屏会话的往返测试：两端产出的投屏会话样例都必须能被本端强类型模型完整解析，
/// 且解析后重新序列化再解析，字段集合与语义均不丢失。
/// </summary>
public class ProjectionModelRoundTripTests
{
    /// <summary>两份投屏会话样例文件名。</summary>
    public static TheoryData<string> ProjectionFixtureFiles =>
        new()
        {
            { SpecLoader.ProjectionMinimalFixtureName },
            { SpecLoader.ProjectionFullFixtureName }
        };

    [Theory]
    [MemberData(nameof(ProjectionFixtureFiles))]
    public void ProjectionSampleParsesIntoModel(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.LoadProjectionFixture(fixtureFileName);

        Assert.Matches(@"^\d+\.\d+\.\d+$", spec.SchemaVersion);
        Assert.Matches("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", spec.Id);
        Assert.Matches("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", spec.InstanceRef);
        Assert.True(spec.Video.Width > 0);
        Assert.True(spec.Video.Height > 0);
    }

    [Theory]
    [MemberData(nameof(ProjectionFixtureFiles))]
    public void ProjectionRoundTripKeepsEveryDeclaredField(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var first = SpecTestHost.Loader.LoadProjectionFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);

        Assert.Equal(
            JsonLeafPaths.NonNullLeaves(JsonNode.Parse(json)),
            JsonLeafPaths.NonNullLeaves(JsonNode.Parse(reserialized)));
    }

    [Theory]
    [MemberData(nameof(ProjectionFixtureFiles))]
    public void ProjectionRoundTripIsSemanticallyEquivalent(string fixtureFileName)
    {
        var first = SpecTestHost.Loader.LoadProjectionFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);
        var second = SpecTestHost.Loader.ParseProjection(reserialized);

        var secondPass = JsonSerializer.Serialize(second, SpecLoader.SerializerOptions);

        Assert.Equal(reserialized, secondPass);
        Assert.Equal(first.SchemaVersion, second.SchemaVersion);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.InstanceRef, second.InstanceRef);
        Assert.Equal(first.Video.Width, second.Video.Width);
        Assert.Equal(first.Video.Height, second.Video.Height);
        Assert.Equal(first.State, second.State);
        Assert.Equal(first.StartedAt, second.StartedAt);
        Assert.Equal(first.EndedAt, second.EndedAt);
    }

    [Theory]
    [MemberData(nameof(ProjectionFixtureFiles))]
    public void RoundTrippedProjectionStillPassesSchema(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.LoadProjectionFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        var result = SpecTestHost.Validator.ValidateProjection(reserialized);

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void MinimalProjectionDoesNotGainOptionalFields()
    {
        var spec = SpecTestHost.Loader.LoadProjectionFixture(SpecLoader.ProjectionMinimalFixtureName);

        Assert.Null(spec.Input);
        Assert.Null(spec.Video.Fps);
        Assert.Null(spec.StartedAt);
        Assert.Null(spec.EndedAt);
        Assert.Null(spec.PlatformConfig);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.DoesNotContain("\"input\"", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"fps\"", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("startedAt", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("endedAt", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void FullProjectionKeepsInputSemantics()
    {
        var spec = SpecTestHost.Loader.LoadProjectionFixture(SpecLoader.ProjectionFullFixtureName);

        Assert.NotNull(spec.Input);
        Assert.Equal(ProjectionInputChannel.Native, spec.Input!.Channel);
        Assert.Equal(
            new[] { ProjectionInputDeviceKind.Pointer, ProjectionInputDeviceKind.Keyboard },
            spec.Input.Devices!);
        Assert.NotNull(spec.Input.Pointer);
        Assert.True(spec.Input.Pointer!.IsAbsoluteDomain());
        Assert.Equal(32767, spec.Input.Pointer.Max);
        Assert.Equal("linux-evdev", spec.Input.Keyboard!.Layout);
    }

    /// <summary>
    /// 目标帧率只声明意图，实测帧率只登记真实观测。
    /// 未测量时必须写成显式 null，契约要求该字段随 fps 一同存在，
    /// 省略或补成 0 都会把「尚未测量」伪装成「实测为零」。
    /// </summary>
    [Fact]
    public void UnmeasuredFrameRateIsWrittenAsExplicitNull()
    {
        var sample = JsonNode.Parse(SpecTestHost.ProjectionFullJson())!.AsObject();
        sample["video"]!["fps"]!["measured"] = null;

        var spec = SpecTestHost.Loader.ParseProjection(sample.ToJsonString());

        Assert.Equal(30, spec.Video.Fps!.Target);
        Assert.Null(spec.Video.Fps.Measured);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Contains("\"measured\": null", reserialized, StringComparison.Ordinal);
        Assert.True(SpecTestHost.Validator.ValidateProjection(reserialized).IsValid);
    }

    [Fact]
    public void ProjectionPlatformConfigIsPreservedVerbatim()
    {
        var json = SpecTestHost.ProjectionFullJson();

        var spec = SpecTestHost.Loader.ParseProjection(json);

        Assert.NotNull(spec.PlatformConfig);

        var original = JsonNode.Parse(json)!["platformConfig"]!;
        var reparsed = JsonSerializer.SerializeToNode(spec.PlatformConfig!.Value);

        Assert.True(JsonNode.DeepEquals(original, reparsed), "platformConfig 未能原样保留");
    }

    /// <summary>
    /// 状态以小写字面量对齐契约，写出时不得退化为枚举成员名。
    /// </summary>
    [Theory]
    [InlineData(ProjectionState.Pending, "pending")]
    [InlineData(ProjectionState.Active, "active")]
    [InlineData(ProjectionState.Stopped, "stopped")]
    public void ProjectionStateIsWrittenAsLowerCaseLiteral(ProjectionState state, string literal)
    {
        var spec = new ProjectionSpec
        {
            Id = "proj-probe-01",
            InstanceRef = "win-main-01",
            Video = new ProjectionVideo { Width = 1280, Height = 720 },
            State = state
        };

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Contains($"\"state\": \"{literal}\"", reserialized, StringComparison.Ordinal);
        Assert.Equal(state == ProjectionState.Active, spec.IsPresenting());
        Assert.Equal(state == ProjectionState.Stopped, spec.IsTerminated());

        Assert.True(SpecTestHost.Validator.ValidateProjection(reserialized).IsValid);
    }

    [Fact]
    public void UndeclaredProjectionStateIsRejectedByTheModel()
    {
        var sample = JsonNode.Parse(SpecTestHost.ProjectionMinimalJson())!.AsObject();
        sample["state"] = "reconnecting";

        var result = SpecTestHost.Validator.ValidateProjection(sample.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path == "/state");
    }
}

/// <summary>
/// 传输任务的往返测试。传输任务的方向、冲突策略与状态决定界面如何呈现进度，
/// 任一字段在往返中丢失都会让界面按缺省含义误报。
/// </summary>
public class FileTransferModelRoundTripTests
{
    /// <summary>两份传输任务样例文件名。</summary>
    public static TheoryData<string> FileTransferFixtureFiles =>
        new()
        {
            { SpecLoader.FileTransferMinimalFixtureName },
            { SpecLoader.FileTransferFullFixtureName }
        };

    [Theory]
    [MemberData(nameof(FileTransferFixtureFiles))]
    public void FileTransferSampleParsesIntoModel(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.LoadFileTransferFixture(fixtureFileName);

        Assert.Matches(@"^\d+\.\d+\.\d+$", spec.SchemaVersion);
        Assert.Matches("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", spec.Id);
        Assert.Matches("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", spec.InstanceRef);
        Assert.Contains(spec.Direction, new[] { "host-to-instance", "instance-to-host" });
        Assert.False(string.IsNullOrWhiteSpace(spec.Paths.Source));
        Assert.False(string.IsNullOrWhiteSpace(spec.Paths.Target));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", spec.CreatedAt);
    }

    [Theory]
    [MemberData(nameof(FileTransferFixtureFiles))]
    public void FileTransferRoundTripKeepsEveryDeclaredField(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.LoadFileTransferFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Equal(
            JsonLeafPaths.NonNullLeaves(JsonNode.Parse(json)),
            JsonLeafPaths.NonNullLeaves(JsonNode.Parse(reserialized)));
    }

    [Theory]
    [MemberData(nameof(FileTransferFixtureFiles))]
    public void FileTransferRoundTripIsSemanticallyEquivalent(string fixtureFileName)
    {
        var first = SpecTestHost.Loader.LoadFileTransferFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);
        var second = SpecTestHost.Loader.ParseFileTransfer(reserialized);

        var secondPass = JsonSerializer.Serialize(second, SpecLoader.SerializerOptions);

        Assert.Equal(reserialized, secondPass);
        Assert.Equal(first.Direction, second.Direction);
        Assert.Equal(first.Paths.Source, second.Paths.Source);
        Assert.Equal(first.Paths.Target, second.Paths.Target);
        Assert.Equal(first.Recursive, second.Recursive);
        Assert.Equal(first.Overwrite, second.Overwrite);
        Assert.Equal(first.State, second.State);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(first.StartedAt, second.StartedAt);
        Assert.Equal(first.FinishedAt, second.FinishedAt);
        Assert.Equal(first.FailureReason, second.FailureReason);
    }

    [Theory]
    [MemberData(nameof(FileTransferFixtureFiles))]
    public void RoundTrippedFileTransferStillPassesSchema(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.LoadFileTransferFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        var result = SpecTestHost.Validator.ValidateFileTransfer(reserialized);

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void MinimalFileTransferDoesNotGainOptionalFields()
    {
        var spec = SpecTestHost.Loader.LoadFileTransferFixture(SpecLoader.FileTransferMinimalFixtureName);

        Assert.Null(spec.Recursive);
        Assert.Null(spec.Progress);
        Assert.Null(spec.StartedAt);
        Assert.Null(spec.FinishedAt);
        Assert.Null(spec.FailureReason);
        Assert.Null(spec.PlatformConfig);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.DoesNotContain("\"recursive\"", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"progress\"", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("startedAt", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("finishedAt", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("failureReason", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void FullFileTransferKeepsProgressAndDirectoryTransfer()
    {
        var spec = SpecTestHost.Loader.LoadFileTransferFixture(SpecLoader.FileTransferFullFixtureName);

        Assert.True(spec.Recursive);
        Assert.Equal("instance-to-host", spec.Direction);
        Assert.Equal(ConflictPolicy.Rename, spec.Overwrite);
        Assert.Equal(FileTransferState.Running, spec.State);
        Assert.False(spec.IsTerminal());
        Assert.NotNull(spec.Progress);
        Assert.Equal(48366080L, spec.Progress!.BytesTransferred);
        Assert.Equal("logs/tombstone_0007.zip", spec.Progress.CurrentEntry);
    }

    /// <summary>
    /// 总量未知时不得自行折算百分比，也不得用已传字节数除以零值伪装成确定进度；
    /// 因此总字节数为空既要在解析后保持为空，写出时也不得补成 0。
    /// </summary>
    [Fact]
    public void UnknownTotalBytesIsNotFabricated()
    {
        var spec = SpecTestHost.Loader.LoadFileTransferFixture(SpecLoader.FileTransferFullFixtureName);

        Assert.NotNull(spec.Progress);
        Assert.Null(spec.Progress!.TotalBytes);
        Assert.False(spec.Progress.HasKnownTotal());

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.DoesNotContain("totalBytes", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("finishedAt", reserialized, StringComparison.Ordinal);
        Assert.True(SpecTestHost.Validator.ValidateFileTransfer(reserialized).IsValid);
    }

    /// <summary>
    /// 冲突策略决定目标已存在同名项时如何处置，写出时不得退化为枚举成员名。
    /// </summary>
    [Theory]
    [InlineData(ConflictPolicy.Overwrite, "overwrite")]
    [InlineData(ConflictPolicy.Skip, "skip")]
    [InlineData(ConflictPolicy.Rename, "rename")]
    public void ConflictPolicyIsWrittenAsLowerCaseLiteral(ConflictPolicy policy, string literal)
    {
        var spec = new FileTransferSpec
        {
            Id = "ft-probe-01",
            InstanceRef = "win-main-01",
            Direction = "host-to-instance",
            Paths = new FileTransferPaths { Source = @"D:\Downloads\a.zip", Target = "/data/local/tmp/a.zip" },
            Overwrite = policy,
            State = FileTransferState.Queued,
            CreatedAt = "2026-10-10T09:40:00+08:00"
        };

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Contains($"\"overwrite\": \"{literal}\"", reserialized, StringComparison.Ordinal);
        Assert.True(SpecTestHost.Validator.ValidateFileTransfer(reserialized).IsValid);
    }

    [Theory]
    [InlineData(FileTransferState.Queued, false)]
    [InlineData(FileTransferState.Running, false)]
    [InlineData(FileTransferState.Completed, true)]
    [InlineData(FileTransferState.Failed, true)]
    [InlineData(FileTransferState.Cancelled, true)]
    public void TerminalStatesAreRecognised(FileTransferState state, bool terminal)
    {
        var spec = new FileTransferSpec
        {
            Id = "ft-probe-02",
            InstanceRef = "win-main-01",
            Direction = "host-to-instance",
            Paths = new FileTransferPaths { Source = @"D:\Downloads\a.zip", Target = "/data/local/tmp/a.zip" },
            State = state,
            CreatedAt = "2026-10-10T09:40:00+08:00"
        };

        Assert.Equal(terminal, spec.IsTerminal());

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Contains($"\"state\": \"{state.ToString().ToLowerInvariant()}\"", reserialized, StringComparison.Ordinal);
        Assert.True(SpecTestHost.Validator.ValidateFileTransfer(reserialized).IsValid);
    }

    [Fact]
    public void FileTransferPlatformConfigIsPreservedVerbatim()
    {
        var json = SpecTestHost.FileTransferFullJson();

        var spec = SpecTestHost.Loader.ParseFileTransfer(json);

        Assert.NotNull(spec.PlatformConfig);

        var original = JsonNode.Parse(json)!["platformConfig"]!;
        var reparsed = JsonSerializer.SerializeToNode(spec.PlatformConfig!.Value);

        Assert.True(JsonNode.DeepEquals(original, reparsed), "platformConfig 未能原样保留");
    }
}

/// <summary>
/// 模块记录的往返测试。模块清单属于外部生态格式，
/// 契约只约束其中五个字段，其余键必须原样穿过模型而不被丢弃。
/// </summary>
public class ModuleModelRoundTripTests
{
    /// <summary>两份模块样例文件名。</summary>
    public static TheoryData<string> ModuleFixtureFiles =>
        new()
        {
            { SpecLoader.ModuleMinimalFixtureName },
            { SpecLoader.ModuleFullFixtureName }
        };

    [Theory]
    [MemberData(nameof(ModuleFixtureFiles))]
    public void ModuleSampleParsesIntoModel(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.LoadModuleFixture(fixtureFileName);

        Assert.Matches(@"^\d+\.\d+\.\d+$", spec.SchemaVersion);
        Assert.Matches("^(?!\\.)(?!.*\\.\\.)[A-Za-z0-9._-]{1,255}$", spec.Id);
        Assert.Matches("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", spec.InstanceRef);
        Assert.False(string.IsNullOrWhiteSpace(spec.Install.SourceZip));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", spec.Install.InstalledAt);
    }

    [Theory]
    [MemberData(nameof(ModuleFixtureFiles))]
    public void ModuleRoundTripKeepsEveryDeclaredField(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.LoadModuleFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Equal(
            JsonLeafPaths.NonNullLeaves(JsonNode.Parse(json)),
            JsonLeafPaths.NonNullLeaves(JsonNode.Parse(reserialized)));
    }

    [Theory]
    [MemberData(nameof(ModuleFixtureFiles))]
    public void ModuleRoundTripIsSemanticallyEquivalent(string fixtureFileName)
    {
        var first = SpecTestHost.Loader.LoadModuleFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);
        var second = SpecTestHost.Loader.ParseModule(reserialized);

        var secondPass = JsonSerializer.Serialize(second, SpecLoader.SerializerOptions);

        Assert.Equal(reserialized, secondPass);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.InstanceRef, second.InstanceRef);
        Assert.Equal(first.RemotePath, second.RemotePath);
        Assert.Equal(first.Install.State, second.Install.State);
        Assert.Equal(first.Install.InstalledAt, second.Install.InstalledAt);
        Assert.Equal(first.Install.SourceZip, second.Install.SourceZip);
    }

    [Theory]
    [MemberData(nameof(ModuleFixtureFiles))]
    public void RoundTrippedModuleStillPassesSchema(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.LoadModuleFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        var result = SpecTestHost.Validator.ValidateModule(reserialized);

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    [Fact]
    public void MinimalModuleDoesNotGainOptionalFields()
    {
        var spec = SpecTestHost.Loader.LoadModuleFixture(SpecLoader.ModuleMinimalFixtureName);

        Assert.Null(spec.RemotePath);
        Assert.Null(spec.Manifest);
        Assert.Null(spec.Layout);
        Assert.Null(spec.PlatformConfig);
        Assert.Equal(ModuleInstallState.Installed, spec.Install.State);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.DoesNotContain("remotePath", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"manifest\"", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"layout\"", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("platformConfig", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void FullModuleKeepsManifestAndLayout()
    {
        var spec = SpecTestHost.Loader.LoadModuleFixture(SpecLoader.ModuleFullFixtureName);

        Assert.Equal("/data/adb/modules/zygisk_next", spec.RemotePath);
        Assert.Equal(ModuleInstallState.Enabled, spec.Install.State);
        Assert.True(spec.IsEnabledOnNextBoot());
        Assert.False(spec.IsDisabled());

        Assert.NotNull(spec.Manifest);
        Assert.Equal("zygisk_next", spec.Manifest!.Id);
        Assert.Equal("Zygisk Next", spec.Manifest.Name);
        Assert.Equal("0.8.0", spec.Manifest.Version);

        Assert.NotNull(spec.Layout);
        Assert.True(spec.Layout!.SystemOverlay);
        Assert.True(spec.Layout.HasStageScript("post-fs-data.sh"));
        Assert.True(spec.Layout.HasStageScript("service.sh"));
        Assert.False(spec.Layout.HasStageScript("boot-completed.sh"));
    }

    /// <summary>
    /// 清单属于外部生态格式，允许出现契约未列出的额外键。
    /// 一旦模型把它们丢掉，另一端写入的字段就会在往返中消失。
    /// </summary>
    [Fact]
    public void ManifestKeepsUndeclaredFieldsAsEscapeHatch()
    {
        var spec = SpecTestHost.Loader.LoadModuleFixture(SpecLoader.ModuleFullFixtureName);

        Assert.NotNull(spec.Manifest);
        Assert.NotNull(spec.Manifest!.ExtensionData);
        Assert.True(spec.Manifest.ExtensionData!.ContainsKey("versionCode"));

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Contains("\"versionCode\": 800", reserialized, StringComparison.Ordinal);
        Assert.True(SpecTestHost.Validator.ValidateModule(reserialized).IsValid);
    }

    [Theory]
    [InlineData(ModuleInstallState.Installed, "installed", false, false)]
    [InlineData(ModuleInstallState.Enabled, "enabled", true, false)]
    [InlineData(ModuleInstallState.Disabled, "disabled", false, true)]
    public void ModuleInstallStateIsWrittenAsLowerCaseLiteral(
        ModuleInstallState state,
        string literal,
        bool enabledOnNextBoot,
        bool disabled)
    {
        var spec = new ModuleSpec
        {
            Id = "probe_module",
            InstanceRef = "win-main-01",
            Install = new ModuleInstall
            {
                State = state,
                InstalledAt = "2026-10-10T09:12:00+08:00",
                SourceZip = "probe-module.zip"
            }
        };

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Contains($"\"state\": \"{literal}\"", reserialized, StringComparison.Ordinal);
        Assert.Equal(enabledOnNextBoot, spec.IsEnabledOnNextBoot());
        Assert.Equal(disabled, spec.IsDisabled());
        Assert.True(SpecTestHost.Validator.ValidateModule(reserialized).IsValid);
    }

    [Fact]
    public void ModulePlatformConfigIsPreservedVerbatim()
    {
        var json = SpecTestHost.ModuleFullJson();

        var spec = SpecTestHost.Loader.ParseModule(json);

        Assert.NotNull(spec.PlatformConfig);

        var original = JsonNode.Parse(json)!["platformConfig"]!;
        var reparsed = JsonSerializer.SerializeToNode(spec.PlatformConfig!.Value);

        Assert.True(JsonNode.DeepEquals(original, reparsed), "platformConfig 未能原样保留");
    }
}

/// <summary>
/// 应用记录的往返测试。未安装的包只有身份与状态可言，
/// 版本、ABI、安装时刻、入口组件与安装来源一旦在往返中凭空出现即为伪造。
/// </summary>
public class ApplicationModelRoundTripTests
{
    /// <summary>两份应用样例文件名。</summary>
    public static TheoryData<string> ApplicationFixtureFiles =>
        new()
        {
            { SpecLoader.ApplicationMinimalFixtureName },
            { SpecLoader.ApplicationFullFixtureName }
        };

    [Theory]
    [MemberData(nameof(ApplicationFixtureFiles))]
    public void ApplicationSampleParsesIntoModel(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.LoadApplicationFixture(fixtureFileName);

        Assert.Matches(@"^\d+\.\d+\.\d+$", spec.SchemaVersion);
        Assert.Matches("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", spec.PackageName);
        Assert.Matches("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", spec.InstanceRef);
    }

    [Theory]
    [MemberData(nameof(ApplicationFixtureFiles))]
    public void ApplicationRoundTripKeepsEveryDeclaredField(string fixtureFileName)
    {
        var json = SpecTestHost.Loader.ReadFixtureText(fixtureFileName);

        var spec = SpecTestHost.Loader.LoadApplicationFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Equal(
            JsonLeafPaths.NonNullLeaves(JsonNode.Parse(json)),
            JsonLeafPaths.NonNullLeaves(JsonNode.Parse(reserialized)));
    }

    [Theory]
    [MemberData(nameof(ApplicationFixtureFiles))]
    public void ApplicationRoundTripIsSemanticallyEquivalent(string fixtureFileName)
    {
        var first = SpecTestHost.Loader.LoadApplicationFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);
        var second = SpecTestHost.Loader.ParseApplication(reserialized);

        var secondPass = JsonSerializer.Serialize(second, SpecLoader.SerializerOptions);

        Assert.Equal(reserialized, secondPass);
        Assert.Equal(first.PackageName, second.PackageName);
        Assert.Equal(first.InstanceRef, second.InstanceRef);
        Assert.Equal(first.InstallState, second.InstallState);
        Assert.Equal(first.VersionName, second.VersionName);
        Assert.Equal(first.VersionCode, second.VersionCode);
        Assert.Equal(first.PrimaryCpuAbi, second.PrimaryCpuAbi);
        Assert.Equal(first.InstalledAt, second.InstalledAt);
        Assert.Equal(first.LaunchActivity, second.LaunchActivity);
    }

    [Theory]
    [MemberData(nameof(ApplicationFixtureFiles))]
    public void RoundTrippedApplicationStillPassesSchema(string fixtureFileName)
    {
        var spec = SpecTestHost.Loader.LoadApplicationFixture(fixtureFileName);
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        var result = SpecTestHost.Validator.ValidateApplication(reserialized);

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    /// <summary>
    /// 未安装的包只有身份与状态可言。给未安装的包标注版本号或 ABI 没有可观测来源，
    /// 往返时凭空补出这些字段等于诱使两端各自编造。
    /// </summary>
    [Fact]
    public void MinimalApplicationDoesNotGainOptionalFields()
    {
        var spec = SpecTestHost.Loader.LoadApplicationFixture(SpecLoader.ApplicationMinimalFixtureName);

        Assert.Equal(ApplicationInstallState.NotInstalled, spec.InstallState);
        Assert.False(spec.IsInstalled());
        Assert.Null(spec.VersionName);
        Assert.Null(spec.VersionCode);
        Assert.Null(spec.PrimaryCpuAbi);
        Assert.Null(spec.InstalledAt);
        Assert.Null(spec.LaunchActivity);
        Assert.Null(spec.Source);
        Assert.Null(spec.LastOperation);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.DoesNotContain("versionName", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("versionCode", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("primaryCpuAbi", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("installedAt", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("launchActivity", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\"", reserialized, StringComparison.Ordinal);
        Assert.DoesNotContain("lastOperation", reserialized, StringComparison.Ordinal);
        Assert.True(SpecTestHost.Validator.ValidateApplication(reserialized).IsValid);
    }

    [Fact]
    public void FullApplicationKeepsSourceAndLastOperation()
    {
        var spec = SpecTestHost.Loader.LoadApplicationFixture(SpecLoader.ApplicationFullFixtureName);

        Assert.Equal(ApplicationInstallState.Installed, spec.InstallState);
        Assert.True(spec.IsInstalled());
        Assert.Equal("3.8.2", spec.VersionName);
        Assert.Equal(30802, spec.VersionCode);
        Assert.Equal("arm64-v8a", spec.PrimaryCpuAbi);
        Assert.Equal(".MainActivity", spec.LaunchActivity);

        Assert.NotNull(spec.Source);
        Assert.Equal("HandyPlayer_3.8.2_arm64-v8a.apk", spec.Source!.FileName);
        Assert.Equal(48213904L, spec.Source.SizeBytes);

        Assert.NotNull(spec.LastOperation);
        Assert.Equal(ApplicationOperationKind.Install, spec.LastOperation!.Operation);
        Assert.Equal(spec.PackageName, spec.LastOperation.PackageName);
        Assert.True(spec.LastOperation.IsSuccess());
        Assert.Equal(8420, spec.LastOperation.DurationMs);
        Assert.Null(spec.LastOperation.FailureReason);
    }

    /// <summary>
    /// 安装状态是契约里唯一采用小驼峰字面量的枚举，
    /// 写出时退化为全小写会让另一端读到的取值落在域外。
    /// </summary>
    [Theory]
    [InlineData(ApplicationInstallState.Installed, "installed")]
    [InlineData(ApplicationInstallState.NotInstalled, "notInstalled")]
    public void InstallStateIsWrittenAsCamelCaseLiteral(ApplicationInstallState state, string literal)
    {
        var spec = new ApplicationSpec
        {
            PackageName = "com.example.handyplayer",
            InstanceRef = "win-main-01",
            InstallState = state
        };

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Contains($"\"installState\": \"{literal}\"", reserialized, StringComparison.Ordinal);
        Assert.True(SpecTestHost.Validator.ValidateApplication(reserialized).IsValid);
    }

    [Fact]
    public void FailedOperationCarriesItsAttribution()
    {
        var spec = new ApplicationOperation
        {
            Id = "appop-probe-01",
            InstanceRef = "win-main-01",
            Operation = ApplicationOperationKind.Install,
            PackageName = "com.example.handyplayer",
            Result = ApplicationOperationResult.Failure,
            OccurredAt = "2026-10-10T21:14:07+08:00",
            DurationMs = 410,
            FailureReason = new ApplicationOperationFailure
            {
                Category = ApplicationErrorCategory.Guest,
                Code = "INSTALL_FAILED_NO_MATCHING_ABIS",
                Message = " INSTALL_FAILED_NO_MATCHING_ABIS: Failed to extract native libraries"
            }
        };

        Assert.False(spec.IsSuccess());

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        Assert.Contains("\"result\": \"failure\"", reserialized, StringComparison.Ordinal);
        Assert.Contains("\"category\": \"guest\"", reserialized, StringComparison.Ordinal);
        Assert.Contains("INSTALL_FAILED_NO_MATCHING_ABIS", reserialized, StringComparison.Ordinal);

        var json = JsonNode.Parse(JsonSerializer.Serialize(new ApplicationSpec
        {
            PackageName = "com.example.handyplayer",
            InstanceRef = "win-main-01",
            InstallState = ApplicationInstallState.Installed,
            LastOperation = spec
        }, SpecLoader.SerializerOptions))!;

        Assert.True(SpecTestHost.Validator.ValidateApplication(json.ToJsonString()).IsValid);
    }

    [Fact]
    public void SchemaRejectsVersionFieldsOnAnUninstalledPackage()
    {
        var sample = JsonNode.Parse(SpecTestHost.ApplicationMinimalJson())!.AsObject();
        sample["versionName"] = "3.8.2";

        var result = SpecTestHost.Validator.ValidateApplication(sample.ToJsonString());

        Assert.False(result.IsValid, "未安装的包不得携带版本名");
    }

    [Fact]
    public void SchemaRejectsFailureAttributionOnASuccessfulOperation()
    {
        var sample = JsonNode.Parse(SpecTestHost.ApplicationFullJson())!.AsObject();
        sample["lastOperation"]!["failureReason"] = new JsonObject
        {
            ["category"] = "guest",
            ["code"] = "INSTALL_FAILED_OLDER_SDK"
        };

        var result = SpecTestHost.Validator.ValidateApplication(sample.ToJsonString());

        Assert.False(result.IsValid, "成功的操作不得携带失败归因");
    }
}

/// <summary>
/// JSON 叶子路径收集，供四份新契约的往返比对共用。
/// </summary>
internal static class JsonLeafPaths
{
    /// <summary>
    /// 递归收集 JSON 文档中全部非空叶子节点的路径与取值。
    /// 显式 null 不参与比对：契约允许可空字段写成 null，
    /// 而写出时缺省的可选字段不被补成 null，两者语义等价。
    /// </summary>
    /// <param name="node">待遍历的 JSON 节点。</param>
    /// <param name="prefix">路径前缀，根节点为空串。</param>
    /// <returns>按序号排序的叶子路径与取值。</returns>
    public static List<string> NonNullLeaves(JsonNode? node, string prefix = "")
    {
        var paths = new List<string>();

        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    paths.AddRange(NonNullLeaves(property.Value, $"{prefix}/{property.Key}"));
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    paths.AddRange(NonNullLeaves(array[i], $"{prefix}/{i}"));
                }

                break;

            case null:
                break;

            default:
                paths.Add($"{prefix}={node.ToJsonString()}");
                break;
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }
}
