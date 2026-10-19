namespace IntoChat.OS.Architecture.Tests.Unit;

// CA2016 (forward the CancellationToken) is an error for every C# file, so a method holding a token cannot start an
// awaitable call that accepts one and leave it behind. Grain contract methods without a token have no source to forward;
// they are not changed here (the contract manifest does not move).
public sealed class TokenForwardingFacts
{
    [Fact]
    public void ForwardingTheCancellationTokenIsABuildError()
    {
        var lines = File.ReadAllLines(Path.Combine(Repository.Root, ".editorconfig"));
        var section = lines.SkipWhile(line => line.Trim() != "[*.cs]").Skip(1).TakeWhile(line => !line.TrimStart().StartsWith('[')).Select(line => line.Trim()).ToArray();

        Assert.Contains("dotnet_diagnostic.CA2016.severity = error", section);
        Assert.DoesNotContain(section, line => line.StartsWith("dotnet_diagnostic.CA2016.severity", StringComparison.Ordinal) && !line.EndsWith("= error", StringComparison.Ordinal));
    }
}
