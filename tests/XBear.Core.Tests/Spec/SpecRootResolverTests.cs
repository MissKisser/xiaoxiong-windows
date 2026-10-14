using XBear.Core.Diagnostics;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Spec;

/// <summary>
/// 共享规格根目录探测逻辑单元测试。
/// </summary>
public sealed class SpecRootResolverTests
{
    [Fact]
    public void IsSpecRootReturnsFalseWhenDirectoryIsInvalidOrIncomplete()
    {
        Assert.False(SpecRootResolver.IsSpecRoot(null));
        Assert.False(SpecRootResolver.IsSpecRoot(string.Empty));
        Assert.False(SpecRootResolver.IsSpecRoot("   "));

        string emptyDir = Path.Combine(Path.GetTempPath(), "xbear-empty-spec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            Assert.False(SpecRootResolver.IsSpecRoot(emptyDir));

            File.WriteAllText(Path.Combine(emptyDir, SpecLoader.VersionFileName), "{}");
            Assert.False(SpecRootResolver.IsSpecRoot(emptyDir));

            Directory.CreateDirectory(Path.Combine(emptyDir, SpecLoader.SchemaDirectoryName));
            Assert.True(SpecRootResolver.IsSpecRoot(emptyDir));
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveFindsValidSpecDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "xbear-install-layout-" + Guid.NewGuid().ToString("N"));
        string appDir = Path.Combine(root, "app");
        string specDir = Path.Combine(root, "spec");
        string schemaDir = Path.Combine(specDir, SpecLoader.SchemaDirectoryName);

        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(schemaDir);
        File.WriteAllText(Path.Combine(specDir, SpecLoader.VersionFileName), "{}");

        try
        {
            string resolved = SpecRootResolver.Resolve(appDir);
            Assert.Equal(Path.GetFullPath(specDir), Path.GetFullPath(resolved));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveThrowsWhenNotFound()
    {
        string isolatedApp = Path.Combine(Path.GetTempPath(), "xbear-no-spec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedApp);
        try
        {
            var ex = Assert.Throws<XBearException>(() =>
                SpecRootResolver.Resolve(isolatedApp, Array.Empty<string>()));

            Assert.Equal(ErrorCategory.Spec, ex.Category);
            Assert.False(string.IsNullOrWhiteSpace(ex.Remediation));
        }
        finally
        {
            Directory.Delete(isolatedApp, recursive: true);
        }
    }
}