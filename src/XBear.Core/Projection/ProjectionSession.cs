using System.Globalization;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Projection;

/// <summary>
/// 投屏会话领域模型。严格维护 pending → active → stopped 单向状态机，
/// 任何非法跃迁或逆向迁移一律抛出 <see cref="XBearException"/>（分类为 <see cref="ErrorCategory.State"/>）。
/// 会话内部包含符合 spec/schema/projection.schema.json 契约的规格数据，实测帧率只允许由真实观测写入。
/// </summary>
public sealed class ProjectionSession
{
    private const string DateTimeOffsetFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    private readonly object _gate = new();
    private readonly ProjectionSpec _spec;

    /// <summary>
    /// 创建投屏会话，初始状态为 pending。
    /// </summary>
    /// <param name="id">投屏会话全局唯一标识，必须符合契约命名约束。</param>
    /// <param name="instanceRef">被投屏实例标识。</param>
    /// <param name="width">初始画面宽度，单位像素。</param>
    /// <param name="height">初始画面高度，单位像素。</param>
    /// <param name="targetFps">目标帧率，单位 fps，为 null 时不声明目标帧率。</param>
    /// <param name="pointerDomain">指针坐标域，默认为绝对坐标域。</param>
    /// <param name="pointerMax">绝对坐标域上界，默认 virtio-tablet 的 32767。</param>
    /// <param name="keyboardLayout">按键编码表，默认 linux-evdev。</param>
    public ProjectionSession(
        string id,
        string instanceRef,
        int width,
        int height,
        int? targetFps = 30,
        ProjectionPointerDomain pointerDomain = ProjectionPointerDomain.Absolute,
        int pointerMax = 32767,
        string keyboardLayout = "linux-evdev")
    {
        ValidateIdentifier(id, nameof(id));
        ValidateIdentifier(instanceRef, nameof(instanceRef));
        ValidateDimension(width, nameof(width));
        ValidateDimension(height, nameof(height));

        _spec = new ProjectionSpec
        {
            SchemaVersion = "1.0.0",
            Id = id,
            InstanceRef = instanceRef,
            State = ProjectionState.Pending,
            StartedAt = null,
            EndedAt = null,
            Video = new ProjectionVideo
            {
                Width = width,
                Height = height,
                Fps = targetFps is > 0
                    ? new ProjectionFrameRate { Target = targetFps.Value, Measured = null }
                    : null,
            },
            Input = new ProjectionInput
            {
                Channel = ProjectionInputChannel.Native,
                Devices = [ProjectionInputDeviceKind.Pointer, ProjectionInputDeviceKind.Keyboard],
                Pointer = new ProjectionPointer
                {
                    Domain = pointerDomain,
                    Max = pointerDomain == ProjectionPointerDomain.Absolute ? pointerMax : null,
                },
                Keyboard = new ProjectionKeyboard
                {
                    Layout = keyboardLayout,
                },
            },
        };
    }

    /// <summary>投屏会话标识。</summary>
    public string Id
    {
        get
        {
            lock (_gate)
            {
                return _spec.Id;
            }
        }
    }

    /// <summary>被投屏实例标识。</summary>
    public string InstanceRef
    {
        get
        {
            lock (_gate)
            {
                return _spec.InstanceRef;
            }
        }
    }

    /// <summary>当前会话状态。</summary>
    public ProjectionState State
    {
        get
        {
            lock (_gate)
            {
                return _spec.State;
            }
        }
    }

    /// <summary>画面宽度，单位像素。</summary>
    public int Width
    {
        get
        {
            lock (_gate)
            {
                return _spec.Video.Width;
            }
        }
    }

    /// <summary>画面高度，单位像素。</summary>
    public int Height
    {
        get
        {
            lock (_gate)
            {
                return _spec.Video.Height;
            }
        }
    }

    /// <summary>目标帧率，单位 fps，未声明时为 null。</summary>
    public int? TargetFps
    {
        get
        {
            lock (_gate)
            {
                return _spec.Video.Fps?.Target;
            }
        }
    }

    /// <summary>实测帧率，单位 fps，未测量时为 null。</summary>
    public double? MeasuredFps
    {
        get
        {
            lock (_gate)
            {
                return _spec.Video.Fps?.Measured;
            }
        }
    }

    /// <summary>首次进入 active 的时刻，尚未激活时为 null。</summary>
    public string? StartedAt
    {
        get
        {
            lock (_gate)
            {
                return _spec.StartedAt;
            }
        }
    }

    /// <summary>进入 stopped 的时刻，尚未终止时为 null。</summary>
    public string? EndedAt
    {
        get
        {
            lock (_gate)
            {
                return _spec.EndedAt;
            }
        }
    }

    /// <summary>指针坐标域约定。</summary>
    public ProjectionPointer? PointerConfig
    {
        get
        {
            lock (_gate)
            {
                return _spec.Input?.Pointer;
            }
        }
    }

    /// <summary>按键映射约定。</summary>
    public ProjectionKeyboard? KeyboardConfig
    {
        get
        {
            lock (_gate)
            {
                return _spec.Input?.Keyboard;
            }
        }
    }

    /// <summary>
    /// 激活会话：将状态由 pending 推进为 active。
    /// 只有处于 pending 的会话允许激活，其他任何状态均拒绝。
    /// </summary>
    /// <param name="startedAt">激活发生的时间戳，为空时使用当前时间。</param>
    /// <exception cref="XBearException">状态非法时抛出，分类为 <see cref="ErrorCategory.State"/>。</exception>
    public void Activate(DateTimeOffset? startedAt = null)
    {
        lock (_gate)
        {
            if (_spec.State != ProjectionState.Pending)
            {
                throw new XBearException(
                    ErrorCategory.State,
                    $"无法将状态为 {_spec.State} 的投屏会话激活。",
                    "投屏会话只能沿 pending → active → stopped 单向推进，已终止的会话必须重新建立。");
            }

            DateTimeOffset timestamp = startedAt ?? DateTimeOffset.UtcNow;
            _spec.State = ProjectionState.Active;
            _spec.StartedAt = timestamp.ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 终止会话：将状态推进为 stopped。
    /// 允许从 pending（建立后未取帧直接取消）或 active 推进到 stopped；
    /// 已经处于 stopped 的会话重复终止时抛出异常。
    /// </summary>
    /// <param name="endedAt">终止发生的时间戳，为空时使用当前时间。</param>
    /// <exception cref="XBearException">已处于 stopped 状态时抛出，分类为 <see cref="ErrorCategory.State"/>。</exception>
    public void Stop(DateTimeOffset? endedAt = null)
    {
        lock (_gate)
        {
            if (_spec.State == ProjectionState.Stopped)
            {
                throw new XBearException(
                    ErrorCategory.State,
                    $"投屏会话 {_spec.Id} 已经处于 stopped 状态，无法重复终止。",
                    "已终止的会话已进入墓碑，重新投屏必须新建会话。");
            }

            DateTimeOffset timestamp = endedAt ?? DateTimeOffset.UtcNow;
            _spec.State = ProjectionState.Stopped;
            _spec.EndedAt = timestamp.ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 更新实测帧率。只允许来自真实观测，禁止由 target 推导或估算。
    /// </summary>
    /// <param name="measured">真实观测到的帧率值，未测得或样本不足时为 null。</param>
    public void UpdateMeasuredFps(double? measured)
    {
        lock (_gate)
        {
            if (_spec.Video.Fps is null)
            {
                if (measured is not null)
                {
                    _spec.Video.Fps = new ProjectionFrameRate
                    {
                        Target = 30,
                        Measured = measured,
                    };
                }

                return;
            }

            _spec.Video.Fps.Measured = measured;
        }
    }

    /// <summary>
    /// 更新呈现画面尺寸（分辨率变化通知到达时更新）。
    /// </summary>
    /// <param name="width">新的宽度，单位像素。</param>
    /// <param name="height">新的高度，单位像素。</param>
    public void UpdateResolution(int width, int height)
    {
        ValidateDimension(width, nameof(width));
        ValidateDimension(height, nameof(height));

        lock (_gate)
        {
            _spec.Video.Width = width;
            _spec.Video.Height = height;
        }
    }

    /// <summary>
    /// 导出当前投屏会话的只读规格模型快照，符合投屏契约。
    /// </summary>
    /// <returns>深拷贝的 <see cref="ProjectionSpec"/> 实例。</returns>
    public ProjectionSpec ToSpec()
    {
        lock (_gate)
        {
            return new ProjectionSpec
            {
                SchemaVersion = _spec.SchemaVersion,
                Id = _spec.Id,
                InstanceRef = _spec.InstanceRef,
                State = _spec.State,
                StartedAt = _spec.StartedAt,
                EndedAt = _spec.EndedAt,
                PlatformConfig = _spec.PlatformConfig,
                Video = new ProjectionVideo
                {
                    Width = _spec.Video.Width,
                    Height = _spec.Video.Height,
                    Fps = _spec.Video.Fps is { } fps
                        ? new ProjectionFrameRate
                        {
                            Target = fps.Target,
                            Measured = fps.Measured,
                        }
                        : null,
                },
                Input = _spec.Input is { } input
                    ? new ProjectionInput
                    {
                        Channel = input.Channel,
                        Devices = input.Devices is { } devs ? [..devs] : null,
                        Pointer = input.Pointer is { } ptr
                            ? new ProjectionPointer
                            {
                                Domain = ptr.Domain,
                                Max = ptr.Max,
                            }
                            : null,
                        Keyboard = input.Keyboard is { } kbd
                            ? new ProjectionKeyboard
                            {
                                Layout = kbd.Layout,
                            }
                            : null,
                    }
                    : null,
            };
        }
    }

    /// <summary>
    /// 生成符合契约的投屏会话标识。
    /// </summary>
    /// <param name="instanceRef">实例标识。</param>
    /// <returns>合规的投屏会话标识字符串。</returns>
    public static string GenerateSessionId(string instanceRef)
    {
        string safeInstance = string.IsNullOrWhiteSpace(instanceRef) ? "inst" : instanceRef.ToLowerInvariant();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string id = $"proj-{safeInstance}-{suffix}";
        if (id.Length > 64)
        {
            id = id[..64];
        }

        return id;
    }

    private static void ValidateIdentifier(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("标识不能为空。", paramName);
        }

        if (value.Length is < 3 or > 64)
        {
            throw new ArgumentException($"标识长度必须在 3 到 64 之间，实际为 {value.Length}。", paramName);
        }
    }

    private static void ValidateDimension(int value, string paramName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, "画面尺寸必须为正整数。");
        }
    }
}
