using System.IO;
using XBear.App.Presentation;
using XBear.App.ViewModels;
using XBear.Core.Diagnostics;
using XBear.Core.FileTransfers;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 文件传输窗口与视图模型的单元测试。
/// </summary>
public class FileTransferWindowTests : IDisposable
{
    private const string InstanceId = "win-ft-01";
    private const string InstanceName = "测试传输实例";
    private const int AdbPort = 5555;

    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "xbear-ft-view-test-" + Guid.NewGuid().ToString("N"));

    public FileTransferWindowTests()
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

    [Fact]
    public void WindowTitleShowsInstanceNameAndTerminology()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new FileTransferService(() => stubAdb);
        var viewModel = new FileTransferWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        Assert.Contains(InstanceName, viewModel.WindowTitle, StringComparison.Ordinal);
        Assert.Contains(Terms().FileTransfer, viewModel.WindowTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectionDefaultsToHostToInstanceAndCanToggle()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new FileTransferService(() => stubAdb);
        var viewModel = new FileTransferWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        Assert.True(viewModel.IsHostToInstance);
        Assert.False(viewModel.IsInstanceToHost);
        Assert.Equal(FileTransferDirections.HostToInstance, viewModel.Direction);

        viewModel.IsInstanceToHost = true;

        Assert.False(viewModel.IsHostToInstance);
        Assert.True(viewModel.IsInstanceToHost);
        Assert.Equal(FileTransferDirections.InstanceToHost, viewModel.Direction);
    }

    [Fact]
    public void OverwriteDefaultsToSkipAndCanToggle()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new FileTransferService(() => stubAdb);
        var viewModel = new FileTransferWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        Assert.True(viewModel.IsSkip);
        Assert.False(viewModel.IsOverwrite);
        Assert.Equal(ConflictPolicy.Skip, viewModel.Overwrite);

        viewModel.IsOverwrite = true;

        Assert.False(viewModel.IsSkip);
        Assert.True(viewModel.IsOverwrite);
        Assert.Equal(ConflictPolicy.Overwrite, viewModel.Overwrite);
    }

    [Fact]
    public void BrowseHostFileSelectsOpenOrSaveBasedOnDirection()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new FileTransferService(() => stubAdb);

        FileTransferDialogRequest? lastRequest = null;
        var viewModel = new FileTransferWindowViewModel(
            service,
            InstanceId,
            InstanceName,
            AdbPort,
            Terms(),
            fileDialogHandler: req =>
            {
                lastRequest = req;
                return @"C:\local\test.txt";
            });

        viewModel.IsHostToInstance = true;
        viewModel.BrowseHostFileCommand.Execute(null);

        Assert.NotNull(lastRequest);
        Assert.False(lastRequest!.IsSave);
        Assert.Equal(@"C:\local\test.txt", viewModel.HostPath);

        viewModel.IsInstanceToHost = true;
        viewModel.GuestPath = "/sdcard/Documents/output.log";
        viewModel.BrowseHostFileCommand.Execute(null);

        Assert.True(lastRequest.IsSave);
        Assert.Equal("output.log", lastRequest.DefaultFileName);
    }

    [Fact]
    public async Task StartTransferWithInvalidGuestPathFailsValidation()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new FileTransferService(() => stubAdb);
        var viewModel = new FileTransferWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        string hostFile = Path.Combine(_tempDir, "source.bin");
        File.WriteAllText(hostFile, "content");

        viewModel.HostPath = hostFile;
        viewModel.GuestPath = "/sdcard/../../outside"; // 上级目录回退越界

        await viewModel.StartTransferAsync();

        Assert.True(viewModel.HasFailure);
        Assert.Contains("越界", viewModel.FailureText, StringComparison.Ordinal);
        Assert.Empty(viewModel.Tasks);
    }

    [Fact]
    public async Task StartTransferExecutesSuccessfullyAndUpdatesProgress()
    {
        string hostFile = Path.Combine(_tempDir, "file.txt");
        File.WriteAllText(hostFile, "hello xbear file transfer");

        var stubAdb = new StubFeatureAdbClient
        {
            StatHandler = _ => null,
            PushHandler = (src, dst, progress, ct) =>
            {
                progress?.Report(20);
            }
        };

        var service = new FileTransferService(() => stubAdb);
        var viewModel = new FileTransferWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        viewModel.HostPath = hostFile;
        viewModel.GuestPath = "/sdcard/Download/file.txt";

        await viewModel.StartTransferAsync();

        Assert.False(viewModel.HasFailure);
        Assert.Single(viewModel.Tasks);

        FileTransferTaskItemViewModel item = viewModel.Tasks[0];
        Assert.Equal("已完成", item.StateText);
        Assert.Equal(100, item.ProgressPercent);
        Assert.Equal(FileTransferDirections.HostToInstance, item.Direction);
    }

    [Fact]
    public async Task CancelTransferCancelsActiveOperation()
    {
        string hostFile = Path.Combine(_tempDir, "cancel.bin");
        File.WriteAllText(hostFile, "large data");

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stubAdb = new StubFeatureAdbClient
        {
            StatHandler = _ => null,
            PushHandler = (src, dst, progress, ct) =>
            {
                tcs.SetResult();
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
            }
        };

        var service = new FileTransferService(() => stubAdb);
        var viewModel = new FileTransferWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        viewModel.HostPath = hostFile;
        viewModel.GuestPath = "/sdcard/Download/cancel.bin";

        Task transferTask = viewModel.StartTransferAsync();
        await tcs.Task;

        viewModel.CancelTransferCommand.Execute(null);
        await transferTask;

        Assert.Single(viewModel.Tasks);
        Assert.Equal("已取消", viewModel.Tasks[0].StateText);
    }

    [Fact]
    public async Task CloseAsyncUnregistersEventsAndCancelsTokens()
    {
        var stubAdb = new StubFeatureAdbClient();
        var service = new FileTransferService(() => stubAdb);
        var viewModel = new FileTransferWindowViewModel(service, InstanceId, InstanceName, AdbPort, Terms());

        await viewModel.CloseAsync();

        Assert.False(viewModel.IsTransferring);
    }

    [Fact]
    public void OpeningSameInstanceTwiceKeepsOneWindow()
    {
        var host = new StubFileTransferWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Open("win-a", "实例甲", null);

        Assert.Single(host.Created);
        Assert.Single(host.Activated);
        Assert.Equal("win-a", host.OpenInstanceIds.Single());
    }

    [Fact]
    public void OpeningDifferentInstancesKeepsSeparateWindows()
    {
        var host = new StubFileTransferWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Open("win-b", "实例乙", null);

        Assert.Equal(2, host.Created.Count);
        Assert.Equal(2, host.OpenInstanceIds.Count);
    }

    [Fact]
    public void CloseRemovesTheWindowAndAllowsReopening()
    {
        var host = new StubFileTransferWindowHost();

        host.Open("win-a", "实例甲", null);
        host.Close("win-a");
        host.Open("win-a", "实例甲", null);

        Assert.Equal(2, host.Created.Count);
        Assert.Single(host.Closed);
        Assert.Equal("win-a", host.OpenInstanceIds.Single());
    }
}
