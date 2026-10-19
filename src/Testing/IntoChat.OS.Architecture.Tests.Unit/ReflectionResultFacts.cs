namespace IntoChat.OS.Architecture.Tests.Unit;

// A task of unknown result type is read through TaskBoxing.ResultAsync, which awaits it and caches one delegate per result type.
public sealed class ReflectionResultFacts
{
    private static readonly string[] Reads = ["GetProperty(\"Result\")", "nameof(Task<object>.Result)"];

    [Fact]
    public void NoProductionCodeReadsATasksResultByReflection()
    {
        var hits = Repository.ProjectFiles("*.cs", path => Path.GetFileName(path) != "TaskBoxing.cs",
                project => project.Name != "IntoChat.OS.Architecture.Tests.Unit" && !project.Name.EndsWith(".Tests", StringComparison.Ordinal)
                    && !project.Name.Contains(".Tests.", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path) is var text && Reads.Any(read => text.Contains(read, StringComparison.Ordinal)))
            .Select(Repository.Relative).Order(StringComparer.Ordinal).ToArray();

        Assert.True(hits.Length == 0, "Task<T>.Result read by reflection; use TaskBoxing.ResultAsync." + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }
}
