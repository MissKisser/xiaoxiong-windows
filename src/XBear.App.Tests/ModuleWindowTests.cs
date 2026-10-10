using System.IO;
using System.IO.Compression;
using System.Text;
using XBear.App.Presentation;
using XBear.App.ViewModels;
using XBear.Core.Modules;

namespace XBear.App.Tests;

/// <summary>
/// 模块管理窗口与视图模型的单元测试。
/// </summary>
public class ModuleWindowTests : IDisposable
{
    private const string InstanceId = "win-mod-01";
    private const string InstanceName = "测试模块实例";
    private const int AdbPort = 5555;
    private const string TestModuleId = "test-mod";

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "xbear-mod-view-test-" + Guid.NewGuid().ToString("N"));

    public ModuleWindowTests()
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

    private string CreateFakeModuleZip(string fileName, string moduleId)
    {
        string path = Path.Combine(_tempDir, fileName);
        using FileStream file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        ZipArchiveEntry propEntry = archive.CreateEntry("module.prop");
        using (Stream stream = propEntry.Open())
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            writer.WriteLine($"id={moduleId}");
            writer.WriteLine("name=Test Module");
            writer.WriteLine("version=1.0.0");
        }

        ZipArchiveEntry serviceEntry = archive.CreateEntry("service.sh");
        using (Stream stream = serviceEntry.Open())
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            writer.WriteLine("#!/system/bin/sh");
        }

        return path;
    }

    [Fact]
    public void WindowTitleShowsInstanceNameAndTerminology()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new ModuleInstallerService(() => stubAdb);
        var viewModel = new ModuleWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        Assert.Contains(InstanceName, viewModel.WindowTitle, StringComparison.Ordinal);
        Assert.Contains(Terms().Module, viewModel.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshCommandLoadsModules()
    {
        var stubAdb = new StubFeatureAdbClient
        {
            ShellHandler = cmd =>
            {
                if (cmd == $"test -d {ModuleInstallerService.ModulesRootPath}")
                {
                    return string.Empty;
                }

                if (cmd == $"ls -1 {ModuleInstallerService.ModulesRootPath}")
                {
                    return "mod-alpha\nmod-beta\n";
                }

                return string.Empty;
            }
        };

        var service = new ModuleInstallerService(() => stubAdb);
        var viewModel = new ModuleWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        await viewModel.RefreshAsync();

        Assert.Equal(2, viewModel.Modules.Count);
        Assert.Equal("mod-alpha", viewModel.Modules[0].Id);
        Assert.Equal("/data/adb/modules/mod-alpha", viewModel.Modules[0].RemotePath);
        Assert.Equal("已安装", viewModel.Modules[0].InstallStateText);
        Assert.Equal("mod-beta", viewModel.Modules[1].Id);
    }

    [Fact]
    public void BrowseZipSelectsFile()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new ModuleInstallerService(() => stubAdb);
        string zipPath = CreateFakeModuleZip("sample.zip", TestModuleId);

        var viewModel = new ModuleWindowViewModel(
            service,
            InstanceId,
            InstanceName,
            AdbPort,
            Terms(),
            zipFileDialogHandler: _ => zipPath);

        viewModel.BrowseZipCommand.Execute(null);

        Assert.Equal(zipPath, viewModel.SelectedZipPath);
        Assert.True(viewModel.CanInstall);
    }

    [Fact]
    public async Task InstallAsyncInstallsModuleAndRefreshesList()
    {
        string zipPath = CreateFakeModuleZip("mod.zip", TestModuleId);

        var stubAdb = new StubFeatureAdbClient
        {
            ShellHandler = cmd =>
            {
                if (cmd == $"test -d {ModuleInstallerService.ModulesRootPath}")
                {
                    return string.Empty;
                }

                if (cmd == $"ls -1 {ModuleInstallerService.ModulesRootPath}")
                {
                    return $"{TestModuleId}\n";
                }

                return string.Empty;
            }
        };

        var service = new ModuleInstallerService(() => stubAdb);
        var viewModel = new ModuleWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms())
        {
            SelectedZipPath = zipPath,
        };

        await viewModel.InstallAsync();

        Assert.False(viewModel.HasOperationFailure);
        Assert.True(viewModel.HasOperationResult);
        Assert.Contains(TestModuleId, viewModel.LastOperationResultText, StringComparison.Ordinal);
        Assert.Single(viewModel.Modules);
        Assert.Equal(TestModuleId, viewModel.Modules[0].Id);
    }

    [Fact]
    public async Task UninstallAsyncWithConfirmationRemovesModule()
    {
        var stubAdb = new StubFeatureAdbClient
        {
            ShellHandler = cmd =>
            {
                if (cmd.StartsWith("test -d /data/adb/modules/test-mod", StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                if (cmd.StartsWith("rm -rf /data/adb/modules/test-mod", StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                if (cmd == $"test -d {ModuleInstallerService.ModulesRootPath}")
                {
                    return string.Empty;
                }

                if (cmd == $"ls -1 {ModuleInstallerService.ModulesRootPath}")
                {
                    return string.Empty;
                }

                return string.Empty;
            }
        };

        var service = new ModuleInstallerService(() => stubAdb);
        bool confirmed = false;
        var viewModel = new ModuleWindowViewModel(
            service,
            InstanceId,
            InstanceName,
            AdbPort,
            Terms(),
            confirmUninstallHandler: _ =>
            {
                confirmed = true;
                return true;
            });

        var item = new ModuleItemViewModel(new InstalledModule(TestModuleId, $"/data/adb/modules/{TestModuleId}"));
        viewModel.Modules.Add(item);
        viewModel.SelectedItem = item;

        await viewModel.UninstallAsync();

        Assert.True(confirmed);
        Assert.False(viewModel.HasOperationFailure);
        Assert.Contains("卸载模块成功", viewModel.LastOperationResultText, StringComparison.Ordinal);
        Assert.Empty(viewModel.Modules);
    }

    [Fact]
    public async Task UninstallAsyncWithoutConfirmationAborts()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new ModuleInstallerService(() => stubAdb);

        var viewModel = new ModuleWindowViewModel(
            service,
            InstanceId,
            InstanceName,
            AdbPort,
            Terms(),
            confirmUninstallHandler: _ => false);

        var item = new ModuleItemViewModel(new InstalledModule(TestModuleId, $"/data/adb/modules/{TestModuleId}"));
        viewModel.Modules.Add(item);
        viewModel.SelectedItem = item;

        await viewModel.UninstallAsync();

        Assert.Empty(stubAdb.ExecutedShellCommands);
        Assert.Single(viewModel.Modules);
    }

    [Fact]
    public void OpeningSameInstanceTwiceKeepsOneWindow()
    {
        var host = new StubModuleWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Open("win-a", "实例甲", null);

        Assert.Single(host.Created);
        Assert.Single(host.Activated);
        Assert.Equal("win-a", host.OpenInstanceIds.Single());
    }

    [Fact]
    public void OpeningDifferentInstancesKeepsSeparateWindows()
    {
        var host = new StubModuleWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Open("win-b", "实例乙", null);

        Assert.Equal(2, host.Created.Count);
        Assert.Equal(2, host.OpenInstanceIds.Count);
    }

    [Fact]
    public void CloseRemovesTheWindowAndAllowsReopening()
    {
        var host = new StubModuleWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Close("win-a");
        host.Open("win-a", "实例甲", null);

        Assert.Equal(2, host.Created.Count);
        Assert.Single(host.Closed);
        Assert.Equal("win-a", host.OpenInstanceIds.Single());
    }
}
