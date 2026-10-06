using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Input;

namespace XBear.Core.Tests.Input;

/// <summary>截图头部解析测试，尺寸由此得出而非写死。</summary>
public sealed class PpmHeaderTests
{
    [Fact]
    public void 标准头部_解析出宽高()
    {
        Assert.True(PpmHeader.TryParse("P6\n1024 768\n255\n", out ScreenGeometry geometry));

        Assert.Equal(1024, geometry.Width);
        Assert.Equal(768, geometry.Height);
    }

    [Fact]
    public void 含注释的头部_跳过注释解析出宽高()
    {
        Assert.True(PpmHeader.TryParse("P6\n# 由小熊生成的截图\n800 600\n255\n", out ScreenGeometry geometry));

        Assert.Equal(new ScreenGeometry(800, 600), geometry);
    }

    [Fact]
    public void 头部被截断_按解析失败处理()
    {
        Assert.False(PpmHeader.TryParse("P6\n1024 ", out _));
    }

    [Fact]
    public void 非二进制像素图头部_按解析失败处理()
    {
        Assert.False(PpmHeader.TryParse("P3\n2 2\n255\n", out _));
    }

    [Fact]
    public void 宽高为零_按解析失败处理()
    {
        Assert.False(PpmHeader.TryParse("P6\n0 0\n255\n", out _));
    }

    [Fact]
    public void 最大值缺失_按解析失败处理()
    {
        Assert.False(PpmHeader.TryParse("P6\n800 600\n", out _));
    }

    [Fact]
    public void 空输入_按解析失败处理()
    {
        Assert.False(PpmHeader.TryParse(string.Empty, out _));
    }

    [Fact]
    public void 从文件读取_解析出宽高()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xbear-ppm-{Guid.NewGuid():N}.ppm");
        File.WriteAllText(path, "P6\n720 1280\n255\n");

        try
        {
            Assert.Equal(new ScreenGeometry(720, 1280), PpmHeader.TryReadFrom(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 文件不存在_按存储失败上抛()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xbear-ppm-{Guid.NewGuid():N}.ppm");

        XBearException error = Assert.Throws<XBearException>(() => PpmHeader.TryReadFrom(path));

        Assert.Equal(ErrorCategory.Storage, error.Category);
    }

    [Fact]
    public void 文件头部不是像素图_返回空而非抛错()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xbear-ppm-{Guid.NewGuid():N}.ppm");
        File.WriteAllText(path, "not an image");

        try
        {
            Assert.Null(PpmHeader.TryReadFrom(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
