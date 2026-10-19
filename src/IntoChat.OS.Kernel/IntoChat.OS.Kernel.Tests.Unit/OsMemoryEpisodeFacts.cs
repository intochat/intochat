namespace IntoChat.OS.Kernel.Tests.Unit;

// Memory's episode helper (features/os/Memory/Episodes.cs) is compiled into this project.
public sealed class OsMemoryEpisodeFacts
{
    [Fact]
    public void ASummaryParses()
    {
        var parsed = global::Episodes.ParseSummary("```json\n{\"summary\":\"- wanted a flight\",\"importance\":7}\n```");
        Assert.Equal(("- wanted a flight", 7), parsed);
    }

    [Fact]
    public void ASummaryWithoutImportanceIsNull()
    {
        Assert.Null(global::Episodes.ParseSummary("{\"summary\":\"- x\"}"));
        Assert.Null(global::Episodes.ParseSummary("not json"));
    }

    [Fact]
    public void TheFallbackIsBoundedAndMarked()
    {
        var fallback = global::Episodes.Fallback(new string('u', 500), new string('r', 500));
        Assert.Equal(3, fallback.Importance);
        Assert.True(fallback.IsFallback);
        Assert.Contains(new string('u', 300), fallback.Summary);
        Assert.DoesNotContain(new string('u', 301), fallback.Summary);
        Assert.Contains(new string('r', 300), fallback.Summary);
        Assert.DoesNotContain(new string('r', 301), fallback.Summary);
    }

    [Fact]
    public void AnEpisodeIdIsAgentHashRun()
        => Assert.Equal("chat/a#run1", global::Episodes.Id("chat/a", "run1"));
}
