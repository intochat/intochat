using System.Runtime.CompilerServices;
using IntoChat.OS.Google.Gmail.Testing;
using IntoChat.OS.Microsoft.CSharp.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class GmailAuthoringFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task EveryGmailReadmeExampleCompilesAgainstTheInstalledContracts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var google = await GoogleStub.StartAsync(ct);
        await using var brain = await GmailBrain.Compose(RuntimeBrain.Create(new FakeSessionHost()), google).StartAsync(ct);
        var compiler = brain.Services.GetRequiredService<CSharpScriptCheck>();
        var examples = Readme().Replace("\r\n", "\n", StringComparison.Ordinal).Split("```csharp\n", StringSplitOptions.None)
            .Skip(1).Select(block => block[..block.IndexOf("```", StringComparison.Ordinal)]).ToArray();
        Assert.Equal(3, examples.Length);
        foreach (var source in examples)
        {
            var result = compiler.Compile(new Dictionary<string, string> { ["example.cs"] = source });
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }
    }

    private static string Readme([CallerFilePath] string source = "") => File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!,
        "../../../../Google/Gmail/IntoChat.OS.Modules.Google.Gmail/README.md"));
}
