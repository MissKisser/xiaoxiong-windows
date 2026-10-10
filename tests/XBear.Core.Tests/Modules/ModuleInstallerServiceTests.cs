using System.IO.Compression;
using System.Text;
using XBear.Core.Diagnostics;
using XBear.Core.Modules;
using XBear.Core.Tests.Adb;

namespace XBear.Core.Tests.Modules;

/// <summary>
/// 宿主侧模块安装服务的测试：全部针对内存内的假 adbd，
/// 不依赖真实 Android 实例即可验证校验、推送、刷盘与目录维护的完整链路。
/// </summary>
public sealed class ModuleInstallerServiceTests : IDisposable
{
    private const string ModulesRoot = ModuleInstallerService.ModulesRootPath;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "xbear-module-" + Guid.NewGuid().ToString("N"));

    private readonly ModuleGuestShell _guest = new();

    /// <summary>初始化测试目录。</summary>
    public ModuleInstallerServiceTests() => Directory.CreateDirectory(_root);

    /// <summary>清理测试产生的临时文件。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>新建一个指向假 adbd 的模块服务与目标实例。</summary>
    /// <param name="server">假 adbd。</param>
    /// <returns>被测服务与目标实例的组合。</returns>
    private static (ModuleInstallerService Service, ModuleTarget Target) CreateService(FakeAdbdServer server) =>
        (new ModuleInstallerService(), new ModuleTarget("inst-mod-01", server.Port));

    /// <summary>
    /// 新建一个假 adbd，并把 shell 处理接到 guest 文件系统替身上。
    /// </summary>
    /// <param name="guest">guest 文件系统替身。</param>
    /// <returns>假 adbd。</returns>
    private static FakeAdbdServer CreateServer(ModuleGuestShell guest) =>
        new() { ShellHandler = guest.Handle };

    /// <summary>
    /// 打包一个模块 ZIP。
    /// </summary>
    /// <param name="fileName">ZIP 文件名。</param>
    /// <param name="entries">归档内的条目路径与文本内容。</param>
    /// <returns>ZIP 文件的绝对路径。</returns>
    private string CreateZip(string fileName, params (string Path, string Content)[] entries)
    {
        string path = Path.Combine(_root, fileName);
        using FileStream file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        foreach ((string entryPath, string content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryPath);
            using Stream stream = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        return path;
    }

    [Fact]
    public async Task 安装_推送模块文件到实例模块目录并返回文件数()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = CreateZip(
            "demo.zip",
            ("module.prop", "id=demo\nname=演示模块\nversion=1\n"),
            ("system/app/demo.txt", "overlay 内容\n"),
            ("service.sh", "#!/system/bin/sh\n"));

        ModuleInstallResult result = await service.InstallAsync(target, zip);

        Assert.Equal("demo", result.ModuleId);
        Assert.Equal(ModulesRoot + "/demo", result.RemotePath);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(
            new[] { "module.prop", "system/app/demo.txt", "service.sh" },
            result.Files);

        // 开机模块加载器按目录名扫描，system/ 子树与阶段脚本必须落在同一模块目录下。
        Assert.NotNull(server.GetFile(ModulesRoot + "/demo/module.prop"));
        Assert.Equal(
            "overlay 内容\n",
            Encoding.UTF8.GetString(server.GetFile(ModulesRoot + "/demo/system/app/demo.txt")!));
        Assert.NotNull(server.GetFile(ModulesRoot + "/demo/service.sh"));
        Assert.Contains($"mkdir -p {ModulesRoot}/demo", _guest.Commands);
    }

    [Fact]
    public async Task 安装_推送完成后下发sync刷盘()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = CreateZip("demo.zip", ("system/app/demo.txt", "内容\n"));

        // 在 sync 发生的时刻观察推送是否已经完成，用来固定「先推送后刷盘」的先后关系。
        _guest.OnSync = () => server.GetFile(ModulesRoot + "/demo/system/app/demo.txt") is not null;
        await service.InstallAsync(target, zip);

        Assert.Contains("sync", _guest.Commands);
        Assert.True(_guest.SyncObservedAllPushed);
    }

    [Fact]
    public async Task 安装_为阶段脚本补上可执行位()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = CreateZip(
            "demo.zip",
            ("system/app/demo.txt", "内容\n"),
            ("post-fs-data.sh", "#!/system/bin/sh\n"),
            ("boot-completed.sh", "#!/system/bin/sh\n"));

        await service.InstallAsync(target, zip);

        Assert.Contains(
            $"chmod 0755 {ModulesRoot}/demo/post-fs-data.sh {ModulesRoot}/demo/boot-completed.sh",
            _guest.Commands);
    }

    [Fact]
    public async Task 安装_缺少moduleProp时以ZIP文件名作为模块标识()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = CreateZip("overlay-demo.zip", ("system/app/demo.txt", "内容\n"));

        ModuleInstallResult result = await service.InstallAsync(target, zip);

        Assert.Equal("overlay-demo", result.ModuleId);
        Assert.Equal(1, result.FileCount);
        Assert.NotNull(server.GetFile(ModulesRoot + "/overlay-demo/system/app/demo.txt"));
    }

    [Fact]
    public async Task 安装_整体套一层目录的模块包能识别根前缀()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = CreateZip(
            "wrapped.zip",
            ("demo/module.prop", "id=wrapped\n"),
            ("demo/system/app/demo.txt", "内容\n"));

        ModuleInstallResult result = await service.InstallAsync(target, zip);

        Assert.Equal("wrapped", result.ModuleId);
        Assert.NotNull(server.GetFile(ModulesRoot + "/wrapped/module.prop"));
        Assert.NotNull(server.GetFile(ModulesRoot + "/wrapped/system/app/demo.txt"));
    }

    [Fact]
    public async Task 安装_缺少moduleProp且无system子树时抛契约异常()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = CreateZip("readme-only.zip", ("readme.txt", "只有说明文件\n"));

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.InstallAsync(target, zip));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task 安装_模块标识含非法字符时抛契约异常()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = CreateZip("bad-id.zip", ("module.prop", "id=../../evil\n"), ("system/x", "x"));

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.InstallAsync(target, zip));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task 安装_归档损坏时抛存储类异常()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = Path.Combine(_root, "broken.zip");
        await File.WriteAllTextAsync(zip, "这根本不是 ZIP 归档");

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.InstallAsync(target, zip));

        Assert.Equal(ErrorCategory.Storage, error.Category);
        Assert.False(string.IsNullOrWhiteSpace(error.Remediation));
    }

    [Fact]
    public async Task 安装_模块包不存在时抛存储类异常()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.InstallAsync(target, Path.Combine(_root, "missing.zip")));

        Assert.Equal(ErrorCategory.Storage, error.Category);
    }

    [Fact]
    public async Task 安装_条目越界时拒绝安装()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = CreateZip("escape.zip", ("module.prop", "id=escape\n"), ("../outside.txt", "越界\n"));

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.InstallAsync(target, zip));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task 安装_跳过打包工具的元数据条目()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = CreateZip(
            "with-meta.zip",
            ("module.prop", "id=withmeta\n"),
            ("system/app/demo.txt", "内容\n"),
            ("__MACOSX/._module.prop", "垃圾数据"),
            ("system/.DS_Store", "垃圾数据"));

        ModuleInstallResult result = await service.InstallAsync(target, zip);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(new[] { "module.prop", "system/app/demo.txt" }, result.Files);
    }

    [Fact]
    public async Task 列表_返回模块根目录下的模块()
    {
        _guest.Mkdir($"{ModulesRoot}/alpha");
        _guest.Mkdir($"{ModulesRoot}/beta");
        _guest.Mkdir($"{ModulesRoot}/beta/system");

        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);

        IReadOnlyList<InstalledModule> modules = await service.ListAsync(target);

        Assert.Equal(new[] { "alpha", "beta" }, modules.Select(module => module.Id));
        Assert.Equal(ModulesRoot + "/alpha", modules[0].RemotePath);
    }

    [Fact]
    public async Task 列表_模块根目录不存在时返回空列表()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);

        IReadOnlyList<InstalledModule> modules = await service.ListAsync(target);

        Assert.Empty(modules);
        Assert.DoesNotContain($"ls -1 {ModulesRoot}", _guest.Commands);
    }

    [Fact]
    public async Task 列表_跳过隐藏目录与当前目录项()
    {
        _guest.Mkdir(ModulesRoot);
        _guest.Mkdir($"{ModulesRoot}/alpha");
        _guest.Mkdir($"{ModulesRoot}/.disabled");

        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);

        IReadOnlyList<InstalledModule> modules = await service.ListAsync(target);

        Assert.Equal(new[] { "alpha" }, modules.Select(module => module.Id));
    }

    [Fact]
    public async Task 卸载_删除模块目录并刷盘()
    {
        _guest.Mkdir(ModulesRoot);
        _guest.Mkdir($"{ModulesRoot}/demo");
        _guest.Mkdir($"{ModulesRoot}/demo/system");

        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);

        Assert.True(await service.UninstallAsync(target, "demo"));

        Assert.Contains($"rm -rf {ModulesRoot}/demo", _guest.Commands);
        Assert.Contains("sync", _guest.Commands);
        Assert.DoesNotContain($"{ModulesRoot}/demo", _guest.Directories);
        Assert.Empty(await service.ListAsync(target));
    }

    [Fact]
    public async Task 卸载_模块不存在时返回false且不改动实例()
    {
        _guest.Mkdir(ModulesRoot);

        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);

        Assert.False(await service.UninstallAsync(target, "ghost"));
        Assert.DoesNotContain(_guest.Commands, command => command.StartsWith("rm -rf", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("with space")]
    [InlineData("a/b")]
    [InlineData(" ")]
    public async Task 卸载_非法模块标识抛契约异常(string moduleId)
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);

        XBearException error = await Assert.ThrowsAsync<XBearException>(
            () => service.UninstallAsync(target, moduleId));

        Assert.Equal(ErrorCategory.Spec, error.Category);
        Assert.Empty(server.Files);
    }

    [Fact]
    public async Task 安装后列表可见该模块()
    {
        await using var server = CreateServer(_guest);
        (ModuleInstallerService service, ModuleTarget target) = CreateService(server);
        string zip = CreateZip("demo.zip", ("module.prop", "id=demo\n"), ("system/app/demo.txt", "内容\n"));

        await service.InstallAsync(target, zip);

        IReadOnlyList<InstalledModule> modules = await service.ListAsync(target);
        Assert.Equal(new[] { "demo" }, modules.Select(module => module.Id));
    }
}

/// <summary>
/// 实例内的目录与 shell 命令替身：只维护目录集合与命令序列，
/// 足以验证模块安装、列表与卸载在 guest 侧的实际动作。
/// </summary>
internal sealed class ModuleGuestShell
{
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly List<string> _commands = [];

    /// <summary>在 sync 命令发生时执行的观测回调。</summary>
    public Func<bool>? OnSync { get; set; }

    /// <summary>sync 时刻的观测结果。</summary>
    public bool SyncObservedAllPushed { get; private set; }

    /// <summary>已收到的命令序列，按下发顺序排列。</summary>
    public IReadOnlyList<string> Commands => _commands;

    /// <summary>当前已存在的目录集合。</summary>
    public IReadOnlyCollection<string> Directories => _directories;

    /// <summary>
    /// 预置一个已存在的目录，用于构造安装前的实例状态。
    /// </summary>
    /// <param name="path">实例内目录路径。</param>
    public void Mkdir(string path) => AddWithAncestors(path.TrimEnd('/'));

    /// <summary>
    /// 处理一条 guest shell 命令。
    /// </summary>
    /// <param name="command">客户端下发的命令。</param>
    /// <returns>带退出码的应答，退出码 0 表示成功。</returns>
    public ShellResponse Handle(string command)
    {
        lock (_commands)
        {
            _commands.Add(command);
        }

        if (command.StartsWith("mkdir -p ", StringComparison.Ordinal))
        {
            Mkdir(command["mkdir -p ".Length..].Trim());
            return new ShellResponse(string.Empty, 0);
        }

        if (command.StartsWith("test -d ", StringComparison.Ordinal))
        {
            string path = command["test -d ".Length..].Trim();
            lock (_directories)
            {
                return new ShellResponse(string.Empty, _directories.Contains(path) ? 0 : 1);
            }
        }

        if (command.StartsWith("ls -1 ", StringComparison.Ordinal))
        {
            string root = command["ls -1 ".Length..].Trim();
            lock (_directories)
            {
                string prefix = root + "/";
                string listing = string.Join(
                    '\n',
                    _directories
                        .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(path => path[prefix.Length..])
                        .Select(remainder => remainder.Contains('/', StringComparison.Ordinal)
                            ? remainder[..remainder.IndexOf('/', StringComparison.Ordinal)]
                            : remainder)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal));

                return new ShellResponse(listing + "\n", 0);
            }
        }

        if (command.StartsWith("rm -rf ", StringComparison.Ordinal))
        {
            string path = command["rm -rf ".Length..].Trim().TrimEnd('/');
            lock (_directories)
            {
                foreach (string existing in _directories.Where(p => p == path
                        || p.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
                {
                    _directories.Remove(existing);
                }
            }

            return new ShellResponse(string.Empty, 0);
        }

        if (command == "sync")
        {
            SyncObservedAllPushed = OnSync?.Invoke() ?? false;
            return new ShellResponse(string.Empty, 0);
        }

        if (command.StartsWith("chmod ", StringComparison.Ordinal))
        {
            return new ShellResponse(string.Empty, 0);
        }

        return new ShellResponse($"未识别的命令：{command}\n", 127);
    }

    /// <summary>
    /// 登记目录及其全部上级目录。
    /// </summary>
    /// <param name="path">实例内目录路径。</param>
    private void AddWithAncestors(string path)
    {
        lock (_directories)
        {
            string current = path;
            while (current.Length > 0)
            {
                _directories.Add(current);
                int slash = current.LastIndexOf('/');
                if (slash <= 0)
                {
                    break;
                }

                current = current[..slash];
            }
        }
    }
}