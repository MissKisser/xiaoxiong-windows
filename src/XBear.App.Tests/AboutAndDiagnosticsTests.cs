using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using XBear.App.Presentation;
using XBear.App.Services;
using XBear.App.ViewModels;
using XBear.App.Views;
using XBear.Core.Abstractions;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 关于窗口与诊断包构建标识测试。
/// 验证版本信息与构建标识严格来自共享版本契约，禁止硬编码。
/// </summary>
public class AboutAndDiagnosticsTests
{
    private readonly SpecLoader _loader = XBeeSpec.TestSpec();

    /// <summary>
    /// 关于窗口的版本属性必须直接来自 version.json 契约文件，与产品/规格双版本序列一致。
    /// </summary>
    [Fact]
    public void AboutWindowBindsVersionFromContract()
    {
        VersionDocument versionDoc = _loader.LoadVersion();
        var diagnostics = new DiagnosticsExporter(_loader);
        var terms = new TerminologyCatalog(_loader.LoadTerminology());

        var viewModel = new MainViewModel(
            new StubInstanceRepository(),
            null,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            diagnostics,
            terms);

        Assert.Equal(versionDoc.Product.Version, viewModel.ProductVersion);
        Assert.Equal(versionDoc.Spec.Version, viewModel.SpecVersion);
        Assert.Equal(diagnostics.BuildId, viewModel.BuildId);
        Assert.StartsWith($"{versionDoc.Product.Version}+", viewModel.BuildId);
    }

    /// <summary>
    /// 版本属性不得硬编码，传入任意契约版本时均能动态反映。
    /// </summary>
    [Fact]
    public void AboutWindowDoesNotHardcodeVersions()
    {
        var customVersion = new VersionDocument
        {
            Product = new ProductIdentity
            {
                Id = "custom-emu",
                NameZh = "自定义模拟器",
                NameEn = "Custom Emulator",
                Version = "9.8.7"
            },
            Spec = new SpecRelease
            {
                Version = "6.5.4",
                Description = "测试规格"
            },
            Build = new BuildIdPolicy
            {
                IdFormat = "{productVersion}+{commitShort}",
                Fields = new Dictionary<string, string>
                {
                    { "productVersion", "产品版本" },
                    { "commitShort", "提交哈希" }
                }
            }
        };

        var diagnostics = new DiagnosticsExporter(_loader);
        var terms = new TerminologyCatalog(_loader.LoadTerminology());

        var viewModel = new MainViewModel(
            new StubInstanceRepository(),
            null,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            diagnostics,
            terms,
            customVersion);

        Assert.Equal("9.8.7", viewModel.ProductVersion);
        Assert.Equal("6.5.4", viewModel.SpecVersion);
        Assert.StartsWith("9.8.7+", viewModel.BuildId);
    }

    /// <summary>
    /// 关于窗口实例可在 STA 线程成功构建并绑定主视图模型。
    /// </summary>
    [Fact]
    public void AboutWindowCanBeConstructedOnStaThread()
    {
        var diagnostics = new DiagnosticsExporter(_loader);
        var terms = new TerminologyCatalog(_loader.LoadTerminology());

        var viewModel = new MainViewModel(
            new StubInstanceRepository(),
            null,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            diagnostics,
            terms);

        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new AboutWindow(viewModel);
                Assert.Same(viewModel, window.DataContext);
                Assert.Equal("关于小熊模拟器", window.Title);
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadException);
    }

    /// <summary>
    /// 主视图模型暴露关于命令，支持通过回调接管窗口唤起。
    /// </summary>
    [Fact]
    public void MainViewModelExposesOpenAboutCommand()
    {
        var diagnostics = new DiagnosticsExporter(_loader);
        var terms = new TerminologyCatalog(_loader.LoadTerminology());

        var viewModel = new MainViewModel(
            new StubInstanceRepository(),
            null,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            diagnostics,
            terms);

        bool aboutOpened = false;
        viewModel.ShowAboutAction = () => { aboutOpened = true; };

        Assert.True(viewModel.OpenAboutCommand.CanExecute(null));
        viewModel.OpenAboutCommand.Execute(null);

        Assert.True(aboutOpened);
    }

    /// <summary>
    /// 提交短哈希解析支持完整哈希截短与回退本地占位值。
    /// </summary>
    [Theory]
    [InlineData("0.1.0+abcdef0123456789", "abcdef0")]
    [InlineData("0.1.0+1234567", "1234567")]
    [InlineData("0.1.0+short", "short")]
    [InlineData("0.1.0", "local")]
    [InlineData("0.1.0+", "local")]
    [InlineData(null, "local")]
    [InlineData("", "local")]
    public void ResolveCommitShortExtractsHashOrFallsBack(string? informationalVersion, string expected)
    {
        string result = DiagnosticsExporter.ResolveCommitShort(informationalVersion);
        Assert.Equal(expected, result);
    }

    /// <summary>
    /// 诊断包统一构建标识遵循 {productVersion}+{commitShort} 格式。
    /// </summary>
    [Fact]
    public void DiagnosticsExporterGeneratesUnifiedBuildId()
    {
        VersionDocument versionDoc = _loader.LoadVersion();

        var diagnosticsWithCustomCommit = new DiagnosticsExporter(_loader, "abc1234");
        Assert.Equal($"{versionDoc.Product.Version}+abc1234", diagnosticsWithCustomCommit.BuildId);

        var diagnosticsDefault = new DiagnosticsExporter(_loader);
        Assert.StartsWith($"{versionDoc.Product.Version}+", diagnosticsDefault.BuildId);
        Assert.DoesNotContain("{", diagnosticsDefault.BuildId, StringComparison.Ordinal);
        Assert.DoesNotContain("}", diagnosticsDefault.BuildId, StringComparison.Ordinal);
    }

    /// <summary>
    /// 诊断包导出的 system/info.txt 必须包含统一构建标识行。
    /// </summary>
    [Fact]
    public async Task DiagnosticsExportIncludesBuildIdInSystemInfo()
    {
        var temp = new TempRoot();
        try
        {
            string outputDir = temp.New("diagnostics-out");
            var diagnostics = new DiagnosticsExporter(_loader, "testcommit");
            var repository = new StubInstanceRepository();

            string zipPath = await diagnostics.ExportAsync(
                outputDir,
                null,
                repository,
                null);

            Assert.True(File.Exists(zipPath));

            using var stream = File.OpenRead(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            ZipArchiveEntry? buildTxt = archive.GetEntry("system/build.txt");
            Assert.NotNull(buildTxt);

            using var reader = new StreamReader(buildTxt.Open());
            string content = await reader.ReadToEndAsync();

            Assert.Contains($"build: {diagnostics.BuildId}", content, StringComparison.Ordinal);
            Assert.Contains("testcommit", content, StringComparison.Ordinal);
        }
        finally
        {
            temp.Cleanup();
        }
    }
}
