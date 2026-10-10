using System.Text.Json;
using System.Text.Json.Nodes;
using XBear.Core.Spec;
using XBear.Core.Tests.Spec;

namespace XBear.Core.Tests.Compat;

/// <summary>
/// ImageSpec 的 boot 字段契约测试：
/// 解析、往返、Schema 校验，以及无 boot 的旧镜像向前兼容。
/// </summary>
public class BootContractTests
{
    /// <summary>
    /// 镜像样例中的 boot 字段必须能被强类型模型完整解析。
    /// </summary>
    [Fact]
    public void ImageFixtureBootIsParsedIntoModel()
    {
        var json = SpecTestHost.ImageJson();
        var spec = SpecTestHost.Loader.ParseImage(json);

        Assert.NotNull(spec.Boot);
        Assert.Equal("kernel", spec.Boot!.Kernel);
        Assert.Equal("initrd.img", spec.Boot.Initrd);
        Assert.Equal("root=/dev/ram0 quiet nomodeset", spec.Boot.KernelAppend);
    }

    /// <summary>
    /// boot 往返解析必须保持三个字段不变。
    /// </summary>
    [Fact]
    public void BootSurvivesRoundTrip()
    {
        var json = SpecTestHost.ImageJson();
        var first = SpecTestHost.Loader.ParseImage(json);
        var reserialized = JsonSerializer.Serialize(first, SpecLoader.SerializerOptions);
        var second = SpecTestHost.Loader.ParseImage(reserialized);

        Assert.NotNull(second.Boot);
        Assert.Equal(first.Boot!.Kernel, second.Boot.Kernel);
        Assert.Equal(first.Boot.Initrd, second.Boot.Initrd);
        Assert.Equal(first.Boot.KernelAppend, second.Boot.KernelAppend);
    }

    /// <summary>
    /// 镜像样例的 boot 字段经过往返后仍然通过 Schema 校验。
    /// </summary>
    [Fact]
    public void BootRoundTripStillPassesSchema()
    {
        var json = SpecTestHost.ImageJson();
        var spec = SpecTestHost.Loader.ParseImage(json);
        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        var result = SpecTestHost.Validator.ValidateImage(reserialized);

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    /// <summary>
    /// 无 boot 字段的旧镜像必须解析为 null，且序列化时不补出该键。
    /// </summary>
    [Fact]
    public void ImageWithoutBootParsesToNullAndIsNotWrittenBack()
    {
        var node = JsonNode.Parse(SpecTestHost.ImageJson())!.AsObject();
        node.Remove("boot");

        var spec = SpecTestHost.Loader.ParseImage(node.ToJsonString());

        Assert.Null(spec.Boot);

        var reserialized = JsonSerializer.Serialize(spec, SpecLoader.SerializerOptions);

        // 只判顶层键是否存在，不用整段文本的子串搜索代替：
        // 镜像证据等自由文本可能合法地含有 boot 一词，子串搜索会把它们误判成键被写回。
        var roundTripped = JsonNode.Parse(reserialized)!.AsObject();
        Assert.False(roundTripped.ContainsKey("boot"));
    }

    /// <summary>
    /// 无 boot 字段的镜像同样通过 Schema 校验（boot 为可选字段）。
    /// </summary>
    [Fact]
    public void ImageWithoutBootPassesSchema()
    {
        var node = JsonNode.Parse(SpecTestHost.ImageJson())!.AsObject();
        node.Remove("boot");

        var result = SpecTestHost.Validator.ValidateImage(node.ToJsonString());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    /// <summary>
    /// boot 字段三子键均为合法字符串，通过 Schema 校验。
    /// </summary>
    [Fact]
    public void BootWithAllFieldsPassesSchema()
    {
        var node = JsonNode.Parse(SpecTestHost.ImageJson())!.AsObject();
        node["boot"] = new JsonObject
        {
            ["kernel"] = "vmlinuz",
            ["initrd"] = "initrd.img",
            ["kernelAppend"] = "root=/dev/ram0 quiet"
        };

        var result = SpecTestHost.Validator.ValidateImage(node.ToJsonString());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    /// <summary>
    /// boot.kernel 为空字符串时通过 Schema（文件名引用允空，提取时才判有效）。
    /// </summary>
    [Fact]
    public void BootWithEmptyKernelPassesSchema()
    {
        var node = JsonNode.Parse(SpecTestHost.ImageJson())!.AsObject();
        node["boot"] = new JsonObject
        {
            ["kernel"] = "",
            ["initrd"] = "initrd.img",
            ["kernelAppend"] = "root=/dev/ram0 quiet"
        };

        var result = SpecTestHost.Validator.ValidateImage(node.ToJsonString());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    /// <summary>
    /// boot 对象缺少 kernelAppend 时仍然通过 Schema（kernelAppend 为可选推荐骨架）。
    /// </summary>
    [Fact]
    public void BootWithoutKernelAppendPassesSchema()
    {
        var node = JsonNode.Parse(SpecTestHost.ImageJson())!.AsObject();
        node["boot"] = new JsonObject
        {
            ["kernel"] = "kernel",
            ["initrd"] = "initrd.img"
        };

        var result = SpecTestHost.Validator.ValidateImage(node.ToJsonString());

        Assert.True(result.IsValid, result.DescribeErrors());
    }

    /// <summary>
    /// boot 对象传入额外字段时被 additionalProperties: false 拦截。
    /// </summary>
    [Fact]
    public void BootWithExtraFieldFailsSchema()
    {
        var node = JsonNode.Parse(SpecTestHost.ImageJson())!.AsObject();
        node["boot"] = new JsonObject
        {
            ["kernel"] = "kernel",
            ["initrd"] = "initrd.img",
            ["kernelAppend"] = "root=/dev/ram0",
            ["extraField"] = "must-fail"
        };

        var result = SpecTestHost.Validator.ValidateImage(node.ToJsonString());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Path.Contains("boot"));
    }
}
