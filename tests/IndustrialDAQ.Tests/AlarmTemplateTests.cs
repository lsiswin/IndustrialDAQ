using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;

namespace IndustrialDAQ.Tests;

public sealed class AlarmTemplateTests
{
    [Fact]
    public void Template_creates_stable_resource_backed_rules()
    {
        var template = AlarmTemplateFactory.Level("mL", 800, 700, 100, 50, 30, 60);
        var path = new ResourcePath("Devices/PLC/Level");

        var first = template.CreateRules("level-id", "Level", "PLC", path, "plc-level");
        var second = template.CreateRules("level-id", "Level", "PLC", path, "plc-level");

        Assert.Equal(4, first.Count);
        Assert.Equal(first.Select(rule => rule.RuleId), second.Select(rule => rule.RuleId));
        Assert.All(first, rule => Assert.Equal(path, rule.TargetResourcePath));
        Assert.Contains(first, rule => rule.AlarmType == AlarmType.High && rule.Threshold == 700 && rule.Operator == AlarmOperator.GreaterThan);
    }
}
