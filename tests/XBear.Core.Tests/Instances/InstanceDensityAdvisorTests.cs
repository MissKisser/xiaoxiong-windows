using XBear.Core.Instances;
using XBear.Core.Spec;

namespace XBear.Core.Tests.Instances;

/// <summary>
/// 多开密度顾问测试，覆盖档位映射、建议上限、超限提示及延迟劣化上限比较。
/// </summary>
public sealed class InstanceDensityAdvisorTests
{
    private static PerformanceBaseline CreateTestBaseline()
    {
        return new PerformanceBaseline
        {
            Version = "0.1.0",
            Status = BaselineStatus.TargetsOnly,
            Density = new BaselineDensity
            {
                Note = "多实例密度必须绑定宿主内存配置",
                Profiles = new List<BaselineDensityProfile>
                {
                    new()
                    {
                        Tier = "入门",
                        HostMemoryGB = 16,
                        TargetInstances = new BaselineThreshold { Op = ">=", Value = 2 },
                    },
                    new()
                    {
                        Tier = "主流",
                        HostMemoryGB = 32,
                        TargetInstances = new BaselineThreshold { Op = ">=", Value = 3 },
                    },
                },
                Regression = new DensityRegression
                {
                    Note = "并行实例数达标的附加条件是延迟劣化不超过 30%。",
                    MaxDegradationPercent = 30,
                },
            },
        };
    }

    [Theory]
    [InlineData(8.0, null, 1)]
    [InlineData(15.8, "入门", 2)] // 考虑容差，约 16GB 识别为入门档
    [InlineData(16.0, "入门", 2)]
    [InlineData(24.0, "入门", 2)]
    [InlineData(31.8, "主流", 3)] // 考虑容差，约 32GB 识别为主流档
    [InlineData(32.0, "主流", 3)]
    [InlineData(64.0, "主流", 3)]
    public void 宿主内存映射_计算对应档位与建议并行上限(
        double hostMemoryGB,
        string? expectedTier,
        int expectedLimit)
    {
        var baseline = CreateTestBaseline();
        (string? tier, int limit) = InstanceDensityAdvisor.CalculateTierAndLimit(hostMemoryGB, baseline);

        Assert.Equal(expectedTier, tier);
        Assert.Equal(expectedLimit, limit);
    }

    [Fact]
    public void 实例数量达到上限时_标记超限且生成提示文案()
    {
        var detector = new FakeHostMemoryDetector(16.0);
        var advisor = new InstanceDensityAdvisor(detector, CreateTestBaseline);

        // 入门档建议上限为 2
        DensityAdvice adviceZero = advisor.Evaluate(0);
        Assert.False(adviceZero.IsExceeded);
        Assert.Equal(2, adviceZero.RecommendedLimit);

        DensityAdvice adviceOne = advisor.Evaluate(1);
        Assert.False(adviceOne.IsExceeded);

        DensityAdvice adviceTwo = advisor.Evaluate(2);
        Assert.True(adviceTwo.IsExceeded);
        Assert.Contains("建议并行实例上限为 2 个", adviceTwo.Message);
        Assert.Contains("超出建议上限", adviceTwo.Message);
    }

    [Fact]
    public void 提示文案_严格遵守术语且不包含禁用近义词()
    {
        var detector = new FakeHostMemoryDetector(16.0);
        var advisor = new InstanceDensityAdvisor(detector, CreateTestBaseline);

        DensityAdvice advice = advisor.Evaluate(3);
        string message = advice.Message;

        // 必须使用“实例”
        Assert.Contains("实例", message);

        // 绝对禁止使用禁用词汇
        string[] forbiddenWords = ["模拟器", "虚拟机", "分身", "开机", "关机", "系统镜像"];
        foreach (string forbidden in forbiddenWords)
        {
            Assert.DoesNotContain(forbidden, message);
        }
    }

    [Theory]
    [InlineData(100.0, 120.0, 20.0, true)]
    [InlineData(100.0, 130.0, 30.0, true)]
    [InlineData(100.0, 130.1, 30.1, false)]
    [InlineData(100.0, 150.0, 50.0, false)]
    [InlineData(100.0, 80.0, -20.0, true)]
    public void 延迟劣化评估_准确判断是否超出容许上限(
        double baselineLatency,
        double measuredLatency,
        double expectedDegradation,
        bool expectedAcceptable)
    {
        var advisor = new InstanceDensityAdvisor(new FakeHostMemoryDetector(), CreateTestBaseline);

        RegressionEvaluation evaluation = advisor.EvaluateRegression(baselineLatency, measuredLatency);

        Assert.Equal(expectedDegradation, evaluation.DegradationPercent, 1);
        Assert.Equal(30.0, evaluation.MaxDegradationPercent);
        Assert.Equal(expectedAcceptable, evaluation.IsAcceptable);
    }

    [Fact]
    public void 基线延迟小于等于零时_抛出异常()
    {
        var advisor = new InstanceDensityAdvisor(new FakeHostMemoryDetector(), CreateTestBaseline);

        Assert.Throws<ArgumentOutOfRangeException>(() => advisor.EvaluateRegression(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => advisor.EvaluateRegression(-10, 100));
    }
}
