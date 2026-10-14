using System.Diagnostics;
using DigitalBrain.Apps;

namespace DigitalBrain.OS.Tests.E2E;

// Each folder is its own result. xUnit runs these cases serially on one browser and host.
[Collection("Reference host")]
public sealed class ShippedAppFacts(ShippedAppsFixture fixture) : IClassFixture<ShippedAppsFixture>
{
    public static TheoryData<string, string> ShippedApps => new(ShippedAppsFixture.Features.Select(feature => (feature.Owner, feature.Bundle.Name)));

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(ShippedApps))]
    public async Task FolderAppRunsInTheHeadlessBrowser(string owner, string name)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = budget.Token;
        using var caller = fixture.Person.AsCaller();
        var watch = Stopwatch.StartNew();
        fixture.EnsureUsable();
        Exception? failure = null;
        try
        {
            await fixture.PrepareAsync(name, ct);
            if (name == "ino") { await fixture.LoadAsync(Feature("os", "assistant"), ct); }
            await fixture.LoadAsync(Feature(owner, name), ct);
            await fixture.ShowAsync(name, ct);
            await ShippedAppJourneys.ExerciseAsync(fixture, name, ct);
            Console.WriteLine($"[app-e2e] {owner}/{name} complete: {watch.Elapsed.TotalSeconds:F2}s");
        }
        catch (Exception error)
        {
            failure = error;
            try
            {
                var path = Path.Combine(Path.GetTempPath(), $"digitalbrain-app-{name}.png");
                await fixture.Page.ScreenshotAsync(new() { Path = path, Timeout = 5_000 });
                Console.WriteLine($"[app-e2e] failure screenshot: {path}");
            }
            catch (Exception capture) { error.Data["ScreenshotFailure"] = capture.Message; }
            if (ct.IsCancellationRequested) { fixture.Poison(error); }
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await fixture.FinishAsync(cleanup.Token); }
            catch (Exception error)
            {
                fixture.Poison(error);
                if (failure is null) { throw; }
                failure.Data["AppCleanupFailure"] = error.ToString();
                Console.WriteLine($"[app-e2e] cleanup also failed: {error}");
            }
        }
    }

    private static ShippedFeature Feature(string owner, string name)
        => ShippedAppsFixture.Features.Single(feature => feature.Owner == owner && feature.Bundle.Name == name);
}
