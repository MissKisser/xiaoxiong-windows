using XBear.Core.Spec;

namespace XBear.App.Presentation;

/// <summary>
/// 界面文案术语来源。所有可见文案经由本类型取词，保证与 terminology.json 同源，
/// 界面代码里不自行拼写概念名，避免出现禁用近义词。
/// </summary>
public sealed class TerminologyCatalog
{
    /// <summary>实例的术语标识。</summary>
    public const string InstanceTermId = "instance";

    /// <summary>镜像的术语标识。</summary>
    public const string ImageTermId = "image";

    /// <summary>Root 权限的术语标识。</summary>
    public const string RootTermId = "root";

    /// <summary>暴露级别的术语标识。</summary>
    public const string ExposureTermId = "exposure";

    /// <summary>输入通道的术语标识。</summary>
    public const string InputChannelTermId = "input-channel";

    /// <summary>保真度的术语标识。</summary>
    public const string FidelityTermId = "fidelity";

    /// <summary>启动的术语标识。</summary>
    public const string StartTermId = "startup";

    /// <summary>停止的术语标识。</summary>
    public const string StopTermId = "stop";

    /// <summary>宿主环境错误的术语标识。</summary>
    public const string ErrorEnvironmentTermId = "error-environment";

    /// <summary>虚拟化实现错误的术语标识。</summary>
    public const string ErrorProviderTermId = "error-provider";

    /// <summary>实例内错误的术语标识。</summary>
    public const string ErrorGuestTermId = "error-guest";

    /// <summary>配置错误的术语标识。</summary>
    public const string ErrorUserTermId = "error-user";

    /// <summary>快照的术语标识。</summary>
    public const string SnapshotTermId = "snapshot";

    /// <summary>投屏的术语标识。</summary>
    public const string ProjectionTermId = "projection";

    /// <summary>文件传输的术语标识。</summary>
    public const string FileTransferTermId = "file-transfer";

    /// <summary>诊断包的术语标识。</summary>
    public const string DiagnosticsTermId = "diagnostics";

    /// <summary>分辨率的术语标识。</summary>
    public const string ResolutionTermId = "resolution";

    /// <summary>像素密度的术语标识。</summary>
    public const string DensityTermId = "density";

    /// <summary>屏幕方向的术语标识。</summary>
    public const string OrientationTermId = "orientation";

    /// <summary>投屏会话的术语标识。</summary>
    public const string ProjectionSessionTermId = "projection-session";

    /// <summary>帧率的术语标识。</summary>
    public const string FrameRateTermId = "frame-rate";

    /// <summary>指针坐标域的术语标识。</summary>
    public const string PointerCoordinateDomainTermId = "pointer-coordinate-domain";

    /// <summary>输入设备类型的术语标识。</summary>
    public const string InputDeviceKindTermId = "input-device-kind";

    /// <summary>模块的术语标识。</summary>
    public const string ModuleTermId = "module";

    /// <summary>安装模块的术语标识。</summary>
    public const string ModuleInstallTermId = "module-install";

    /// <summary>卸载模块的术语标识。</summary>
    public const string ModuleUninstallTermId = "module-uninstall";

    /// <summary>模块清单的术语标识。</summary>
    public const string ModuleManifestTermId = "module-manifest";

    /// <summary>阶段脚本的术语标识。</summary>
    public const string StageScriptTermId = "stage-script";

    /// <summary>系统叠加的术语标识。</summary>
    public const string SystemOverlayTermId = "system-overlay";

    /// <summary>禁用标记的术语标识。</summary>
    public const string DisableMarkerTermId = "disable-marker";

    /// <summary>传输任务的术语标识。</summary>
    public const string TransferTaskTermId = "transfer-task";

    /// <summary>冲突策略的术语标识。</summary>
    public const string ConflictPolicyTermId = "conflict-policy";

    /// <summary>应用的术语标识。</summary>
    public const string ApplicationTermId = "application";

    /// <summary>拉起应用的术语标识。</summary>
    public const string AppLaunchTermId = "app-launch";

    /// <summary>应用包的术语标识。</summary>
    public const string ApkFileTermId = "apk-file";

    /// <summary>安装状态的术语标识。</summary>
    public const string InstallStateTermId = "install-state";

    private readonly TerminologyDocument _document;

    /// <summary>
    /// 以术语表文档构造术语目录。
    /// </summary>
    /// <param name="document">共享术语表文档。</param>
    public TerminologyCatalog(TerminologyDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>实例的中文名。</summary>
    public string Instance => _document.Require(InstanceTermId).Zh;

    /// <summary>镜像的中文名。</summary>
    public string Image => _document.Require(ImageTermId).Zh;

    /// <summary>Root 权限的中文名。</summary>
    public string Root => _document.Require(RootTermId).Zh;

    /// <summary>暴露级别的中文名。</summary>
    public string Exposure => _document.Require(ExposureTermId).Zh;

    /// <summary>输入通道的中文名。</summary>
    public string InputChannel => _document.Require(InputChannelTermId).Zh;

    /// <summary>保真度的中文名。</summary>
    public string Fidelity => _document.Require(FidelityTermId).Zh;

    /// <summary>启动动作的中文名。</summary>
    public string Start => _document.Require(StartTermId).Zh;

    /// <summary>停止动作的中文名。</summary>
    public string Stop => _document.Require(StopTermId).Zh;

    /// <summary>宿主环境错误的中文名。</summary>
    public string ErrorEnvironment => _document.Require(ErrorEnvironmentTermId).Zh;

    /// <summary>虚拟化实现错误的中文名。</summary>
    public string ErrorProvider => _document.Require(ErrorProviderTermId).Zh;

    /// <summary>实例内错误的中文名。</summary>
    public string ErrorGuest => _document.Require(ErrorGuestTermId).Zh;

    /// <summary>配置错误的中文名。</summary>
    public string ErrorUser => _document.Require(ErrorUserTermId).Zh;

    /// <summary>快照的中文名。</summary>
    public string Snapshot => _document.Require(SnapshotTermId).Zh;

    /// <summary>投屏的中文名。</summary>
    public string Projection => _document.Require(ProjectionTermId).Zh;

    /// <summary>文件传输的中文名。</summary>
    public string FileTransfer => _document.Require(FileTransferTermId).Zh;

    /// <summary>诊断包的中文名。</summary>
    public string Diagnostics => _document.Require(DiagnosticsTermId).Zh;

    /// <summary>分辨率的中文名。</summary>
    public string Resolution => _document.Require(ResolutionTermId).Zh;

    /// <summary>像素密度的中文名。</summary>
    public string Density => _document.Require(DensityTermId).Zh;

    /// <summary>屏幕方向的中文名。</summary>
    public string Orientation => _document.Require(OrientationTermId).Zh;

    /// <summary>投屏会话的中文名。</summary>
    public string ProjectionSession => _document.Require(ProjectionSessionTermId).Zh;

    /// <summary>帧率的中文名。</summary>
    public string FrameRate => _document.Require(FrameRateTermId).Zh;

    /// <summary>指针坐标域的中文名。</summary>
    public string PointerCoordinateDomain => _document.Require(PointerCoordinateDomainTermId).Zh;

    /// <summary>输入设备类型的中文名。</summary>
    public string InputDeviceKind => _document.Require(InputDeviceKindTermId).Zh;

    /// <summary>模块的中文名。</summary>
    public string Module => _document.Require(ModuleTermId).Zh;

    /// <summary>安装模块的中文名。</summary>
    public string ModuleInstall => _document.Require(ModuleInstallTermId).Zh;

    /// <summary>卸载模块的中文名。</summary>
    public string ModuleUninstall => _document.Require(ModuleUninstallTermId).Zh;

    /// <summary>模块清单的中文名。</summary>
    public string ModuleManifest => _document.Require(ModuleManifestTermId).Zh;

    /// <summary>阶段脚本的中文名。</summary>
    public string StageScript => _document.Require(StageScriptTermId).Zh;

    /// <summary>系统叠加的中文名。</summary>
    public string SystemOverlay => _document.Require(SystemOverlayTermId).Zh;

    /// <summary>禁用标记的中文名。</summary>
    public string DisableMarker => _document.Require(DisableMarkerTermId).Zh;

    /// <summary>传输任务的中文名。</summary>
    public string TransferTask => _document.Require(TransferTaskTermId).Zh;

    /// <summary>冲突策略的中文名。</summary>
    public string ConflictPolicy => _document.Require(ConflictPolicyTermId).Zh;

    /// <summary>应用的中文名。</summary>
    public string Application => _document.Require(ApplicationTermId).Zh;

    /// <summary>拉起应用的中文名。</summary>
    public string AppLaunch => _document.Require(AppLaunchTermId).Zh;

    /// <summary>应用包的中文名。</summary>
    public string ApkFile => _document.Require(ApkFileTermId).Zh;

    /// <summary>安装状态的中文名。</summary>
    public string InstallState => _document.Require(InstallStateTermId).Zh;

    /// <summary>
    /// 目录已覆盖的术语标识，与术语表条目一一对应。
    /// </summary>
    public IReadOnlyList<string> TermIds { get; } = new[]
    {
        InstanceTermId,
        ImageTermId,
        RootTermId,
        ExposureTermId,
        InputChannelTermId,
        FidelityTermId,
        StartTermId,
        StopTermId,
        ErrorEnvironmentTermId,
        ErrorProviderTermId,
        ErrorGuestTermId,
        ErrorUserTermId,
        SnapshotTermId,
        ProjectionTermId,
        FileTransferTermId,
        DiagnosticsTermId,
        ResolutionTermId,
        DensityTermId,
        OrientationTermId,
        ProjectionSessionTermId,
        FrameRateTermId,
        PointerCoordinateDomainTermId,
        InputDeviceKindTermId,
        ModuleTermId,
        ModuleInstallTermId,
        ModuleUninstallTermId,
        ModuleManifestTermId,
        StageScriptTermId,
        SystemOverlayTermId,
        DisableMarkerTermId,
        TransferTaskTermId,
        ConflictPolicyTermId,
        ApplicationTermId,
        AppLaunchTermId,
        ApkFileTermId,
        InstallStateTermId,
    };

    /// <summary>
    /// 按标识取术语的中文名。
    /// </summary>
    /// <param name="id">术语标识。</param>
    /// <returns>界面显示用中文名。</returns>
    public string Zh(string id) => _document.Require(id).Zh;

    /// <summary>
    /// 按标识取术语的一句话说明，用于界面提示。
    /// </summary>
    /// <param name="id">术语标识。</param>
    /// <returns>术语说明文本。</returns>
    public string Meaning(string id) => _document.Require(id).Meaning;
}