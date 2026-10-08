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