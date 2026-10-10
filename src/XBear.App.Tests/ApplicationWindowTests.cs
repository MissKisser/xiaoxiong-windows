using System.IO;
using System.IO.Compression;
using XBear.App.Presentation;
using XBear.App.ViewModels;
using XBear.Core.Applications;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 应用管理窗口与视图模型的单元测试。
/// </summary>
public class ApplicationWindowTests : IDisposable
{
    private const string InstanceId = "win-app-01";
    private const string InstanceName = "测试应用实例";
    private const int AdbPort = 5555;
    private const string TestPackageName = "com.example.demoapp";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "xbear-app-view-test-" + Guid.NewGuid().ToString("N"));

    public ApplicationWindowTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static TerminologyCatalog Terms() =>
        new(XBeeSpec.TestSpec().LoadTerminology());

    private string CreateFakeApk(string fileName)
    {
        string path = Path.Combine(_tempDir, fileName);
        using FileStream file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        archive.CreateEntry("AndroidManifest.xml");
        return path;
    }

    [Fact]
    public void WindowTitleShowsInstanceNameAndTerminology()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new ApplicationService(() => stubAdb);
        var viewModel = new ApplicationWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        Assert.Contains(InstanceName, viewModel.WindowTitle, StringComparison.Ordinal);
        Assert.Contains(Terms().Application, viewModel.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshCommandLoadsApplicationsAndFormatsContractFields()
    {
        var stubAdb = new StubFeatureAdbClient
        {
            ShellHandler = cmd =>
            {
                if (cmd == ApplicationService.PackageListCommand)
                {
                    return "package:com.example.demoapp\npackage:com.example.simple";
                }

                if (cmd.Contains("dumpsys package com.example.demoapp", StringComparison.Ordinal))
                {
                    return "versionCode=12 versionName=2.1.0 primaryCpuAbi=x86_64";
                }

                if (cmd.Contains("dumpsys package com.example.simple", StringComparison.Ordinal))
                {
                    // 缺失版本与 ABI
                    return "some other package dump info";
                }

                return string.Empty;
            }
        };

        var service = new ApplicationService(() => stubAdb);
        var viewModel = new ApplicationWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        await viewModel.RefreshAsync();

        Assert.Equal(2, viewModel.Applications.Count);

        ApplicationItemViewModel first = viewModel.Applications[0];
        Assert.Equal(TestPackageName, first.PackageName);
        Assert.Equal("2.1.0", first.VersionNameText);
        Assert.Equal("12", first.VersionCodeText);
        Assert.Equal("x86_64", first.PrimaryCpuAbiText);
        Assert.Equal("已安装", first.InstallStateText);

        ApplicationItemViewModel second = viewModel.Applications[1];
        Assert.Equal("com.example.simple", second.PackageName);
        Assert.Equal("未声明", second.VersionNameText);
        Assert.Equal("未声明", second.VersionCodeText);
        Assert.Equal("未声明", second.PrimaryCpuAbiText);
    }

    [Fact]
    public void BrowseApkSelectsFileAndAutoFillsPackageName()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new ApplicationService(() => stubAdb);

        string apkPath = CreateFakeApk("com.example.autofill.apk");

        var viewModel = new ApplicationWindowViewModel(
            service,
            InstanceId,
            InstanceName,
            AdbPort,
            Terms(),
            apkFileDialogHandler: _ => apkPath);

        viewModel.BrowseApkCommand.Execute(null);

        Assert.Equal(apkPath, viewModel.SelectedApkPath);
        Assert.Equal("com.example.autofill", viewModel.PackageNameInput);
        Assert.True(viewModel.CanInstall);
    }

    [Fact]
    public async Task InstallAsyncSucceedsAndRefreshesList()
    {
        string apkPath = CreateFakeApk("com.example.demoapp.apk");

        var stubAdb = new StubFeatureAdbClient
        {
            ShellHandler = cmd =>
            {
                if (cmd.StartsWith("pm install", StringComparison.Ordinal))
                {
                    return "Success\n";
                }

                if (cmd == ApplicationService.PackageListCommand)
                {
                    return "package:com.example.demoapp\n";
                }

                if (cmd.Contains("dumpsys package com.example.demoapp", StringComparison.Ordinal))
                {
                    return "versionCode=1 versionName=1.0 primaryCpuAbi=null";
                }

                return string.Empty;
            }
        };

        var service = new ApplicationService(() => stubAdb);
        var viewModel = new ApplicationWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms())
        {
            SelectedApkPath = apkPath,
            PackageNameInput = TestPackageName,
        };

        await viewModel.InstallAsync();

        Assert.False(viewModel.HasOperationFailure);
        Assert.True(viewModel.HasOperationResult);
        Assert.Contains("成功", viewModel.LastOperationResultText, StringComparison.Ordinal);
        Assert.Single(viewModel.Applications);
        Assert.Equal("无原生库", viewModel.Applications[0].PrimaryCpuAbiText);
    }

    [Fact]
    public async Task InstallAsyncReportsPmFailureDetails()
    {
        string apkPath = CreateFakeApk("com.example.demoapp.apk");

        var stubAdb = new StubFeatureAdbClient
        {
            ShellHandler = cmd =>
            {
                if (cmd.StartsWith("pm install", StringComparison.Ordinal))
                {
                    return "Failure [INSTALL_FAILED_OLDER_SDK: Failed to install]\n";
                }

                return string.Empty;
            }
        };

        var service = new ApplicationService(() => stubAdb);
        var viewModel = new ApplicationWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms())
        {
            SelectedApkPath = apkPath,
            PackageNameInput = TestPackageName,
        };

        await viewModel.InstallAsync();

        Assert.True(viewModel.HasOperationFailure);
        Assert.Contains("INSTALL_FAILED_OLDER_SDK", viewModel.LastOperationResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchAsyncInvokesServiceAndReportsOutcome()
    {
        var stubAdb = new StubFeatureAdbClient
        {
            ShellHandler = cmd =>
            {
                if (cmd.StartsWith("monkey", StringComparison.Ordinal))
                {
                    return "Events injected: 1\n";
                }

                return string.Empty;
            }
        };

        var service = new ApplicationService(() => stubAdb);
        var viewModel = new ApplicationWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        var item = new ApplicationItemViewModel(new ApplicationSpec
        {
            PackageName = TestPackageName,
            InstanceRef = InstanceId,
            InstallState = ApplicationInstallState.Installed,
        });

        viewModel.SelectedItem = item;
        Assert.True(viewModel.CanLaunch);

        await viewModel.LaunchAsync();

        Assert.False(viewModel.HasOperationFailure);
        Assert.Contains("拉起应用成功", viewModel.LastOperationResultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UninstallAsyncWithConfirmationRemovesApplication()
    {
        var stubAdb = new StubFeatureAdbClient
        {
            ShellHandler = cmd =>
            {
                if (cmd.StartsWith("pm uninstall", StringComparison.Ordinal))
                {
                    return "Success\n";
                }

                if (cmd == ApplicationService.PackageListCommand)
                {
                    return string.Empty;
                }

                return string.Empty;
            }
        };

        var service = new ApplicationService(() => stubAdb);
        bool confirmed = false;
        var viewModel = new ApplicationWindowViewModel(
            service,
            InstanceId,
            InstanceName,
            AdbPort,
            Terms(),
            confirmUninstallHandler: msg =>
            {
                confirmed = true;
                return true;
            });

        var item = new ApplicationItemViewModel(new ApplicationSpec
        {
            PackageName = TestPackageName,
            InstanceRef = InstanceId,
            InstallState = ApplicationInstallState.Installed,
        });

        viewModel.Applications.Add(item);
        viewModel.SelectedItem = item;

        await viewModel.UninstallAsync();

        Assert.True(confirmed);
        Assert.False(viewModel.HasOperationFailure);
        Assert.Contains("卸载应用成功", viewModel.LastOperationResultText, StringComparison.Ordinal);
        Assert.Empty(viewModel.Applications);
    }

    [Fact]
    public async Task UninstallAsyncWithoutConfirmationAborts()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new ApplicationService(() => stubAdb);

        var viewModel = new ApplicationWindowViewModel(
            service,
            InstanceId,
            InstanceName,
            AdbPort,
            Terms(),
            confirmUninstallHandler: _ => false);

        var item = new ApplicationItemViewModel(new ApplicationSpec
        {
            PackageName = TestPackageName,
            InstanceRef = InstanceId,
            InstallState = ApplicationInstallState.Installed,
        });

        viewModel.Applications.Add(item);
        viewModel.SelectedItem = item;

        await viewModel.UninstallAsync();

        Assert.Empty(stubAdb.ExecutedShellCommands);
        Assert.Single(viewModel.Applications);
    }

    [Fact]
    public void OpeningSameInstanceTwiceKeepsOneWindow()
    {
        var host = new StubApplicationWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Open("win-a", "实例甲", null);

        Assert.Single(host.Created);
        Assert.Single(host.Activated);
        Assert.Equal("win-a", host.OpenInstanceIds.Single());
    }

    [Fact]
    public void OpeningDifferentInstancesKeepsSeparateWindows()
    {
        var host = new StubApplicationWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Open("win-b", "实例乙", null);

        Assert.Equal(2, host.Created.Count);
        Assert.Equal(2, host.OpenInstanceIds.Count);
    }

    [Fact]
    public void CloseRemovesTheWindowAndAllowsReopening()
    {
        var host = new StubApplicationWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Close("win-a");
        host.Open("win-a", "实例甲", null);

        Assert.Equal(2, host.Created.Count);
        Assert.Single(host.Closed);
        Assert.Equal("win-a", host.OpenInstanceIds.Single());
    }
}
