using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Testing;

namespace APP_NAMESPACE.Tests.Unit;

public sealed class APP_CLASSFacts
{
    // The generated baseline: one row per operation, the empty input, a fake for every required capability, under 5 s. Keep it.
    public static IEnumerable<TheoryDataRow<AppOperationCase>> Operations => AppBaseline.Cases(typeof(APP_CLASS));

    [Theory]
    [MemberData(nameof(Operations))]
    public Task Baseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    // Scenario: Opening shows the start view. Add one fact per scenario in the spec comment.
    [Fact]
    public async Task Opening_shows_the_start_view()
    {
        await using var os = TestOS.Local();
        var app = await os.Install<APP_CLASS>();

        await app.Request("open");

        Assert.Contains("Welcome to APP_CLASS.", app.Main.Texts);
        Assert.Null(app.App.RecordedError);
    }

    [Fact]
    public void App_id_is_lowercase_and_dotted()
    {
        Assert.Matches(new Regex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)*$"), TestOS.ManifestOf(typeof(APP_CLASS)).Id);
    }
}
