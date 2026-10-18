using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Testing.Aspire.Diagnostics;

namespace IntoChat.OS.Framework.Tests.E2E;

// Proves the trace budget from real exported spans, not from source inspection: an idle shell stays
// quiet, the J1 intent stays within budget, a grain call is exactly two spans (one per end), and the
// Orleans activity source is registered once (a duplicate doubles every call).
// Whole-host idle measurements cannot share a host with identity tests that
// create background app workloads. These measurements share their own host (the Span budget collection).
[Collection("Span budget")]
public sealed class TraceBudgetFacts(ReferenceBrainFixture host) : BrainFact(host)
{
    private const string OrleansSource = "Microsoft.Orleans.Application";

    [Fact(Timeout = 420_000)]
    public async Task IdleShellStaysWithinTheSpanBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = Brain;
        var workspace = User.BrainId;
        await UserConnections.AuthorizeDatabaseAsync(brain, User, ct);
        await AppRuntimeReadiness.WaitAsync(brain, brain.Owner, ct);
        await AppRuntimeReadiness.WaitAsync(brain, User, ct);
        await ContentCaptureOnFacts.WaitIdleAsync(brain, brain.Owner, brain.Owner.BrainId, ct);
        await ContentCaptureOnFacts.WaitIdleAsync(brain, User, workspace, ct);
        var idleSpans = (await Telemetry.ObserveAsync(TimeSpan.FromSeconds(60), ct)).AllSpans.ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine($"Idle {idleSpans.Length} spans/min.");
        Assert.True(idleSpans.Length < 20, $"Idle shell exported {idleSpans.Length} spans/min (budget < 20). Spans: {Describe(idleSpans)}");
    }

    [Fact(Timeout = 420_000)]
    public async Task J1StaysWithinTheSpanBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = Brain;
        var workspace = User.BrainId;
        await UserConnections.AuthorizeDatabaseAsync(brain, User, ct);
        await AppRuntimeReadiness.WaitAsync(brain, brain.Owner, ct);
        await AppRuntimeReadiness.WaitAsync(brain, User, ct);
        await ContentCaptureOnFacts.WaitIdleAsync(brain, brain.Owner, brain.Owner.BrainId, ct);
        await ContentCaptureOnFacts.WaitIdleAsync(brain, User, workspace, ct);
        using var operation = Telemetry.BeginOperation("J1 assistant request");
        var intentStartedAt = operation.StartedAt;
        // A person talks to the assistant app; the request reaches a synapse and the turn's answer is
        // the transcript neuron, which the wait below reads.
        await ContentCaptureOnFacts.SendAsync(brain, workspace, operation, ct, User);
        // Wait for persisted model costs before flushing the journey. Current model metering persists
        // costs through IUsageGrain; script costs use it too, so a span alone cannot prove token usage.
        var costs = await TestWait.UntilAsync(async token =>
            await User.Http.GetFromJsonAsync<JsonElement[]>($"/brains/{workspace}/compute/recent?take=500", token) ?? [],
            rows => rows.Any(row => row.GetProperty("kind").GetString() == "model"
                && row.GetProperty("unit").GetString() == "mtok"
                && row.GetProperty("quantity").GetDecimal() > 0
                && row.GetProperty("at").GetDateTimeOffset() >= intentStartedAt), TimeSpan.FromSeconds(20), ct);
        Assert.Contains(costs, row => row.GetProperty("kind").GetString() == "model"
            && row.GetProperty("quantity").GetDecimal() > 0
            && row.GetProperty("at").GetDateTimeOffset() >= intentStartedAt);
        var capture = await operation.CompleteAsync(ct);
        var intentSpans = capture.AllSpans.ToArray();
        TestContext.Current.TestOutputHelper?.WriteLine($"J1 {intentSpans.Length} spans total.\n{DescribeTree(intentSpans)}");
        // Plan P0.4 measured an in-process agent stream at <= 25, later 131 (2026-10-04: MoveNext
        // pairs, usage, authorization). 140 was that stream plus one event. This turn is scripts
        // joined by signals. Each sandbox neuron call is an HTTP invoke plus an Orleans call, and
        // each behavior completion is a report, an acknowledgement, a run record and a usage write.
        // Both ends are counted, the same rule as OneGrainCallEmitsExactlyTwoSpans. Collapsing the
        // false idle between one-way route/agent work and its signal took the window from 717 to
        // 302 (2026-10-15). What remains is the turn: eight behavior completions, their invokes,
        // signal delivery, four streamed chat reconciles, three presentation refreshes, three run
        // projections, the model call, and this fact's compute read. 320 keeps that floor and one
        // extra streamed step.
        const int J1SpanBudget = 320;
        Assert.True(intentSpans.Length <= J1SpanBudget, $"J1 exported {intentSpans.Length} spans in total (budget <= {J1SpanBudget}).\n{DescribeTree(intentSpans)}");
        Assert.Contains(capture.Spans, span => span.IsGenAi);
        Assert.Contains(capture.Spans, span => string.Equals(span.Name, "IUsageGrain/Add", StringComparison.Ordinal));
    }

    [Fact(Timeout = 300_000)]
    public async Task OneGrainCallEmitsExactlyTwoSpansFromASingleOrleansSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = Brain;

        // The model catalog route drives exactly one silo-internal grain call. Warm the
        // activation first so its one-time spans are not counted as part of the measured call.
        using (var warmOperation = Telemetry.BeginOperation("Warm model catalog"))
        {
            using var request = warmOperation.Request(HttpMethod.Get, CatalogPath);
            using var warm = await warmOperation.SendAsync(brain.HttpClient, request, ct);
            Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
            var warmed = await warmOperation.CompleteAsync(ct);
            Assert.True(GrainCallSpans(warmed).Length >= 2);
        }

        using var operation = Telemetry.BeginOperation(nameof(OneGrainCallEmitsExactlyTwoSpansFromASingleOrleansSource));
        using (var request = operation.Request(HttpMethod.Get, CatalogPath))
        {
            using var response = await operation.SendAsync(brain.HttpClient, request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var callSpans = GrainCallSpans(await operation.CompleteAsync(ct));

        // One grain call crosses the wire once: a client span and a server span, never two of each.
        Assert.True(callSpans.Length == 2, $"A grain call exported {callSpans.Length} spans: {Describe(callSpans)}");
        Assert.Equal([2, 3], callSpans.Select(span => (int)span.Data.Kind).Order().ToArray());
        Assert.Equal([OrleansSource], callSpans.Select(span => span.Scope).Distinct(StringComparer.Ordinal).ToArray());
    }

    private const string CatalogPath = "/ai/models";

    private static TelemetrySpan[] GrainCallSpans(TelemetryCapture capture)
        => capture.Spans
            .Where(span => string.Equals(span.Scope, OrleansSource, StringComparison.Ordinal)
                && string.Equals(span.Name, "IModelCatalog/List", StringComparison.Ordinal))
            .ToArray();


    private static string Describe(IEnumerable<TelemetrySpan> spans)
        => string.Join("; ", spans.Select(span => $"{ResourceName(span)}/{span.Scope}:{span.Name}[kind={span.Data.Kind}]"));


    // Renders the intent's spans as a parent/child tree so a budget failure shows where the
    // spans come from, not just a flat count.
    private static string DescribeTree(IReadOnlyList<TelemetrySpan> spans)
    {
        var byParent = spans.ToLookup(span => span.ParentSpanId ?? string.Empty, StringComparer.Ordinal);
        var ids = spans.Select(span => span.SpanId).ToHashSet(StringComparer.Ordinal);
        var lines = new List<string>();
        void Walk(TelemetrySpan span, int depth)
        {
            // Only routing identities: never dump request bodies, model content or credentials.
            var source = Attribute(span, "orleans.rpc.source_id");
            var target = Attribute(span, "orleans.rpc.target_id");
            var contract = Attribute(span, "orleans.rpc.service");
            lines.Add($"{new string(' ', depth * 2)}{ResourceName(span)}/{span.Scope}:{span.Name}[kind={span.Data.Kind}] source={source} target={target} contract={contract}");
            foreach (var child in byParent[span.SpanId]) { Walk(child, depth + 1); }
        }

        foreach (var root in spans.Where(span => span.ParentSpanId is null || !ids.Contains(span.ParentSpanId)))
        {
            Walk(root, 0);
        }

        var total = spans.Count;
        var perScope = string.Join(", ", spans.GroupBy(span => span.Scope, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key}={group.Count()}"));
        return $"J1 span tree ({total} total; {perScope}):\n" + string.Join("\n", lines);
    }

    private static string Attribute(TelemetrySpan span, string key)
        => span.Data.Attributes.FirstOrDefault(attribute => attribute.Key == key)?.Value?.StringValue ?? "-";

    private static string ResourceName(TelemetrySpan span)
        => span.Resource?.Attributes.FirstOrDefault(attribute => attribute.Key == "service.name")?.Value?.StringValue ?? "-";
}
