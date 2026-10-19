namespace IntoChat.OS.LocalSetup.Tests.Unit;

internal static class Fixtures
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".json");

    public static Task<PreflightReport> CollectAsync(string name, LocalModelOptions? model = null)
        => Preflight.CollectAsync(
            FakeProbeEnvironment.Load(PathOf(name)),
            new PreflightOptions(model ?? new LocalModelOptions(), ShellEnabled: true, RepositoryRoot: "repo"),
            TestContext.Current.CancellationToken);

    public static async Task<(int Exit, string Output)> RunAsync(string name, params string[] extra)
    {
        using var writer = new StringWriter();
        var exit = await Preflight.RunAsync(["preflight", "--fake", PathOf(name), .. extra], writer, TestContext.Current.CancellationToken);
        return (exit, writer.ToString());
    }

    public static CheckResult Result(PreflightReport report, string id) => Assert.Single(report.Results, result => result.Id == id);
}
