using System.Text.Json;
using XBear.Core.Diagnostics;
using XBear.Core.Projection;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Projection;

/// <summary>
/// 投屏会话领域模型与生命周期测试，覆盖契约字段、状态机单向跃迁及实测帧率规则。
/// </summary>
public sealed class ProjectionSessionTests
{
    [Fact]
    public void 初始状态为Pending_未测量帧率保持Null()
    {
        var session = new ProjectionSession(
            id: "proj-inst-01-0001",
            instanceRef: "inst-01",
            width: 1280,
            height: 720,
            targetFps: 30);

        Assert.Equal("proj-inst-01-0001", session.Id);
        Assert.Equal("inst-01", session.InstanceRef);
        Assert.Equal(ProjectionState.Pending, session.State);
        Assert.Equal(1280, session.Width);
        Assert.Equal(720, session.Height);
        Assert.Equal(30, session.TargetFps);
        Assert.Null(session.MeasuredFps);
        Assert.Null(session.StartedAt);
        Assert.Null(session.EndedAt);

        ProjectionSpec spec = session.ToSpec();
        Assert.Equal("1.0.0", spec.SchemaVersion);
        Assert.Equal(ProjectionState.Pending, spec.State);
        Assert.NotNull(spec.Video.Fps);
        Assert.Equal(30, spec.Video.Fps.Target);
        Assert.Null(spec.Video.Fps.Measured);
        Assert.False(spec.IsPresenting());
        Assert.False(spec.IsTerminated());
    }

    [Fact]
    public void 单向状态机_正常推进Pending到Active到Stopped()
    {
        var session = new ProjectionSession("proj-test-01", "inst-01", 1080, 1920);

        var startedTime = DateTimeOffset.Parse("2026-10-10T10:00:00+08:00");
        session.Activate(startedTime);

        Assert.Equal(ProjectionState.Active, session.State);
        Assert.NotNull(session.StartedAt);
        Assert.Contains("+08:00", session.StartedAt);
        Assert.Null(session.EndedAt);
        Assert.True(session.ToSpec().IsPresenting());

        var endedTime = DateTimeOffset.Parse("2026-10-10T10:30:00+08:00");
        session.Stop(endedTime);

        Assert.Equal(ProjectionState.Stopped, session.State);
        Assert.NotNull(session.EndedAt);
        Assert.Contains("+08:00", session.EndedAt);
        Assert.True(session.ToSpec().IsTerminated());
    }

    [Fact]
    public void 非法状态机跃迁_重复激活抛出State异常()
    {
        var session = new ProjectionSession("proj-test-02", "inst-01", 1080, 1920);
        session.Activate();

        XBearException ex = Assert.Throws<XBearException>(() => session.Activate());
        Assert.Equal(ErrorCategory.State, ex.Category);
    }

    [Fact]
    public void 非法状态机跃迁_已停止会话重新激活抛出State异常()
    {
        var session = new ProjectionSession("proj-test-03", "inst-01", 1080, 1920);
        session.Activate();
        session.Stop();

        XBearException ex = Assert.Throws<XBearException>(() => session.Activate());
        Assert.Equal(ErrorCategory.State, ex.Category);
    }

    [Fact]
    public void 非法状态机跃迁_已停止会话重复停止抛出State异常()
    {
        var session = new ProjectionSession("proj-test-04", "inst-01", 1080, 1920);
        session.Stop();

        XBearException ex = Assert.Throws<XBearException>(() => session.Stop());
        Assert.Equal(ErrorCategory.State, ex.Category);
    }

    [Fact]
    public void 未经测量的帧率禁止由Target推导_只有真实观测才更新()
    {
        var session = new ProjectionSession("proj-test-05", "inst-01", 720, 1280, targetFps: 60);

        Assert.Equal(60, session.TargetFps);
        Assert.Null(session.MeasuredFps);

        session.UpdateMeasuredFps(58.7);
        Assert.Equal(58.7, session.MeasuredFps);

        // 复位后必须如实恢复为 null，不可退回 60 或 0
        session.UpdateMeasuredFps(null);
        Assert.Null(session.MeasuredFps);
    }

    [Fact]
    public void 分辨率更新_正确更新呈现画面宽高()
    {
        var session = new ProjectionSession("proj-test-06", "inst-01", 720, 1280);

        session.UpdateResolution(1280, 720);

        Assert.Equal(1280, session.Width);
        Assert.Equal(720, session.Height);
        ProjectionSpec spec = session.ToSpec();
        Assert.Equal(1280, spec.Video.Width);
        Assert.Equal(720, spec.Video.Height);
    }

    [Fact]
    public void 导出的Spec序列化满足契约要求()
    {
        var session = new ProjectionSession("proj-test-07", "inst-01", 1920, 1080, targetFps: 30);
        session.Activate(DateTimeOffset.Parse("2026-10-10T12:00:00+08:00"));
        session.UpdateMeasuredFps(29.8);

        ProjectionSpec spec = session.ToSpec();
        string json = JsonSerializer.Serialize(spec);

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.Equal("proj-test-07", root.GetProperty("id").GetString());
        Assert.Equal("inst-01", root.GetProperty("instanceRef").GetString());
        Assert.Equal("active", root.GetProperty("state").GetString());
        Assert.Equal(30, root.GetProperty("video").GetProperty("fps").GetProperty("target").GetInt32());
        Assert.Equal(29.8, root.GetProperty("video").GetProperty("fps").GetProperty("measured").GetDouble());
        Assert.Equal("native", root.GetProperty("input").GetProperty("channel").GetString());
        Assert.Equal("absolute", root.GetProperty("input").GetProperty("pointer").GetProperty("domain").GetString());
        Assert.Equal(32767, root.GetProperty("input").GetProperty("pointer").GetProperty("max").GetInt32());
    }
}
