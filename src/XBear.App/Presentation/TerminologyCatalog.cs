using XBear.Core.Spec;

namespace XBear.App.Presentation;

/// <summary>
/// 界面文案术语来源。所有可见文案经由本类型取词，保证与 terminology.json 同源，
/// 界面代码里不自行拼写概念名，避免出现禁用近义词。
/// </summary>
public sealed class TerminologyCatalog
{
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
    public string Instance => _document.Require("instance").Zh;

    /// <summary>镜像的中文名。</summary>
    public string Image => _document.Require("image").Zh;

    /// <summary>Root 权限的中文名。</summary>
    public string Root => _document.Require("root").Zh;

    /// <summary>暴露级别的中文名。</summary>
    public string Exposure => _document.Require("exposure").Zh;

    /// <summary>输入通道的中文名。</summary>
    public string InputChannel => _document.Require("input-channel").Zh;

    /// <summary>保真度的中文名。</summary>
    public string Fidelity => _document.Require("fidelity").Zh;

    /// <summary>启动动作的中文名。</summary>
    public string Start => _document.Require("startup").Zh;

    /// <summary>停止动作的中文名。</summary>
    public string Stop => _document.Require("stop").Zh;

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