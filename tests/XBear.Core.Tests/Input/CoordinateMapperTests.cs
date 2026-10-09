using XBear.Core.Abstractions;
using XBear.Core.Diagnostics;
using XBear.Core.Input;

namespace XBear.Core.Tests.Input;

/// <summary>坐标映射测试，覆盖等比缩放、边界与越界夹取。</summary>
public sealed class CoordinateMapperTests
{
    private readonly CoordinateMapper _mapper = new();

    [Fact]
    public void 源尺寸与目标尺寸相同_坐标保持不变()
    {
        InputPoint mapped = _mapper.Map(new InputPoint(300, 400), new ScreenGeometry(720, 1280), new ScreenGeometry(720, 1280));

        Assert.Equal(300d, mapped.X);
        Assert.Equal(400d, mapped.Y);
    }

    [Fact]
    public void 源尺寸与目标尺寸不同_按轴独立缩放()
    {
        InputPoint mapped = _mapper.Map(new InputPoint(500, 250), new ScreenGeometry(1000, 500), new ScreenGeometry(500, 1000));

        Assert.Equal(250d, mapped.X);
        Assert.Equal(501d, mapped.Y);
    }

    [Fact]
    public void 源首末像素分别对应目标首末像素()
    {
        var source = new ScreenGeometry(800, 600);
        var target = new ScreenGeometry(1600, 1200);

        InputPoint topLeft = _mapper.Map(new InputPoint(0, 0), source, target);
        InputPoint bottomRight = _mapper.Map(new InputPoint(799, 599), source, target);

        Assert.Equal(0d, topLeft.X);
        Assert.Equal(0d, topLeft.Y);
        Assert.Equal(1599d, bottomRight.X);
        Assert.Equal(1199d, bottomRight.Y);
    }

    [Theory]
    [InlineData(-50, -20)]
    [InlineData(5000, 9000)]
    public void 坐标越界_夹取到目标范围边界(int x, int y)
    {
        InputPoint mapped = _mapper.Map(new InputPoint(x, y), new ScreenGeometry(1080, 1920), new ScreenGeometry(540, 960));

        Assert.InRange(mapped.X, 0, 539);
        Assert.InRange(mapped.Y, 0, 959);
        Assert.Equal(x < 0 ? 0d : 539d, mapped.X);
        Assert.Equal(y < 0 ? 0d : 959d, mapped.Y);
    }

    [Fact]
    public void 源尺寸未知_按不缩放处理但仍夹取()
    {
        InputPoint mapped = _mapper.Map(new InputPoint(700, -5), default, new ScreenGeometry(720, 1280));

        Assert.Equal(700d, mapped.X);
        Assert.Equal(0d, mapped.Y);
    }

    [Fact]
    public void 目标尺寸无效_按配置违规上抛()
    {
        XBearException error = Assert.Throws<XBearException>(() =>
            _mapper.Map(new InputPoint(10, 10), new ScreenGeometry(720, 1280), new ScreenGeometry(0, 1280)));

        Assert.Equal(ErrorCategory.Spec, error.Category);
    }

    [Fact]
    public void 目标为单像素_坐标夹取到唯一像素()
    {
        InputPoint mapped = _mapper.Map(new InputPoint(640, 480), new ScreenGeometry(1280, 720), new ScreenGeometry(1, 1));

        Assert.Equal(0d, mapped.X);
        Assert.Equal(0d, mapped.Y);
    }

    [Fact]
    public void 非整数坐标_按四舍五入取整()
    {
        InputPoint mapped = _mapper.Map(new InputPoint(50.4, 50.6), new ScreenGeometry(101, 101), new ScreenGeometry(101, 101));

        Assert.Equal(50d, mapped.X);
        Assert.Equal(51d, mapped.Y);
    }

    [Fact]
    public void 实现映射接口_默认映射与组件一致()
    {
        ICoordinateMapper mapper = new CoordinateMapper();

        InputPoint mapped = mapper.Map(new InputPoint(10, 10), new ScreenGeometry(100, 100), new ScreenGeometry(200, 200));

        Assert.Equal(20d, mapped.X);
        Assert.Equal(20d, mapped.Y);
    }
}
