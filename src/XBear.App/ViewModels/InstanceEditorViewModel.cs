using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XBear.App.Presentation;
using XBear.App.Services;
using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.App.ViewModels;

/// <summary>端口映射编辑行。</summary>
public sealed partial class PortForwardRowViewModel : ObservableObject
{
    [ObservableProperty]
    private int _hostPort;

    [ObservableProperty]
    private int _guestPort;

    [ObservableProperty]
    private string _protocol = "tcp";

    /// <summary>转换为配置模型。</summary>
    /// <returns>端口映射条目。</returns>
    public PortForward ToModel() => new()
    {
        HostPort = HostPort,
        GuestPort = GuestPort,
        Protocol = Protocol
    };
}

/// <summary>
/// 实例创建视图模型。承载基本信息、资源配额、网络端口映射与暴露级别选择，
/// 暴露级别默认回环，切到局域网或公网必须经确认，公网需显式勾选风险。
/// </summary>
public sealed partial class InstanceEditorViewModel : ObservableObject
{
    /// <summary>内存下限，与实例契约一致。</summary>
    public const int MemoryMinMB = 512;

    /// <summary>内存上限，与实例契约一致。</summary>
    public const int MemoryMaxMB = 16384;

    /// <summary>CPU 核数下限，与实例契约一致。</summary>
    public const int CpuMinCores = 1;

    /// <summary>CPU 核数上限，与实例契约一致。</summary>
    public const int CpuMaxCores = 64;

    /// <summary>磁盘下限，与实例契约一致。</summary>
    public const int DiskMinGB = 4;

    /// <summary>磁盘上限，与实例契约一致。</summary>
    public const int DiskMaxGB = 512;

    /// <summary>端口下限，与实例契约一致。</summary>
    public const int PortMin = 1;

    /// <summary>端口上限，与实例契约一致。</summary>
    public const int PortMax = 65535;

    private readonly IInstanceRepository _repository;
    private readonly AuditLog _audit;
    private readonly TerminologyCatalog _terms;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _imageRef = string.Empty;

    [ObservableProperty]
    private int _memoryMB = 4096;

    [ObservableProperty]
    private int _cpuCores = 4;

    [ObservableProperty]
    private int _diskGB = 32;

    [ObservableProperty]
    private string _fixedAddress = string.Empty;

    [ObservableProperty]
    private string _proxyHost = string.Empty;

    [ObservableProperty]
    private int _proxyPort;

    [ObservableProperty]
    private bool _isAcknowledged;

    [ObservableProperty]
    private string _validationMessage = string.Empty;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    /// <summary>
    /// 构造实例创建视图模型。
    /// </summary>
    /// <param name="repository">实例配置仓库。</param>
    /// <param name="images">可选镜像清单。</param>
    /// <param name="audit">审计日志。</param>
    /// <param name="terms">界面文案术语来源。</param>
    public InstanceEditorViewModel(
        IInstanceRepository repository,
        IReadOnlyDictionary<string, ImageSpec> images,
        AuditLog audit,
        TerminologyCatalog terms)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(terms);

        _repository = repository;
        _audit = audit;
        _terms = terms;

        // 暴露级别绝不默认对外，初始一律为回环。
        Exposure = new ExposureSelection(new[]
        {
            new ExposureLevelOption(ExposureSelection.Loopback, "仅本机（loopback）", "仅本机可连接，默认档位。"),
            new ExposureLevelOption(ExposureSelection.Lan, "局域网（lan）", "同网段设备可连接，请确认所在网络可信。"),
            new ExposureLevelOption(ExposureSelection.Public, "公网（public）", "任何可达网络的主机均可连接，务必确认已设置访问控制。")
        });

        foreach (KeyValuePair<string, ImageSpec> image in images)
        {
            Images.Add(image.Value.DisplayName);
        }

        PortForwards.Add(new PortForwardRowViewModel());
        _exposureLevel = Exposure.Current;
    }

    /// <summary>可选镜像显示名。</summary>
    public ObservableCollection<string> Images { get; } = new();

    /// <summary>端口映射编辑行。</summary>
    public ObservableCollection<PortForwardRowViewModel> PortForwards { get; } = new();

    /// <summary>暴露级别选择状态机。</summary>
    public ExposureSelection Exposure { get; }

    [ObservableProperty]
    private string _exposureLevel;

    /// <summary>端口协议候选项。</summary>
    public IReadOnlyList<string> Protocols { get; } = new[] { "tcp", "udp" };

    /// <summary>回环档是否选中。</summary>
    public bool IsLoopbackSelected
    {
        get => string.Equals(ExposureLevel, ExposureSelection.Loopback, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                RequestExposure(ExposureSelection.Loopback);
            }
        }
    }

    /// <summary>局域网档是否选中。</summary>
    public bool IsLanSelected
    {
        get => string.Equals(ExposureLevel, ExposureSelection.Lan, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                RequestExposure(ExposureSelection.Lan);
            }
        }
    }

    /// <summary>公网档是否选中。</summary>
    public bool IsPublicSelected
    {
        get => string.Equals(ExposureLevel, ExposureSelection.Public, StringComparison.Ordinal);
        set
        {
            if (value)
            {
                RequestExposure(ExposureSelection.Public);
            }
        }
    }

    /// <summary>暴露级别标题文案。</summary>
    public string ExposureTerm => _terms.Exposure;

    /// <summary>实例标题文案。</summary>
    public string InstanceTerm => _terms.Instance;

    /// <summary>镜像标题文案。</summary>
    public string ImageTerm => _terms.Image;

    /// <summary>弹窗标题。</summary>
    public string DialogTitle => $"创建{_terms.Instance}";

    /// <summary>风险确认勾选项文案。</summary>
    public string AcknowledgementText => "我已理解风险";

    /// <summary>确认按钮文案。</summary>
    public string ConfirmText => "确认";

    /// <summary>取消按钮文案。</summary>
    public string CancelText => "取消";

    /// <summary>是否为模态阻断式确认。</summary>
    public bool IsBlockingConfirmation => Exposure.IsConfirmationPending;

    /// <summary>待确认级别是否为公网。</summary>
    public bool IsPublicPending =>
        string.Equals(Exposure.Pending, ExposureSelection.Public, StringComparison.Ordinal);

    /// <summary>确认对话框中的风险说明。</summary>
    public string ConfirmationText =>
        string.IsNullOrEmpty(Exposure.PendingWarning)
            ? $"切换{ExposureTerm}需要确认。"
            : Exposure.PendingWarning;

    /// <summary>
    /// 请求切换暴露级别。
    /// </summary>
    /// <param name="level">目标暴露级别。</param>
    /// <returns>切换结果，需确认时返回 <see cref="ExposureChangeOutcome.RequiresAcknowledgement"/>。</returns>
    public ExposureChangeOutcome RequestExposure(string level)
    {
        ExposureChangeOutcome outcome = Exposure.Request(level);
        ExposureLevel = Exposure.Current;

        if (outcome is ExposureChangeOutcome.RequiresAcknowledgement)
        {
            // 切换 lan 与 public 属高影响操作，必须在日志中留痕。
            _audit.Record(
                DisplayName,
                "exposure-change-requested",
                $"from={Exposure.Current} to={level}");
        }

        return outcome;
    }

    /// <summary>
    /// 提交暴露级别变更。未勾选风险确认时不允许继续。
    /// </summary>
    /// <returns>生效返回 true，被阻断返回 false。</returns>
    public bool ConfirmExposure()
    {
        ExposureChangeOutcome outcome = Exposure.Confirm(IsAcknowledged);
        ExposureLevel = Exposure.Current;

        if (outcome is ExposureChangeOutcome.Blocked)
        {
            _audit.Record(DisplayName, "exposure-change-blocked", "未勾选风险确认，变更被阻断");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 取消待确认变更。
    /// </summary>
    public void CancelExposure()
    {
        Exposure.CancelPending();
        IsAcknowledged = false;
        ExposureLevel = Exposure.Current;
    }

    /// <summary>
    /// 校验资源取值是否落在契约区间内。
    /// </summary>
    /// <returns>全部合法返回 null，否则返回中文错误说明。</returns>
    public string? ValidateResources()
    {
        if (MemoryMB is < MemoryMinMB or > MemoryMaxMB)
        {
            return $"内存取值须在 {MemoryMinMB} 至 {MemoryMaxMB} MB 之间，当前为 {MemoryMB} MB。";
        }

        if (CpuCores is < CpuMinCores or > CpuMaxCores)
        {
            return $"CPU 核数须在 {CpuMinCores} 至 {CpuMaxCores} 之间，当前为 {CpuCores}。";
        }

        if (DiskGB is < DiskMinGB or > DiskMaxGB)
        {
            return $"磁盘取值须在 {DiskMinGB} 至 {DiskMaxGB} GB 之间，当前为 {DiskGB} GB。";
        }

        foreach (PortForwardRowViewModel row in PortForwards)
        {
            if (row.HostPort is < PortMin or > PortMax || row.GuestPort is < PortMin or > PortMax)
            {
                return $"端口取值须在 {PortMin} 至 {PortMax} 之间。";
            }
        }

        return null;
    }

    /// <summary>
    /// 创建实例并写入配置。校验不通过时不落盘。
    /// </summary>
    /// <returns>创建成功返回 true。</returns>
    [RelayCommand]
    public async Task<bool> CreateAsync()
    {
        ValidationMessage = ValidateResources() ?? string.Empty;
        if (!string.IsNullOrEmpty(ValidationMessage))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(DisplayName) || string.IsNullOrWhiteSpace(ImageRef))
        {
            ValidationMessage = $"{InstanceTerm}名称与{ImageTerm}均不可为空。";
            return false;
        }

        if (Exposure.IsConfirmationPending)
        {
            ValidationMessage = $"请先完成{ExposureTerm}确认。";
            return false;
        }

        try
        {
            InstanceSpec spec = BuildSpec();
            await _repository.SaveAsync(spec).ConfigureAwait(true);

            // 落盘后再留痕，保证审计记录与实际配置一致。
            if (ExposureSelection.RequiresRiskBanner(spec.Network?.Exposure))
            {
                _audit.Record(spec.Id, "instance-created", $"exposure={spec.Network?.Exposure}");
            }

            ResultMessage = $"已创建{InstanceTerm} {DisplayName}。";
            return true;
        }
        catch (Exception ex)
        {
            (Presentation.ErrorCategoryText text, string? remediation) = ErrorPresenter.Describe(ex);
            ValidationMessage = $"{text.Title}：{ex.Message}";
            if (!string.IsNullOrEmpty(remediation))
            {
                ValidationMessage += " " + remediation;
            }

            return false;
        }
    }

    private InstanceSpec BuildSpec()
    {
        List<PortForward> forwards = PortForwards
            .Where(r => r.HostPort > 0 && r.GuestPort > 0)
            .Select(r => r.ToModel())
            .ToList();

        return new InstanceSpec
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            DisplayName = DisplayName,
            Platform = "windows",
            ImageRef = ImageRef,
            Resources = new ResourceSpec
            {
                MemoryMB = MemoryMB,
                CpuCores = CpuCores,
                DiskGB = DiskGB
            },
            Network = new NetworkSpec
            {
                PortForwards = forwards,
                FixedAddress = string.IsNullOrWhiteSpace(FixedAddress) ? null : FixedAddress,
                Proxy = string.IsNullOrWhiteSpace(ProxyHost) || ProxyPort <= 0
                    ? null
                    : new ProxySpec { Host = ProxyHost, Port = ProxyPort },
                Exposure = Exposure.Current
            }
        };
    }

    partial void OnExposureLevelChanged(string value)
    {
        if (!string.Equals(value, Exposure.Current, StringComparison.Ordinal))
        {
            RequestExposure(value);
        }
    }
}