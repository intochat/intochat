using System.Reflection;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

// The flows index starts, lists and resumes flows of any type. Engineering runs are IEngineerRuns' subject, not the index's.
public sealed class FlowContractsKnowNoEngineeringFacts
{
    private static readonly string[] EngineeringWords = ["Engineer", "Candidate", "Ratif", "Worktree", "PullRequest", "PrReview"];

    [Theory]
    [InlineData(typeof(IFlows))]
    [InlineData(typeof(IFlowStarter))]
    [InlineData(typeof(FlowsNeuron))]
    public void TheGenericFlowTypesNameNoEngineeringMember(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var named = type.GetMembers(all)
            .SelectMany(member => member switch
            {
                MethodInfo method => [method.Name, method.ReturnType.ToString(), .. method.GetParameters().Select(parameter => parameter.ParameterType.ToString())],
                FieldInfo field => [field.Name, field.FieldType.ToString()],
                PropertyInfo property => [property.Name, property.PropertyType.ToString()],
                _ => new[] { member.Name },
            })
            .Where(name => EngineeringWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToArray();

        Assert.Empty(named);
    }

    [Fact]
    public void TheEngineeringDoorOwnsTheRunCommandsAndTheEngineeringFlowTypes()
    {
        string[] commands = [.. typeof(IEngineerRuns).GetMethods().Where(method => !method.IsStatic).Select(method => method.Name).Order(StringComparer.Ordinal)];

        Assert.Equal(["InspectEngineerRun", "RatifyEngineerPlan", "SelectEngineerCandidate", "StartEngineerRun"], commands);
        Assert.Equal("flow.engineer", IEngineerRuns.EngineerFlow);
        Assert.Equal("flow.change", IEngineerRuns.ChangeFlow);
        Assert.Equal("flow.pr-review", IEngineerRuns.PrReviewFlow);
        Assert.Contains(typeof(IEngineerFlow), typeof(EngineerFlow).GetInterfaces());
    }
}
