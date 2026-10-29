namespace IntoChat.OS.Kernel.Tests.Unit;

// The consolidation prompt's contract and the sensitivity policy the Classify helper holds it to.
public sealed class OsMemoryPromptFacts
{
    private static string Prompt() => File.ReadAllText(Path.Combine(SolutionProjects.Features, "os", "Memory", "prompts", "consolidate.md"));

    [Fact]
    public void TheConsolidatePromptStatesItsContract()
    {
        var prompt = Prompt();
        foreach (var field in new[] { "\"items\"", "disposition", "episodeIds", "kind", "text", "subject", "confidence", "importance", "reason", "contradicts" })
        { Assert.Contains(field, prompt); }
        Assert.Contains("publish|review|discard", prompt);
        Assert.Contains("profile|fact|insight|intent|working", prompt);
        Assert.Contains("never items", prompt);
        Assert.Contains("transient status chatter", prompt);
        Assert.Contains("up to three items of kind `insight`", prompt);
    }

    [Fact]
    public void TheSampleBatchIsReviewedDroppedAndPublished()
    {
        var items = Consolidation.ParseVerdict("""
            {"items":[
              {"disposition":"publish","episodeIds":["a#1"],"kind":"fact","text":"Takes medication for high blood pressure","subject":"user","confidence":0.95,"importance":7,"reason":"","contradicts":null},
              {"disposition":"review","episodeIds":["a#2"],"kind":"fact","text":"Uses the Stripe key sk-live-51HxYzAbCdEf0123","subject":"user","confidence":0.9,"importance":5,"reason":"","contradicts":null},
              {"disposition":"publish","episodeIds":["a#3"],"kind":"profile","text":"Prefers short answers","subject":"user","confidence":0.9,"importance":6,"reason":"","contradicts":null}
            ]}
            """);
        Assert.NotNull(items);
        var decisions = items.Select(item => Consolidation.Classify(item, 0.75, Consolidation.SensitiveClasses)).ToArray();
        Assert.Equal(Consolidation.Disposition.Review, decisions[0].Disposition);
        Assert.Equal(new Consolidation.Decision(Consolidation.Disposition.Discard, "secret"), decisions[1]);
        Assert.Equal(Consolidation.Disposition.Publish, decisions[2].Disposition);
    }

    [Fact]
    public void ThePromptVersionIsAShortHash()
    {
        var version = Consolidation.PromptVersion(Prompt());
        Assert.Matches("^[0-9a-f]{12}$", version);
        Assert.NotEqual(version, Consolidation.PromptVersion(Prompt() + " "));
    }
}
