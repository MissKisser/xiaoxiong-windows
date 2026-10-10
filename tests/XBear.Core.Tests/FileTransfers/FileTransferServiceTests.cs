using System.Text;
using System.Text.Json;
using XBear.Core.Adb;
using XBear.Core.Diagnostics;
using XBear.Core.FileTransfers;
using XBear.Core.Spec;
using XBear.Core.Tests.Adb;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.FileTransfers;

/// <summary>
/// 文件传输服务与任务记录的单元与集成测试。
/// 全部基于假 adbd 并在本地临时目录下执行，不依赖真实 Android 实例。
/// </summary>
public sealed class FileTransferServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "xbear-ft-test-" + Guid.NewGuid().ToString("N"));

    public FileTransferServiceTests()
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

    [Fact]
    public async Task 双向传输_HostToInstance_传输完成且字节一致()
    {
        await using var server = new FakeAdbdServer();
        var service = new FileTransferService(() => new AdbClient());
        var target = new FileTransferTarget("win-main-01", server.Port);

        byte[] payload = Encoding.UTF8.GetBytes("host to instance test content 1234567890");
        string localSource = CreateLocalFile("source.bin", payload);
        const string remoteTarget = "/sdcard/Download/dest.bin";

        FileTransferSpec spec = await service.TransferAsync(
            target,
            localSource,
            remoteTarget,
            FileTransferDirections.HostToInstance,
            overwrite: false);

        Assert.Equal(FileTransferState.Completed, spec.State);
        Assert.Null(spec.FailureReason);
        Assert.NotNull(spec.FinishedAt);
        Assert.NotNull(spec.StartedAt);
        Assert.Equal(payload.Length, spec.Progress?.BytesTransferred);
        Assert.Equal(payload.Length, spec.Progress?.TotalBytes);
        Assert.Equal(payload, server.GetFile(remoteTarget));

        // Schema 校验
        string json = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);
        var validation = SpecTestHost.Validator.ValidateFileTransfer(json);
        Assert.True(validation.IsValid, validation.DescribeErrors());
    }

    [Fact]
    public async Task 双向传输_InstanceToHost_传输完成且字节一致()
    {
        await using var server = new FakeAdbdServer();
        var service = new FileTransferService(() => new AdbClient());
        var target = new FileTransferTarget("win-main-01", server.Port);

        byte[] payload = Encoding.UTF8.GetBytes("instance to host test content 9876543210");
        const string remoteSource = "/sdcard/Download/guest_source.bin";
        server.SetFile(remoteSource, payload);

        string localTarget = Path.Combine(_tempDir, "received.bin");

        FileTransferSpec spec = await service.TransferAsync(
            target,
            remoteSource,
            localTarget,
            FileTransferDirections.InstanceToHost,
            overwrite: false);

        Assert.Equal(FileTransferState.Completed, spec.State);
        Assert.Null(spec.FailureReason);
        Assert.NotNull(spec.FinishedAt);
        Assert.True(File.Exists(localTarget));
        Assert.Equal(payload, await File.ReadAllBytesAsync(localTarget));
        Assert.Equal(payload.Length, spec.Progress?.BytesTransferred);
        Assert.Equal(payload.Length, spec.Progress?.TotalBytes);

        // Schema 校验
        string json = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);
        var validation = SpecTestHost.Validator.ValidateFileTransfer(json);
        Assert.True(validation.IsValid, validation.DescribeErrors());
    }

    [Fact]
    public async Task 双向传输_HostToInstance_完成后触发guest_sync刷盘()
    {
        await using var server = new FakeAdbdServer();
        var shellCommands = new List<string>();
        server.ShellHandler = cmd =>
        {
            shellCommands.Add(cmd);
            return new ShellResponse(string.Empty, 0);
        };

        var service = new FileTransferService(() => new AdbClient());
        var target = new FileTransferTarget("win-main-01", server.Port);

        string localSource = CreateLocalFile("sync_test.txt", "flush test content");
        const string remoteTarget = "/sdcard/Download/sync_test.txt";

        await service.TransferAsync(
            target,
            localSource,
            remoteTarget,
            FileTransferDirections.HostToInstance,
            overwrite: true,
            flush: true);

        Assert.Contains(FileTransferService.GuestFlushCommand, shellCommands);
    }

    [Fact]
    public async Task 冲突策略_HostToInstance_目标存在且overwrite为false时被拒()
    {
        await using var server = new FakeAdbdServer();
        var service = new FileTransferService(() => new AdbClient());
        var target = new FileTransferTarget("win-main-01", server.Port);

        const string remoteTarget = "/sdcard/Download/existing.txt";
        server.SetFile(remoteTarget, Encoding.UTF8.GetBytes("old content"));

        string localSource = CreateLocalFile("new.txt", "new content");

        XBearException ex = await Assert.ThrowsAsync<XBearException>(() =>
            service.TransferAsync(
                target,
                localSource,
                remoteTarget,
                FileTransferDirections.HostToInstance,
                overwrite: false));

        Assert.Equal(ErrorCategory.Storage, ex.Category);
        Assert.Contains("已拒绝覆盖", ex.Message);
        Assert.Equal("old content", Encoding.UTF8.GetString(server.GetFile(remoteTarget)!));

        // 验证任务状态为失败终态
        FileTransferTask? task = service.ListTasks().LastOrDefault();
        Assert.NotNull(task);
        Assert.Equal(FileTransferState.Failed, task.State);
        Assert.NotNull(task.FailureReason);
        Assert.NotNull(task.FinishedAt);
    }

    [Fact]
    public async Task 冲突策略_HostToInstance_目标存在且overwrite为true时覆盖成功()
    {
        await using var server = new FakeAdbdServer();
        var service = new FileTransferService(() => new AdbClient());
        var target = new FileTransferTarget("win-main-01", server.Port);

        const string remoteTarget = "/sdcard/Download/overwrite_me.txt";
        server.SetFile(remoteTarget, Encoding.UTF8.GetBytes("old data"));

        string localSource = CreateLocalFile("new_data.txt", "overwritten data");

        FileTransferSpec spec = await service.TransferAsync(
            target,
            localSource,
            remoteTarget,
            FileTransferDirections.HostToInstance,
            overwrite: true);

        Assert.Equal(FileTransferState.Completed, spec.State);
        Assert.Equal("overwritten data", Encoding.UTF8.GetString(server.GetFile(remoteTarget)!));
    }

    [Fact]
    public async Task 冲突策略_InstanceToHost_目标存在且overwrite为false时被拒()
    {
        await using var server = new FakeAdbdServer();
        var service = new FileTransferService(() => new AdbClient());
        var target = new FileTransferTarget("win-main-01", server.Port);

        const string remoteSource = "/sdcard/Download/remote.txt";
        server.SetFile(remoteSource, Encoding.UTF8.GetBytes("remote data"));

        string localTarget = CreateLocalFile("local_existing.txt", "local data");

        XBearException ex = await Assert.ThrowsAsync<XBearException>(() =>
            service.TransferAsync(
                target,
                remoteSource,
                localTarget,
                FileTransferDirections.InstanceToHost,
                overwrite: false));

        Assert.Equal(ErrorCategory.Storage, ex.Category);
        Assert.Contains("已拒绝覆盖", ex.Message);
        Assert.Equal("local data", File.ReadAllText(localTarget));

        FileTransferTask? task = service.ListTasks().LastOrDefault();
        Assert.NotNull(task);
        Assert.Equal(FileTransferState.Failed, task.State);
    }

    [Fact]
    public async Task 冲突策略_InstanceToHost_目标存在且overwrite为true时覆盖成功()
    {
        await using var server = new FakeAdbdServer();
        var service = new FileTransferService(() => new AdbClient());
        var target = new FileTransferTarget("win-main-01", server.Port);

        const string remoteSource = "/sdcard/Download/source.txt";
        server.SetFile(remoteSource, Encoding.UTF8.GetBytes("pulled new data"));

        string localTarget = CreateLocalFile("target.txt", "initial local content");

        FileTransferSpec spec = await service.TransferAsync(
            target,
            remoteSource,
            localTarget,
            FileTransferDirections.InstanceToHost,
            overwrite: true);

        Assert.Equal(FileTransferState.Completed, spec.State);
        Assert.Equal("pulled new data", File.ReadAllText(localTarget));
    }

    [Fact]
    public async Task 取消传输_中途取消进入Cancelled终态且清理半成品()
    {
        await using var server = new FakeAdbdServer();
        var service = new FileTransferService(() => new AdbClient());
        var target = new FileTransferTarget("win-main-01", server.Port);

        // 创建大文件触发分块流式传输
        var bigPayload = new byte[256 * 1024];
        new Random(42).NextBytes(bigPayload);
        const string remoteSource = "/sdcard/Download/big.bin";
        server.SetFile(remoteSource, bigPayload);

        string localTarget = Path.Combine(_tempDir, "partial.bin");

        using var cts = new CancellationTokenSource();
        service.ProgressChanged += (_, e) =>
        {
            if (e.BytesTransferred > 0)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TransferAsync(
                target,
                remoteSource,
                localTarget,
                FileTransferDirections.InstanceToHost,
                overwrite: true,
                flush: false,
                cancellationToken: cts.Token));

        FileTransferTask? task = service.ListTasks().LastOrDefault();
        Assert.NotNull(task);
        Assert.Equal(FileTransferState.Cancelled, task.State);
        Assert.Null(task.FailureReason);
        Assert.NotNull(task.FinishedAt);

        // 验证半成品文件已被清理
        Assert.False(File.Exists(localTarget));
    }

    [Fact]
    public async Task 进度上报_字节数单调不减()
    {
        await using var server = new FakeAdbdServer();
        var service = new FileTransferService(() => new AdbClient());
        var target = new FileTransferTarget("win-main-01", server.Port);

        var payload = new byte[180 * 1024];
        new Random(123).NextBytes(payload);
        string localSource = CreateLocalFile("multi_chunk.bin", payload);
        const string remoteTarget = "/sdcard/Download/multi_chunk.bin";

        var progressHistory = new List<long>();
        service.ProgressChanged += (_, e) =>
        {
            progressHistory.Add(e.BytesTransferred);
        };

        FileTransferSpec spec = await service.TransferAsync(
            target,
            localSource,
            remoteTarget,
            FileTransferDirections.HostToInstance,
            overwrite: true);

        Assert.Equal(FileTransferState.Completed, spec.State);
        Assert.NotEmpty(progressHistory);

        // 验证单调不减
        for (int i = 1; i < progressHistory.Count; i++)
        {
            Assert.True(progressHistory[i] >= progressHistory[i - 1], "进度字节数必须单调不减。");
        }

        Assert.Equal(payload.Length, progressHistory.Last());
    }

    [Fact]
    public async Task totalBytes语义_Host源可知_Guest源取不到时为null()
    {
        await using var server = new FakeAdbdServer();
        var service = new FileTransferService(() => new AdbClient());
        var target = new FileTransferTarget("win-main-01", server.Port);

        // 1. Host-to-Instance: 源尺寸天然可知
        string localFile = CreateLocalFile("known_size.txt", "12345");
        FileTransferSpec hostSpec = await service.TransferAsync(
            target,
            localFile,
            "/sdcard/Download/known.txt",
            FileTransferDirections.HostToInstance,
            overwrite: true);

        Assert.NotNull(hostSpec.Progress?.TotalBytes);
        Assert.Equal(5, hostSpec.Progress.TotalBytes);

        // 2. Instance-to-Host: 禁用 STAT 模拟取不到源文件大小
        server.DisableStat = true;
        server.SetFile("/sdcard/Download/unknown_size.txt", Encoding.UTF8.GetBytes("guest content"));
        string localDest = Path.Combine(_tempDir, "dest_unknown.txt");

        FileTransferSpec guestSpec = await service.TransferAsync(
            target,
            "/sdcard/Download/unknown_size.txt",
            localDest,
            FileTransferDirections.InstanceToHost,
            overwrite: true);

        Assert.Equal(FileTransferState.Completed, guestSpec.State);
        Assert.Null(guestSpec.Progress?.TotalBytes);
        Assert.Equal(File.ReadAllBytes(localDest), Encoding.UTF8.GetBytes("guest content"));
    }

    [Theory]
    [InlineData("/sdcard/../data/local/tmp/hack.txt")]
    [InlineData("/data/local/tmp/outside.txt")]
    [InlineData("/sdcard/../../etc/passwd")]
    [InlineData("sdcard/no_root_slash.txt")]
    [InlineData("")]
    public void 路径域校验_Guest侧逃逸sdcard被拒(string invalidGuestPath)
    {
        XBearException ex = Assert.Throws<XBearException>(() =>
            FileTransferPathValidator.NormalizeAndValidateGuestPath(invalidGuestPath));

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void 路径域校验_Guest侧允许有效子目录与上级抵消()
    {
        string normalized = FileTransferPathValidator.NormalizeAndValidateGuestPath("/sdcard/a/b/../c/file.txt");
        Assert.Equal("/sdcard/a/c/file.txt", normalized);
    }

    [Theory]
    [InlineData("relative/path/test.txt")]
    [InlineData("")]
    public void 路径域校验_Host侧相对路径被拒(string invalidHostPath)
    {
        XBearException ex = Assert.Throws<XBearException>(() =>
            FileTransferPathValidator.NormalizeAndValidateHostPath(invalidHostPath));

        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    [Fact]
    public void 状态机_非法状态迁移抛异常()
    {
        var task = new FileTransferTask(
            "ft-test-001",
            "win-main-01",
            FileTransferDirections.HostToInstance,
            Path.Combine(_tempDir, "a.txt"),
            "/sdcard/Download/a.txt",
            ConflictPolicy.Overwrite);

        Assert.Equal(FileTransferState.Queued, task.State);

        // Queued 状态不能直接 Complete
        XBearException ex1 = Assert.Throws<XBearException>(() => task.Complete());
        Assert.Equal(ErrorCategory.State, ex1.Category);

        // Queued 状态不能直接 ReportProgress
        XBearException ex2 = Assert.Throws<XBearException>(() => task.ReportProgress(100));
        Assert.Equal(ErrorCategory.State, ex2.Category);

        // 启动后流转到 Running
        task.Start(500, "a.txt");
        Assert.Equal(FileTransferState.Running, task.State);

        // Running 状态不能再次 Start
        XBearException ex3 = Assert.Throws<XBearException>(() => task.Start(500, "a.txt"));
        Assert.Equal(ErrorCategory.State, ex3.Category);

        // 完成
        task.Complete();
        Assert.Equal(FileTransferState.Completed, task.State);
        Assert.True(task.IsTerminal());

        // 终态不可再次流转到 Failed、Cancelled 或 Complete
        XBearException ex4 = Assert.Throws<XBearException>(() => task.Fail("fail again"));
        Assert.Equal(ErrorCategory.State, ex4.Category);

        XBearException ex5 = Assert.Throws<XBearException>(() => task.Cancel());
        Assert.Equal(ErrorCategory.State, ex5.Category);

        XBearException ex6 = Assert.Throws<XBearException>(() => task.Complete());
        Assert.Equal(ErrorCategory.State, ex6.Category);
    }

    [Fact]
    public async Task 任务管理_ListTasks可按实例过滤且Id不复用()
    {
        var service = new FileTransferService(() => new AdbClient());

        var req1 = new FileTransferRequest(
            FileTransferDirections.HostToInstance,
            Path.Combine(_tempDir, "x.txt"),
            "/sdcard/Download/x.txt",
            overwrite: true,
            taskId: "ft-inst-a-001");

        // 创建临时文件供校验通过
        File.WriteAllText(Path.Combine(_tempDir, "x.txt"), "x");

        // 任务标识不合法被拒
        var reqBadId = new FileTransferRequest(
            FileTransferDirections.HostToInstance,
            Path.Combine(_tempDir, "x.txt"),
            "/sdcard/Download/x.txt",
            overwrite: true,
            taskId: "INVALID_UPPERCASE");

        var target = new FileTransferTarget("inst-a", 15555);

        XBearException ex = await Assert.ThrowsAsync<XBearException>(async () =>
            await service.TransferAsync(target, reqBadId));
        Assert.Equal(ErrorCategory.Spec, ex.Category);
    }

    private string CreateLocalFile(string fileName, byte[] content)
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string CreateLocalFile(string fileName, string text) =>
        CreateLocalFile(fileName, Encoding.UTF8.GetBytes(text));
}
