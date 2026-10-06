using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Instances;

/// <summary>实例生命周期编排测试，重点覆盖端口泄漏防护与状态机约束。</summary>
public sealed class InstanceManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "xbear-mgr-" + Guid.NewGuid().ToString("N"));

    private readonly InMemoryInstanceRepository _repository = new();
    private readonly FakePortAllocator _ports = new();
    private readonly FakeQemuArgBuilder _argBuilder = new();
    private readonly FakeQemuLauncher _launcher = new();
    private readonly FakeQcow2Manager _qcow2 = new();
    private readonly InstanceManager _manager;

    /// <summary>初始化被测对象并准备好一个可用的 base 镜像。</summary>
    public InstanceManagerTests()
    {
        string imagesRoot = Path.Combine(_root, "images");
        Directory.CreateDirectory(imagesRoot);
        File.WriteAllText(Path.Combine(imagesRoot, "bliss-os-17-x86_64.qcow2"), "base");

        _manager = new InstanceManager(
            _repository,
            _ports,
            _argBuilder,
            _launcher,
            _qcow2,
            imagesRoot,
            Path.Combine(_root, "instances"));

        _repository.Add(NewSpec());
    }

    /// <summary>清理测试产生的临时目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static InstanceSpec NewSpec(string id = "inst-01") => new()
    {
        Id = id,
        DisplayName = "测试实例",
        ImageRef = "bliss-os-17-x86_64",
    };

    [Fact]
    public async Task 正常启动_状态变为运行中且端口已占用()
    {
        await _manager.StartAsync("inst-01");

        Assert.Equal(InstanceState.Running, _manager.GetState("inst-01"));
        Assert.Contains("inst-01", _ports.Held);
        Assert.True(_manager.AllocatedPorts.ContainsKey("inst-01"));
        Assert.Single(_launcher.Started);
        Assert.NotEmpty(_argBuilder.LastArguments);
    }

    [Fact]
    public async Task 启动时_overlay不存在则创建()
    {
        await _manager.StartAsync("inst-01");

        string expected = Path.Combine(_root, "instances", "inst-01.qcow2");
        Assert.Contains(expected, _qcow2.CreatedOverlays);
        Assert.Equal(expected, _argBuilder.LastDiskPath);
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task 启动时_overlay已存在则不重复创建()
    {
        await _manager.StartAsync("inst-01");
        await _manager.StopAsync("inst-01");

        await _manager.StartAsync("inst-01");

        Assert.Single(_qcow2.CreatedOverlays);
    }

    [Fact]
    public async Task 启动成功_抛出状态变更通知供界面绑定()
    {
        var transitions = new List<InstanceStateChangedEventArgs>();
        _manager.StateChanged += (_, e) => transitions.Add(e);

        await _manager.StartAsync("inst-01");

        Assert.Collection(
            transitions,
            first =>
            {
                Assert.Equal(InstanceState.Stopped, first.OldState);
                Assert.Equal(InstanceState.Starting, first.NewState);
            },
            second =>
            {
                Assert.Equal(InstanceState.Starting, second.OldState);
                Assert.Equal(InstanceState.Running, second.NewState);
            });
    }

    [Fact]
    public async Task 运行态再次启动_抛出状态类异常()
    {
        await _manager.StartAsync("inst-01");

        XBearException ex = await Assert.ThrowsAsync<XBearException>(() => _manager.StartAsync("inst-01"));

        Assert.Equal(ErrorCategory.State, ex.Category);
        Assert.False(string.IsNullOrWhiteSpace(ex.Remediation));
    }

    [Fact]
    public async Task 实例配置不存在_抛出契约类异常且状态为故障()
    {
        XBearException ex = await Assert.ThrowsAsync<XBearException>(() => _manager.StartAsync("ghost"));

        Assert.Equal(ErrorCategory.Spec, ex.Category);
        Assert.Equal(InstanceState.Faulted, _manager.GetState("ghost"));
    }

    [Fact]
    public async Task 镜像不存在_抛出依赖类异常且端口未泄漏()
    {
        _repository.Add(new InstanceSpec { Id = "inst-noimg", ImageRef = "missing-image" });

        XBearException ex = await Assert.ThrowsAsync<XBearException>(() => _manager.StartAsync("inst-noimg"));

        Assert.Equal(ErrorCategory.Dependency, ex.Category);
        Assert.Equal(InstanceState.Faulted, _manager.GetState("inst-noimg"));
        Assert.Empty(_ports.Held);
        Assert.Empty(_manager.AllocatedPorts);
    }

    [Fact]
    public async Task overlay创建失败_抛出存储类异常且端口未泄漏()
    {
        _qcow2.CreateFailure = new IOException("磁盘已满");

        XBearException ex = await Assert.ThrowsAsync<XBearException>(() => _manager.StartAsync("inst-01"));

        Assert.Equal(ErrorCategory.Storage, ex.Category);
        Assert.Equal(InstanceState.Faulted, _manager.GetState("inst-01"));
        Assert.Empty(_ports.Held);
    }

    [Fact]
    public async Task 端口分配失败_抛出端口类异常且状态为故障()
    {
        _ports.AcquireFailure = new XBearException(ErrorCategory.Port, "无可用端口");

        XBearException ex = await Assert.ThrowsAsync<XBearException>(() => _manager.StartAsync("inst-01"));

        Assert.Equal(ErrorCategory.Port, ex.Category);
        Assert.Equal(InstanceState.Faulted, _manager.GetState("inst-01"));
        Assert.Empty(_manager.AllocatedPorts);
    }

    [Fact]
    public async Task 进程拉起失败_状态为故障且端口被释放()
    {
        _launcher.StartFailure = new XBearException(ErrorCategory.Process, "QEMU 退出码 1");

        XBearException ex = await Assert.ThrowsAsync<XBearException>(() => _manager.StartAsync("inst-01"));

        Assert.Equal(ErrorCategory.Process, ex.Category);
        Assert.Equal(InstanceState.Faulted, _manager.GetState("inst-01"));
        Assert.False(string.IsNullOrWhiteSpace(ex.Remediation));

        // 关键断言：端口绝不能泄漏。
        Assert.Empty(_ports.Held);
        Assert.Empty(_manager.AllocatedPorts);
        Assert.Equal(1, _ports.ReleaseCount);
    }

    [Fact]
    public async Task 进程拉起失败_修复后可重试启动()
    {
        _launcher.StartFailure = new XBearException(ErrorCategory.Process, "QEMU 退出码 1");
        await Assert.ThrowsAsync<XBearException>(() => _manager.StartAsync("inst-01"));

        _launcher.StartFailure = null;
        await _manager.StartAsync("inst-01");

        Assert.Equal(InstanceState.Running, _manager.GetState("inst-01"));
        Assert.Single(_ports.Held);
    }

    [Fact]
    public async Task 非预期异常_归类为内部错误并带处置建议()
    {
        _launcher.StartFailure = new InvalidOperationException("意外情况");

        XBearException ex = await Assert.ThrowsAsync<XBearException>(() => _manager.StartAsync("inst-01"));

        Assert.Equal(ErrorCategory.Internal, ex.Category);
        Assert.False(string.IsNullOrWhiteSpace(ex.Remediation));
        Assert.Empty(_ports.Held);
    }

    [Fact]
    public async Task 取消启动_抛出取消异常且端口被释放且状态不为故障()
    {
        _launcher.StartFailure = new OperationCanceledException("用户取消启动");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.StartAsync("inst-01"));

        // 关键断言：端口绝不能泄漏，且取消不得被呈现为内部故障。
        Assert.Empty(_ports.Held);
        Assert.Empty(_manager.AllocatedPorts);
        Assert.Equal(InstanceState.Stopped, _manager.GetState("inst-01"));
    }

    [Fact]
    public async Task 取消启动_可立即重新启动()
    {
        _launcher.StartFailure = new OperationCanceledException("用户取消启动");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.StartAsync("inst-01"));

        _launcher.StartFailure = null;
        await _manager.StartAsync("inst-01");

        Assert.Equal(InstanceState.Running, _manager.GetState("inst-01"));
        Assert.Single(_ports.Held);
    }

    [Fact]
    public async Task 取消停止_抛出取消异常且端口仍被释放()
    {
        await _manager.StartAsync("inst-01");
        _launcher.Started[0].StopFailure = new OperationCanceledException("用户取消停止");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _manager.StopAsync("inst-01"));

        Assert.Empty(_ports.Held);
        Assert.Empty(_manager.AllocatedPorts);
        Assert.Equal(InstanceState.Stopped, _manager.GetState("inst-01"));
    }

    [Fact]
    public async Task 正常停止_先终止进程再释放端口且状态为已停止()
    {
        await _manager.StartAsync("inst-01");
        FakeQemuProcessHandle handle = _launcher.Started[0];

        await _manager.StopAsync("inst-01");

        Assert.Equal(InstanceState.Stopped, _manager.GetState("inst-01"));
        Assert.Equal(1, handle.StopCount);
        Assert.Equal(1, handle.DisposeCount);
        Assert.Empty(_ports.Held);
        Assert.Empty(_manager.AllocatedPorts);
    }

    [Fact]
    public async Task 进程终止失败_端口仍被释放且状态为故障()
    {
        await _manager.StartAsync("inst-01");
        _launcher.Started[0].StopFailure = new XBearException(ErrorCategory.Process, "进程拒绝退出");

        XBearException ex = await Assert.ThrowsAsync<XBearException>(() => _manager.StopAsync("inst-01"));

        Assert.Equal(ErrorCategory.Process, ex.Category);
        Assert.Equal(InstanceState.Faulted, _manager.GetState("inst-01"));

        // 关键断言：进程终止失败也不得泄漏端口。
        Assert.Empty(_ports.Held);
        Assert.Empty(_manager.AllocatedPorts);
    }

    [Fact]
    public async Task 停止已停止的实例_直接返回不抛异常()
    {
        await _manager.StopAsync("inst-01");

        Assert.Equal(InstanceState.Stopped, _manager.GetState("inst-01"));
    }

    [Fact]
    public async Task 停止后可再次启动_端口被重新占用且进程重新拉起()
    {
        await _manager.StartAsync("inst-01");
        await _manager.StopAsync("inst-01");

        int releasesAfterStop = _ports.ReleaseCount;

        await _manager.StartAsync("inst-01");

        Assert.Equal(InstanceState.Running, _manager.GetState("inst-01"));
        Assert.Contains("inst-01", _ports.Held);
        Assert.Equal(2, _launcher.StartCount);

        // 端口在停止时归还，重新启动必然能再次拿到同一组空闲端口。
        Assert.Equal(releasesAfterStop, _ports.ReleaseCount);
        Assert.Equal(_launcher.LastPorts, _manager.AllocatedPorts["inst-01"]);
    }

    [Fact]
    public async Task 多个实例并行_端口互不冲突()
    {
        _repository.Add(NewSpec("inst-02"));
        _repository.Add(NewSpec("inst-03"));

        await Task.WhenAll(
            _manager.StartAsync("inst-01"),
            _manager.StartAsync("inst-02"),
            _manager.StartAsync("inst-03"));

        Assert.Equal(3, _ports.Held.Count);
        Assert.Equal(3, _manager.AllocatedPorts.Count);
        Assert.Equal(3, _manager.AllocatedPorts.Values.Select(p => p.Adb).Distinct().Count());
    }
}