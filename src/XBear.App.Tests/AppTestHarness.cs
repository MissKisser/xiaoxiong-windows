using System;
using System.Collections.Generic;
using System.IO;
using XBear.App.Presentation;
using XBear.App.Services;
using XBear.App.ViewModels;
using XBear.Core.Abstractions;
using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.App.Tests;

/// <summary>
/// 测试用临时目录。每个测试类各持一个实例，避免并行执行的测试类之间互相删除目录。
/// </summary>
public sealed class TempRoot
{
    private readonly List<string> _created = new();

    /// <summary>
    /// 创建一个临时子目录。
    /// </summary>
    /// <param name="name">子目录名。</param>
    /// <returns>目录绝对路径。</returns>
    public string New(string name)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "xbear-apptests",
            Guid.NewGuid().ToString("N"),
            name);

        Directory.CreateDirectory(path);
        _created.Add(Path.GetDirectoryName(path)!);

        return path;
    }

    /// <summary>
    /// 创建一个含占位 base 镜像的目录，使启动流程的 overlay 准备能够通过。
    /// </summary>
    /// <param name="imageRef">镜像引用。</param>
    /// <returns>镜像根目录绝对路径。</returns>
    public string NewImagesRootWithBaseImage(string imageRef)
    {
        string root = New("images");
        File.WriteAllText(Path.Combine(root, imageRef + ".qcow2"), "stub base image");
        return root;
    }

    /// <summary>清理本测试类创建的全部临时目录。</summary>
    public void Cleanup()
    {
        foreach (string root in _created)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 清理失败不影响断言结果。
            }
        }

        _created.Clear();
    }
}

/// <summary>装配主视图模型所需的依赖，供视图模型测试复用。</summary>
/// <param name="ViewModel">被测主视图模型。</param>
/// <param name="Repository">实例配置仓库。</param>
public sealed record MainViewModelHarness(MainViewModel ViewModel, IInstanceRepository Repository)
{
    /// <summary>
    /// 以给定管理器与仓库装配主视图模型。
    /// </summary>
    /// <param name="manager">实例生命周期编排器。</param>
    /// <param name="repository">实例配置仓库。</param>
    /// <returns>视图模型与仓库的组合。</returns>
    public static MainViewModelHarness Create(InstanceManager manager, IInstanceRepository repository)
    {
        SpecLoader loader = XBeeSpec.TestSpec();
        var terms = new TerminologyCatalog(loader.LoadTerminology());
        var diagnostics = new DiagnosticsExporter(loader);

        var viewModel = new MainViewModel(
            repository,
            manager,
            new Dictionary<string, ImageSpec>(StringComparer.Ordinal),
            diagnostics,
            terms);

        return new MainViewModelHarness(viewModel, repository);
    }
}
