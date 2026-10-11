using XBear.Core.Qemu;

namespace XBear.Core.Tests.Qemu;

/// <summary>
/// 引导命令行清洗与收敛（SanitizeKernelAppend）单元测试。
/// 验证 A-13 提取 SRC 并消除安装态标记，以及按 Q-05 默认处置移除串口控制台。
/// </summary>
public sealed class KernelCommandLineSanitizeTests
{
    [Fact]
    public void 提取SRC并消除串口控制台()
    {
        const string input = "stack_depot_disable=on cgroup_disable=pressure root=/dev/ram0 SRC=android-2024-10-12 nomodeset console=tty0 console=ttyS0,115200";

        string output = KernelCommandLine.SanitizeKernelAppend(input, out string? src);

        Assert.Equal("android-2024-10-12", src);
        Assert.DoesNotContain("SRC=", output);
        Assert.DoesNotContain("console=ttyS0", output);
        Assert.Contains("root=/dev/ram0", output);
        Assert.Contains("nomodeset", output);
        Assert.Contains("console=tty0", output);
    }

    [Fact]
    public void 无SRC时返回null并保留普通参数()
    {
        const string input = "root=/dev/ram0 quiet nomodeset console=tty0";

        string output = KernelCommandLine.SanitizeKernelAppend(input, out string? src);

        Assert.Null(src);
        Assert.Equal("root=/dev/ram0 quiet nomodeset console=tty0", output);
    }

    [Fact]
    public void 空白输入返回空串()
    {
        string output1 = KernelCommandLine.SanitizeKernelAppend("", out string? src1);
        string output2 = KernelCommandLine.SanitizeKernelAppend("   ", out string? src2);

        Assert.Null(src1);
        Assert.Equal(string.Empty, output1);
        Assert.Null(src2);
        Assert.Equal(string.Empty, output2);
    }

    [Fact]
    public void 仅含SRC和串口时收敛为空串()
    {
        const string input = "SRC=android-2024-10-12 console=ttyS0,115200";

        string output = KernelCommandLine.SanitizeKernelAppend(input, out string? src);

        Assert.Equal("android-2024-10-12", src);
        Assert.Equal(string.Empty, output);
    }
}
