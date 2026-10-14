using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;

namespace DigitalBrain.OS.Tests.E2E;

// The features folder is the program. This fact loads every shipped app from that folder through the
// same import-or-replace the host uses, in one brain, and the app is working when its startup
// publishes a presentation. Each app gets the same four minutes a single runtime fact gets.
public sealed class ShippedAppFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    private static readonly TimeSpan AppBudget = TimeSpan.FromMinutes(4);

    [Fact(Timeout = 4_800_000)]
    public async Task EveryShippedAppLoadsFromTheFeaturesFolderAndPresents()
    {
        var ct = TestContext.Current.CancellationToken;
        var features = new FeatureDirectory(SolutionProjects.Features).Load()
            .OrderBy(feature => feature.Owner, StringComparer.Ordinal)
            .ThenBy(feature => feature.Bundle.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(features);

        List<string> failures = [];
        foreach (var feature in features)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(AppBudget);
            try { await LoadAndPresent(feature, budget.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { failures.Add($"{feature.Owner}/{feature.Bundle.Name}: did not present within {AppBudget.TotalMinutes:0} minutes"); }
            catch (Exception error) when (error is not OperationCanceledException)
            { failures.Add($"{feature.Owner}/{feature.Bundle.Name}: {error.Message}"); }
        }

        Assert.True(failures.Count == 0, "Shipped apps that did not present:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private async Task LoadAndPresent(ShippedFeature feature, CancellationToken ct)
    {
        var name = feature.Bundle.Name;
        var origin = new AppOrigin(feature.Owner, new AppSource.Shipped(), name, feature.Bundle.Version);
        var app = Brain.Get<IApp>(IApp.Key(WorkspaceId, name));
        await using var presented = await Brain.SubscribeAsync<PresentationChanged>(app, ct);

        var held = await app.Read();
        var snapshot = held.Manifest.Title.Length == 0
            ? await app.Import(feature.Bundle, origin)
            : await app.Replace(feature.Bundle, origin);
        if (snapshot.Status != AppStatus.Live)
        { throw new InvalidOperationException($"{snapshot.Status}: {string.Join(" | ", snapshot.Activity)}"); }
        if (snapshot.Version != feature.Bundle.Version)
        { throw new InvalidOperationException($"running {snapshot.Version}, folder is {feature.Bundle.Version}"); }

        if (snapshot.Presentation is not { Root: not null, Error: null })
        {
            await using var signals = presented.ReadAllAsync(ct).GetAsyncEnumerator(ct);
            while (snapshot.Presentation is not { Root: not null, Error: null })
            {
                if (snapshot.Presentation?.Error is { } error)
                { throw new InvalidOperationException($"could not present: {error}"); }
                if (!await signals.MoveNextAsync())
                { throw new InvalidOperationException("the presentation stream ended before the app presented"); }
                snapshot = await app.Read();
            }
        }

        if (string.IsNullOrWhiteSpace(snapshot.Presentation!.Root!.Sphere?.Icon))
        { throw new InvalidOperationException("presented without an icon"); }
    }
}
