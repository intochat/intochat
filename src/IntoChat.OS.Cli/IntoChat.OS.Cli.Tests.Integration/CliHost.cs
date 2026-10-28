using IntoChat.OS.Testing;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Cli.Tests.Integration.CliHost))]

namespace IntoChat.OS.Cli.Tests.Integration;

// The CLI facts drive child dotnet processes and need no host; xUnit builds the fixture only for a fact that asks for it.
public sealed class CliHost : BrainHostFixture
{
    protected override TestBrain Configure(TestBrain brain) => brain;
}
