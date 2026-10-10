using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XBear.Core.Applications;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;
using XBear.Core.Tests.Adb;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.Applications;

/// <summary>
/// 宿主侧应用管理服务的单元与集成测试：基于内存内的假 adbd 验证应用列表解析、
/// 安装校验、覆盖安装、卸载、应用拉起与操作记录的契约遵从性。
/// </summary>
public sealed class ApplicationServiceTests : IDisposable
{
    private const string DefaultInstanceId = "win-main-01";
    private const string DefaultPackageName = "com.example.handyplayer";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "xbear-app-" + Guid.NewGuid().ToString("N"));

    /// <summary>初始化测试用的宿主临时目录。</summary>
    public ApplicationServiceTests() => Directory.CreateDirectory(_root);

    /// <summary>清理测试产生的临时文件与目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// 创建指向假 adbd 的应用管理服务与目标实例组合。
    /// </summary>
    /// <param name="server">假 adbd 服务端。</param>
    /// <param name="instanceId">目标实例标识。</param>
    /// <returns>应用管理服务与目标实例的元组。</returns>
    private static (ApplicationService Service, ApplicationTarget Target) CreateService(
        FakeAdbdServer server,
        string instanceId = DefaultInstanceId) =>
        (new ApplicationService(), new ApplicationTarget(instanceId, server.Port));

    /// <summary>
    /// 在宿主临时目录中构造一个带有合法 ZIP 头魔数的假 APK 应用包。
    /// </summary>
    /// <param name="fileName">应用包文件名，须符合契约命名约束。</param>
    /// <param name="entryContent">归档内清单占位内容。</param>
    /// <returns>应用包在宿主上的绝对路径。</returns>
    private string CreateApk(string fileName, string entryContent = "xbear-manifest")
    {
        string path = Path.Combine(_root, fileName);
        using FileStream file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry("AndroidManifest.xml");
        using Stream stream = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(entryContent);
        stream.Write(bytes, 0, bytes.Length);
        return path;
    }

    [Fact]
    public async Task 列表_解析多个第三方应用并提取版本与ABI()
    {
        await using var server = new FakeAdbdServer();
        server.ShellHandler = command =>
        {
            if (command == ApplicationService.PackageListCommand)
            {
                return new ShellResponse(
                    "package:com.example.handyplayer\npackage:com.example.purejava\npackage:com.example.unknownabi\n",
                    0);
            }

            if (command == string.Format(ApplicationService.PackageDumpCommandFormat, "com.example.handyplayer"))
            {
                return new ShellResponse(
                    "Packages:\n  Package [com.example.handyplayer]:\n    userId=10123\n    versionCode=30802 minSdk=24 targetSdk=33\n    versionName=3.8.2\n    primaryCpuAbi=arm64-v8a\n",
                    0);
            }

            if (command == string.Format(ApplicationService.PackageDumpCommandFormat, "com.example.purejava"))
            {
                return new ShellResponse(
                    "Packages:\n  Package [com.example.purejava]:\n    userId=10124\n    versionCode=100\n    versionName=1.0.0\n    primaryCpuAbi=null\n",
                    0);
            }

            if (command == string.Format(ApplicationService.PackageDumpCommandFormat, "com.example.unknownabi"))
            {
                return new ShellResponse(
                    "Packages:\n  Package [com.example.unknownabi]:\n    userId=10125\n    versionCode=200\n    versionName=2.0.0\n    primaryCpuAbi=mips\n",
                    0);
            }

            return new ShellResponse(string.Empty, 0);
        };

        (ApplicationService service, ApplicationTarget target) = CreateService(server);

        IReadOnlyList<ApplicationSpec> apps = await service.ListAsync(target);

        Assert.Equal(3, apps.Count);
        Assert.Equal(
            new[] { "com.example.handyplayer", "com.example.purejava", "com.example.unknownabi" },
            apps.Select(a => a.PackageName));

        ApplicationSpec handy = apps[0];
        Assert.Equal("com.example.handyplayer", handy.PackageName);
        Assert.Equal(DefaultInstanceId, handy.InstanceRef);
        Assert.Equal(ApplicationInstallState.Installed, handy.InstallState);
        Assert.Equal(30802, handy.VersionCode);
        Assert.Equal("3.8.2", handy.VersionName);
        Assert.Equal("arm64-v8a", handy.PrimaryCpuAbi);

        ApplicationSpec pure = apps[1];
        Assert.Equal("com.example.purejava", pure.PackageName);
        Assert.Equal(100, pure.VersionCode);
        Assert.Equal("1.0.0", pure.VersionName);
        Assert.Equal("none", pure.PrimaryCpuAbi);

        ApplicationSpec unknown = apps[2];
        Assert.Equal("com.example.unknownabi", unknown.PackageName);
        Assert.Equal(200, unknown.VersionCode);
        Assert.Equal("2.0.0", unknown.VersionName);
        Assert.Null(unknown.PrimaryCpuAbi);

        foreach (ApplicationSpec app in apps)
        {
            string json = JsonSerializer.Serialize(app, SpecLoader.SerializerOptions);
            Assert.True(SpecTestHost.Validator.ValidateApplication(json).IsValid);
        }
    }

    [Fact]
    public async Task 列表_包详情取不到版本与ABI时优雅降级()
    {
        await using var server = new FakeAdbdServer();
        server.ShellHandler = command =>
        {
            if (command == ApplicationService.PackageListCommand)
            {
                return new ShellResponse("package:com.example.minimal\n", 0);
            }

            if (command == string.Format(ApplicationService.PackageDumpCommandFormat, "com.example.minimal"))
            {
                return new ShellResponse("Packages:\n  Package [com.example.minimal]:\n    userId=10126\n", 0);
            }

            return new ShellResponse(string.Empty, 0);
        };

        (ApplicationService service, ApplicationTarget target) = CreateService(server);

        IReadOnlyList<ApplicationSpec> apps = await service.ListAsync(target);

        Assert.Single(apps);
        ApplicationSpec app = apps[0];
        Assert.Equal("com.example.minimal", app.PackageName);
        Assert.Equal(ApplicationInstallState.Installed, app.InstallState);
        Assert.Null(app.VersionName);
        Assert.Null(app.VersionCode);
        Assert.Null(app.PrimaryCpuAbi);

        string json = JsonSerializer.Serialize(app, SpecLoader.SerializerOptions);
        Assert.True(SpecTestHost.Validator.ValidateApplication(json).IsValid);
    }

    [Fact]
    public async Task 列表_无第三方应用时返回空列表()
    {
        await using var server = new FakeAdbdServer();
        server.ShellHandler = command =>
            command == ApplicationService.PackageListCommand
                ? new ShellResponse(string.Empty, 0)
                : new ShellResponse(string.Empty, 0);

        (ApplicationService service, ApplicationTarget target) = CreateService(server);

        IReadOnlyList<ApplicationSpec> apps = await service.ListAsync(target);

        Assert.Empty(apps);
    }

    [Fact]
    public async Task 安装_校验拒绝_文件不存在()
    {
        await using var server = new FakeAdbdServer();
        (ApplicationService service, ApplicationTarget target) = CreateService(server);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.InstallAsync(target, Path.Combine(_root, "non_existent.apk"), DefaultPackageName));

        Assert.Equal(ErrorCategory.Storage, error.Category);
        Assert.Empty(server.RawSyncFrames);
    }

    [Fact]
    public async Task 安装_校验拒绝_非ZIP魔数()
    {
        await using var server = new FakeAdbdServer();
        (ApplicationService service, ApplicationTarget target) = CreateService(server);

        string corruptApk = Path.Combine(_root, "corrupt.apk");
        await File.WriteAllTextAsync(corruptApk, "这不是一个合法的 ZIP 文件开头");

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.InstallAsync(target, corruptApk, DefaultPackageName));

        Assert.Equal(ErrorCategory.Storage, error.Category);
        Assert.Empty(server.RawSyncFrames);
    }

    [Theory]
    [InlineData("InvalidFileName!.apk", "com.example.handyplayer")]
    [InlineData("Valid.apk", "InvalidPackageName")]
    [InlineData("Valid.apk", "com.example.UpperCase")]
    [InlineData("Valid.apk", "com.123startwithnumber")]
    public async Task 安装_校验拒绝_非法文件名或非法包名(string fileName, string packageName)
    {
        await using var server = new FakeAdbdServer();
        (ApplicationService service, ApplicationTarget target) = CreateService(server);

        string apkPath = CreateApk(fileName);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.InstallAsync(target, apkPath, packageName));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Empty(server.RawSyncFrames);
    }

    [Fact]
    public async Task 安装_成功路径_推送文件执行安装并清理暂存且刷盘()
    {
        await using var server = new FakeAdbdServer();
        string? stagedFilePath = null;
        bool syncObserved = false;
        bool rmObserved = false;

        server.ShellHandler = command =>
        {
            if (command.StartsWith("pm install -r ", StringComparison.Ordinal))
            {
                stagedFilePath = command["pm install -r ".Length..].Trim();
                Assert.NotNull(server.GetFile(stagedFilePath));
                return new ShellResponse("Success\n", 0);
            }

            if (command.StartsWith("rm -f ", StringComparison.Ordinal))
            {
                rmObserved = true;
                return new ShellResponse(string.Empty, 0);
            }

            if (command == ApplicationService.FlushCommand)
            {
                syncObserved = true;
                return new ShellResponse(string.Empty, 0);
            }

            return new ShellResponse(string.Empty, 0);
        };

        (ApplicationService service, ApplicationTarget target) = CreateService(server);
        string apkPath = CreateApk("HandyPlayer_3.8.2.apk");

        ApplicationOperation op = await service.InstallAsync(target, apkPath, DefaultPackageName);

        Assert.True(op.IsSuccess());
        Assert.Equal(ApplicationOperationKind.Install, op.Operation);
        Assert.Equal(DefaultPackageName, op.PackageName);
        Assert.Equal(DefaultInstanceId, op.InstanceRef);
        Assert.Equal(ApplicationOperationResult.Success, op.Result);
        Assert.Null(op.FailureReason);
        Assert.True(op.DurationMs >= 0);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T", op.OccurredAt);
        Assert.Matches(@"^appop-[a-z0-9-]+$", op.Id);

        Assert.NotNull(stagedFilePath);
        Assert.StartsWith(ApplicationService.GuestStagingDirectory, stagedFilePath);
        Assert.True(rmObserved);
        Assert.True(syncObserved);
        Assert.Null(server.GetFile(stagedFilePath));

        var appSpec = new ApplicationSpec
        {
            PackageName = DefaultPackageName,
            InstanceRef = DefaultInstanceId,
            InstallState = ApplicationInstallState.Installed,
            Source = new ApplicationSource
            {
                FileName = Path.GetFileName(apkPath)
            },
            LastOperation = op
        };
        string json = JsonSerializer.Serialize(appSpec, SpecLoader.SerializerOptions);
        Assert.True(SpecTestHost.Validator.ValidateApplication(json).IsValid);
    }

    [Fact]
    public async Task 安装_失败路径_原样传播包管理器错误且清理暂存文件不刷盘()
    {
        await using var server = new FakeAdbdServer();
        string? stagedFilePath = null;
        bool rmObserved = false;
        bool syncObserved = false;

        server.ShellHandler = command =>
        {
            if (command.StartsWith("pm install -r ", StringComparison.Ordinal))
            {
                stagedFilePath = command["pm install -r ".Length..].Trim();
                return new ShellResponse("Failure [INSTALL_FAILED_VERSION_DOWNGRADE]\n", 1);
            }

            if (command.StartsWith("rm -f ", StringComparison.Ordinal))
            {
                rmObserved = true;
                return new ShellResponse(string.Empty, 0);
            }

            if (command == ApplicationService.FlushCommand)
            {
                syncObserved = true;
                return new ShellResponse(string.Empty, 0);
            }

            return new ShellResponse(string.Empty, 0);
        };

        (ApplicationService service, ApplicationTarget target) = CreateService(server);
        string apkPath = CreateApk("HandyPlayer_3.8.2.apk");

        ApplicationOperation op = await service.InstallAsync(target, apkPath, DefaultPackageName);

        Assert.False(op.IsSuccess());
        Assert.Equal(ApplicationOperationKind.Install, op.Operation);
        Assert.Equal(ApplicationOperationResult.Failure, op.Result);
        Assert.NotNull(op.FailureReason);
        Assert.Equal(ApplicationErrorCategory.Guest, op.FailureReason!.Category);
        Assert.Equal("INSTALL_FAILED_VERSION_DOWNGRADE", op.FailureReason.Code);
        Assert.Contains("INSTALL_FAILED_VERSION_DOWNGRADE", op.FailureReason.Message);

        Assert.True(rmObserved);
        Assert.False(syncObserved);
        Assert.NotNull(stagedFilePath);
        Assert.Null(server.GetFile(stagedFilePath));

        var appSpec = new ApplicationSpec
        {
            PackageName = DefaultPackageName,
            InstanceRef = DefaultInstanceId,
            InstallState = ApplicationInstallState.NotInstalled,
            LastOperation = op
        };
        string json = JsonSerializer.Serialize(appSpec, SpecLoader.SerializerOptions);
        Assert.True(SpecTestHost.Validator.ValidateApplication(json).IsValid);
    }

    [Fact]
    public async Task 卸载_成功路径_执行卸载并刷盘()
    {
        await using var server = new FakeAdbdServer();
        bool syncObserved = false;

        server.ShellHandler = command =>
        {
            if (command == string.Format(ApplicationService.UninstallCommandFormat, DefaultPackageName))
            {
                return new ShellResponse("Success\n", 0);
            }

            if (command == ApplicationService.FlushCommand)
            {
                syncObserved = true;
                return new ShellResponse(string.Empty, 0);
            }

            return new ShellResponse(string.Empty, 0);
        };

        (ApplicationService service, ApplicationTarget target) = CreateService(server);

        ApplicationOperation op = await service.UninstallAsync(target, DefaultPackageName);

        Assert.True(op.IsSuccess());
        Assert.Equal(ApplicationOperationKind.Uninstall, op.Operation);
        Assert.Equal(DefaultPackageName, op.PackageName);
        Assert.Equal(ApplicationOperationResult.Success, op.Result);
        Assert.Null(op.FailureReason);
        Assert.True(syncObserved);

        var appSpec = new ApplicationSpec
        {
            PackageName = DefaultPackageName,
            InstanceRef = DefaultInstanceId,
            InstallState = ApplicationInstallState.NotInstalled,
            LastOperation = op
        };
        string json = JsonSerializer.Serialize(appSpec, SpecLoader.SerializerOptions);
        Assert.True(SpecTestHost.Validator.ValidateApplication(json).IsValid);
    }

    [Fact]
    public async Task 卸载_失败路径_原样传播包管理器错误且不刷盘()
    {
        await using var server = new FakeAdbdServer();
        bool syncObserved = false;

        server.ShellHandler = command =>
        {
            if (command == string.Format(ApplicationService.UninstallCommandFormat, DefaultPackageName))
            {
                return new ShellResponse("Failure [DELETE_FAILED_INTERNAL_ERROR]\n", 1);
            }

            if (command == ApplicationService.FlushCommand)
            {
                syncObserved = true;
                return new ShellResponse(string.Empty, 0);
            }

            return new ShellResponse(string.Empty, 0);
        };

        (ApplicationService service, ApplicationTarget target) = CreateService(server);

        ApplicationOperation op = await service.UninstallAsync(target, DefaultPackageName);

        Assert.False(op.IsSuccess());
        Assert.Equal(ApplicationOperationKind.Uninstall, op.Operation);
        Assert.Equal(ApplicationOperationResult.Failure, op.Result);
        Assert.NotNull(op.FailureReason);
        Assert.Equal(ApplicationErrorCategory.Guest, op.FailureReason!.Category);
        Assert.Equal("DELETE_FAILED_INTERNAL_ERROR", op.FailureReason.Code);
        Assert.False(syncObserved);

        var appSpec = new ApplicationSpec
        {
            PackageName = DefaultPackageName,
            InstanceRef = DefaultInstanceId,
            InstallState = ApplicationInstallState.NotInstalled,
            LastOperation = op
        };
        string json = JsonSerializer.Serialize(appSpec, SpecLoader.SerializerOptions);
        Assert.True(SpecTestHost.Validator.ValidateApplication(json).IsValid);
    }

    [Fact]
    public async Task 拉起_成功路径_注入事件返回成功()
    {
        await using var server = new FakeAdbdServer();
        server.ShellHandler = command =>
        {
            if (command == string.Format(ApplicationService.LaunchCommandFormat, DefaultPackageName))
            {
                return new ShellResponse(
                    ":Monkey: seed=14243 count=1\n:AllowPackage: com.example.handyplayer\nEvents injected: 1\n",
                    0);
            }

            return new ShellResponse(string.Empty, 0);
        };

        (ApplicationService service, ApplicationTarget target) = CreateService(server);

        ApplicationOperation op = await service.LaunchAsync(target, DefaultPackageName);

        Assert.True(op.IsSuccess());
        Assert.Equal(ApplicationOperationKind.Launch, op.Operation);
        Assert.Equal(DefaultPackageName, op.PackageName);
        Assert.Equal(ApplicationOperationResult.Success, op.Result);
        Assert.Null(op.FailureReason);

        var appSpec = new ApplicationSpec
        {
            PackageName = DefaultPackageName,
            InstanceRef = DefaultInstanceId,
            InstallState = ApplicationInstallState.Installed,
            LastOperation = op
        };
        string json = JsonSerializer.Serialize(appSpec, SpecLoader.SerializerOptions);
        Assert.True(SpecTestHost.Validator.ValidateApplication(json).IsValid);
    }

    [Fact]
    public async Task 拉起_失败路径_未找到入口活动归因为失败()
    {
        await using var server = new FakeAdbdServer();
        server.ShellHandler = command =>
        {
            if (command == string.Format(ApplicationService.LaunchCommandFormat, DefaultPackageName))
            {
                return new ShellResponse(
                    "** No activities found to run, monkey aborted.\n",
                    254);
            }

            return new ShellResponse(string.Empty, 0);
        };

        (ApplicationService service, ApplicationTarget target) = CreateService(server);

        ApplicationOperation op = await service.LaunchAsync(target, DefaultPackageName);

        Assert.False(op.IsSuccess());
        Assert.Equal(ApplicationOperationKind.Launch, op.Operation);
        Assert.Equal(ApplicationOperationResult.Failure, op.Result);
        Assert.NotNull(op.FailureReason);
        Assert.Equal(ApplicationErrorCategory.Guest, op.FailureReason!.Category);
        Assert.Equal("LAUNCH_FAILED_NO_ACTIVITIES", op.FailureReason.Code);
        Assert.Contains("No activities found to run", op.FailureReason.Message);

        var appSpec = new ApplicationSpec
        {
            PackageName = DefaultPackageName,
            InstanceRef = DefaultInstanceId,
            InstallState = ApplicationInstallState.Installed,
            LastOperation = op
        };
        string json = JsonSerializer.Serialize(appSpec, SpecLoader.SerializerOptions);
        Assert.True(SpecTestHost.Validator.ValidateApplication(json).IsValid);
    }

    [Fact]
    public async Task 自定义操作标识_失败重试可保持相同标识()
    {
        await using var server = new FakeAdbdServer();
        server.ShellHandler = command => new ShellResponse("Success\n", 0);

        (ApplicationService service, ApplicationTarget target) = CreateService(server);
        string customId = "appop-win-main-01-retry01";

        ApplicationOperation op = await service.UninstallAsync(
            target,
            DefaultPackageName,
            operationId: customId);

        Assert.Equal(customId, op.Id);
    }

    [Fact]
    public async Task 非法目标实例标识_拒绝操作抛契约异常()
    {
        await using var server = new FakeAdbdServer();
        var service = new ApplicationService();
        var badTarget = new ApplicationTarget("INVALID_INSTANCE!", server.Port);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.ListAsync(badTarget));

        Assert.Equal(ErrorCategory.Spec, error.Category);
    }
}
