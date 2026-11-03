using DigitalBrain.Testing.Conformance.Host;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire.Conformance;

public sealed class OsHostConformanceFacts : HostConformanceFacts
{
    public static TheoryData<string> TargetNames => new(IntoChatOsTarget.TargetName);

    protected override IEnumerable<FrameworkTarget> Targets => [new IntoChatOsTarget()];

    [Fact]
    public void TheTargetHostsEveryOsModule()
    {
        var expected = IntoChatOsTarget.ExpectedOsModules()
            .Append(typeof(FrameworkModule))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(IntoChatOsTarget.ExpectedOsModules());
        Assert.Equal(expected, IntoChatOsTarget.Modules);
    }
}

