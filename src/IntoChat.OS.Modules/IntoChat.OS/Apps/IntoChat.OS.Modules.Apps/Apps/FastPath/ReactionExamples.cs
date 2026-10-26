using System.Diagnostics;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

// One synapse the caller can see, as the fast path shows it to the model: where it lives, its spec (the
// comments that say what it does) and its code. BehaviorId is set for this brain's own apps, which run.
// Similarity is its closeness to the sentence, History its run record's nudge; Score ranks by both.
internal sealed record ReactionExample(string Origin, string Path, string Spec, string Source, string? BehaviorId, double Similarity = 0, double History = 0)
{
    public string Name => Origin + "/" + Path;
    public double Score => Similarity + History;
}

// The best few synapses for a sentence, best first, and the one to fork when one is close enough.
// Layouts are at most two reference examples of how a view is composed (shipped Calculator and Desktop first); never forked.
internal sealed record ReactionRanking(ReactionExample[] Examples, ReactionExample? Base, ReactionExample[]? LayoutExamples = null)
{
    public ReactionExample[] Layouts => LayoutExamples ?? [];
}

// Few-shot retrieval for the fast path. The corpus is what the caller can see: this brain's apps and the
// features folder. Ranking is by meaning when an embedding model is composed (the same unkeyed generator
// NeuronTypeSearch uses), by words otherwise, then nudged by each synapse's run history in this brain.
internal sealed class ReactionExamples(
    IGrainFactory grains,
    IntoChat.OS.Runtime.IPlainNeurons plain,
    IKernel kernel,
    FeatureCatalog catalog,
    IEmbeddingGenerator<string, Embedding<float>>? embeddings,
    ILogger<ReactionExamples> log,
    int vectorCapacity = ReactionExamples.DefaultVectorCapacity)
{
    public const int Take = 3;
    // A best match at or above its scale's threshold is close enough to fork: the model edits it instead of
    // writing fresh. The two rankings score on different scales, so each has its own. Cosine is
    // text-embedding-3-small's similarity between the sentence and a synapse's spec, where a sentence that
    // restates the spec lands around 0.6 and above and unrelated text well below.
    public const double CosineForkThreshold = 0.6;
    // Word share is the fraction of the sentence's words the spec mentions: three in four.
    public const double WordForkThreshold = 0.75;
    // The most run history moves a score either way, and the runs it takes to earn half of that.
    public const double MaxHistory = 0.05;
    public const double HistoryRuns = 5;
    internal const int DefaultVectorCapacity = 4096;

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);
    private static readonly EventId SyntaxBoundary = new(0, "ReactionSyntaxBoundary");
    private static long _nextInvocation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

    public static ReactionExamples Create(IServiceProvider services) => new(
        services.GetRequiredService<IGrainFactory>(), services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>(), services.GetRequiredService<IKernel>(), services.GetRequiredService<FeatureCatalog>(),
        Embeddings(services), services.GetRequiredService<ILogger<ReactionExamples>>());

    // The sentence embedded once, for every ranking of it; null ranks by words (no model, or the model failed).
    public async Task<float[]?> Embed(string sentence, CancellationToken cancellationToken)
    {
        if (embeddings is null) { return null; }
        try { return (await embeddings.GenerateAsync([sentence], cancellationToken: cancellationToken))[0].Vector.ToArray(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Synapse retrieval fell back to word ranking: the embedding model failed");
            return null;
        }
    }

    // query is the sentence's embedding from Embed when the caller already started it; without one Rank embeds the sentence itself.
    public async Task<ReactionRanking> Rank(string brain, string sentence, CancellationToken cancellationToken, Task<float[]?>? query = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain);
        ArgumentException.ThrowIfNullOrWhiteSpace(sentence);
        var invocation = log.IsEnabled(LogLevel.Debug) ? Interlocked.Increment(ref _nextInvocation) : 0;
        var behaviorsRead = Behaviors(brain, cancellationToken);
        var entries = await Corpus(brain, cancellationToken, invocation);
        if (entries.Length == 0) { return new([], null); }
        ReactionExample[] layouts;
        using (var observation = Observe(invocation, "layouts"))
        {
            layouts = Layouts(entries);
            observation?.Complete();
        }
        ReactionExample[] corpus;
        using (var observation = Observe(invocation, "shared-filter"))
        {
            corpus = [.. entries.Where(entry => !entry.Shared).Select(entry => entry.Example)];
            observation?.Complete();
        }
        if (corpus.Length == 0) { return new([], null, layouts); }
        var (scores, forkThreshold) = await Scores(corpus, sentence, query ?? Embed(sentence, cancellationToken), cancellationToken);
        var behaviors = await behaviorsRead;
        BehaviorView? Behavior(ReactionExample example) => example.BehaviorId is { } id ? behaviors.GetValueOrDefault(id) : null;
        ReactionExample[] scored = [.. corpus
            .Select((example, index) => example with { Similarity = scores[index], History = RunHistory(Behavior(example)) })
            .OrderByDescending(example => example.Score).ThenBy(example => example.Name, StringComparer.Ordinal)];
        // Fork-first: the base is the best scored synapse whose meaning alone is close enough to fork, so among
        // candidates about as close the proven one wins. A failing one is the base only when every candidate is failing.
        var basis = scored.Where(example => example.Similarity >= forkThreshold).OrderBy(example => Failing(Behavior(example))).FirstOrDefault();
        return new([.. scored.Take(Take)], basis, layouts);
    }

    public const int LayoutTake = 2;
    private static readonly HashSet<string> Shipped = new(["features/os/calculator", "features/os/desktop"], StringComparer.OrdinalIgnoreCase);

    // The shipped Calculator and Desktop layouts first, then the rest by name.
    private static ReactionExample[] Layouts(CorpusEntry[] corpus) => [.. corpus.Where(entry => entry.Layout).Select(entry => entry.Example)
        .OrderBy(example => Shipped.Contains(example.Origin) ? 0 : 1).ThenBy(example => example.Name, StringComparer.Ordinal).Take(LayoutTake)];

    // A synapse's run history from the runtime's counters for its current code:
    //   MaxHistory × (successes − failures) / (runs + HistoryRuns)
    // Never run is 0; five clean runs earn half of MaxHistory and a long clean record nears it; failing more than half
    // the runs is below 0 and a long failing record nears −MaxHistory. One the sandbox reports down (exited with an
    // error) is −MaxHistory whatever it ran, since a crash before it subscribes fails no run. Bounded by MaxHistory,
    // so similarity still dominates.
    internal static double RunHistory(BehaviorView? behavior)
    {
        if (behavior is null) { return 0; }
        if (Down(behavior)) { return -MaxHistory; }
        return behavior.Runs > 0 ? MaxHistory * Net(behavior) / (behavior.Runs + HistoryRuns) : 0;
    }

    // Never the base while a candidate that is not failing is close enough: down now, or failures outnumbering
    // clean runs by HistoryRuns or more. Five failed runs in a row are a record; one unlucky run is not.
    internal static bool Failing(BehaviorView? behavior)
        => behavior is not null && (Down(behavior) || Net(behavior) <= -HistoryRuns);

    private static bool Down(BehaviorView behavior) => behavior is { Status: "Exited", Error: not null };

    // Clean runs less failed ones.
    private static long Net(BehaviorView behavior)
    {
        var runs = Math.Max(behavior.Runs, 0);
        return runs - 2 * Math.Clamp(behavior.Failures, 0, runs);
    }

    private sealed record CorpusEntry(ReactionExample Example, bool Shared, bool Layout);

    private async Task<CorpusEntry[]> Corpus(string brain, CancellationToken cancellationToken, long invocation)
    {
        List<CorpusEntry> examples = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        // The brain's own copies come first: they carry run history, and a feature's identical copy adds nothing.
        // The apps are read together; their order stays the kernel's.
        Task<IReadOnlyDictionary<(string Owner, string Name), AppBundle>> bundles;
        using (var observation = Observe(invocation, "catalog-call"))
        {
            bundles = catalog.Bundles();
            observation?.Complete();
        }
        List<string> keys = [];
        await foreach (var instance in kernel.InstancesIn(brain, kernel.TypeOf(typeof(IApp)), cancellationToken))
        {
            if (IApp.NameOf(brain, instance.Key) is not null) { keys.Add(instance.Key); }
        }
        var apps = await Task.WhenAll(keys.Select(key => plain.Get<IApp>(key).Read().WaitAsync(cancellationToken)));
        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            Add(key, apps[i].Manifest, apps[i].Files, path => BehaviorIds.For(key, path));
        }
        var shipped = await bundles;
        using (var observation = Observe(invocation, "shipped-add"))
        {
            foreach (var ((owner, name), bundle) in shipped)
            { Add($"features/{owner}/{name}", bundle.Manifest, bundle.Files, _ => null); }
            observation?.Complete();
        }
        return [.. examples];

        void Add(string origin, AppManifest manifest, IReadOnlyDictionary<string, string> files, Func<string, string?> behavior)
        {
            foreach (var (path, source) in files.OrderBy(file => file.Key, StringComparer.Ordinal))
            {
                if (!AppFiles.IsProgram(path) || source is null) { continue; }
                var root = CSharpSyntaxTree.ParseText(source, Parse).GetCompilationUnitRoot();
                var shared = ReactionShape.Shared(root);
                var layout = ReactionShape.Layout(root);
                if ((shared && !layout) || !seen.Add(source)) { continue; }
                examples.Add(new(new(origin, path, Spec(manifest, path, root), source, behavior(path)), shared, layout));
            }
        }
    }

    private SyntaxObservation? Observe(long invocation, string stage)
        => invocation == 0 ? null : new(log, invocation, stage);

    // These scopes contain no await: allocation deltas belong to the same executing thread.
    private sealed class SyntaxObservation : IDisposable
    {
        private readonly ILogger<ReactionExamples> _log;
        private readonly long _invocation;
        private readonly string _stage;
        private readonly long _started;
        private readonly long _allocated;
        private bool _finished;

        public SyntaxObservation(ILogger<ReactionExamples> log, long invocation, string stage)
        {
            _log = log;
            _invocation = invocation;
            _stage = stage;
            Write("entered", 0, 0);
            _started = Stopwatch.GetTimestamp();
            _allocated = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Complete() => Finish("completed");

        public void Dispose()
        {
            if (!_finished) { Finish("stopped"); }
        }

        private void Finish(string state)
        {
            var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocated;
            _finished = true;
            Write(state, elapsed, allocated);
        }

        private void Write(string state, double elapsed, long allocated)
            => _log.LogDebug(SyntaxBoundary,
                "Synapse syntax boundary {Invocation} {Stage} {State} {ElapsedMs} {AllocatedBytes}",
                _invocation, _stage, state, elapsed, allocated);
    }

    // What the synapse says it does: the app it belongs to, its file name, and the comments above its code
    // and its reaction.
    private static string Spec(AppManifest manifest, string path, CompilationUnitSyntax root)
    {
        var statements = root.Members.OfType<GlobalStatementSyntax>().ToArray();
        var reaction = statements.FirstOrDefault(statement => statement.Statement is CommonForEachStatementSyntax or ExpressionStatementSyntax { Expression: AwaitExpressionSyntax });
        var comments = new[] { root.GetFirstToken(includeZeroWidth: true), statements.FirstOrDefault()?.GetFirstToken() ?? default, reaction?.GetFirstToken() ?? default }
            .Where(token => !token.IsKind(SyntaxKind.None))
            .SelectMany(token => token.LeadingTrivia)
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            .Select(trivia => trivia.ToString().TrimStart('/').Trim().TrimStart('*').TrimEnd('/').TrimEnd('*').Trim())
            .Where(comment => comment.Length > 0)
            .Distinct(StringComparer.Ordinal);
        return $"{manifest.Title}: {System.IO.Path.GetFileNameWithoutExtension(path)}\n{string.Join('\n', comments)}".Trim();
    }

    // Each synapse's score and the fork threshold of the scale it was scored on.
    private async Task<(double[] Scores, double ForkThreshold)> Scores(ReactionExample[] corpus, string sentence, Task<float[]?> embedded, CancellationToken cancellationToken)
    {
        if (embeddings is null) { return (ByWords(corpus, sentence), WordForkThreshold); }
        try
        {
            var vectors = await Vectors(corpus, cancellationToken);
            if (await embedded is not { } query) { return (ByWords(corpus, sentence), WordForkThreshold); }
            return ([.. vectors.Select(vector => vector.Length == query.Length ? Cosine(query, vector) : 0)], CosineForkThreshold);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "Synapse retrieval fell back to word ranking: the embedding model failed");
            return (ByWords(corpus, sentence), WordForkThreshold);
        }
    }

    // The answer is built from the cache and the newly embedded specs; the cache only remembers. Past its
    // capacity it starts over with this corpus, the likeliest to be asked for next.
    private async Task<float[][]> Vectors(ReactionExample[] corpus, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var vectors = new float[corpus.Length][];
            List<int> missing = [];
            for (var i = 0; i < corpus.Length; i++)
            {
                if (_vectors.TryGetValue(Key(corpus[i]), out var cached)) { vectors[i] = cached; }
                else { missing.Add(i); }
            }
            if (missing.Count == 0) { return vectors; }
            var generated = await embeddings!.GenerateAsync(missing.Select(index => corpus[index].Spec), cancellationToken: cancellationToken);
            if (generated.Count != missing.Count) { throw new InvalidOperationException("The embedding model returned a different number of vectors than texts."); }
            for (var i = 0; i < missing.Count; i++) { vectors[missing[i]] = generated[i].Vector.ToArray(); }
            if (_vectors.Count + missing.Count > vectorCapacity) { _vectors.Clear(); }
            for (var i = 0; i < corpus.Length && _vectors.Count < vectorCapacity; i++) { _vectors.TryAdd(Key(corpus[i]), vectors[i]); }
            return vectors;
        }
        finally { _gate.Release(); }
    }

    private static string Key(ReactionExample example) => example.Name + "\n" + example.Spec;

    // The share of the sentence's words (three letters or more) that the synapse's spec mentions.
    private static double[] ByWords(ReactionExample[] corpus, string sentence)
    {
        var terms = sentence.Split([' ', ',', '.', ';', ':', '!', '?', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 2).Select(term => term.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (terms.Length == 0) { return new double[corpus.Length]; }
        return [.. corpus.Select(example => (double)terms.Count(term => example.Spec.Contains(term, StringComparison.OrdinalIgnoreCase)) / terms.Length)];
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }

    // A brain without a runtime has no run history; history only orders the synapses, so its absence is no error.
    private async Task<IReadOnlyDictionary<string, BehaviorView>> Behaviors(string brain, CancellationToken cancellationToken)
    {
        try
        {
            var runtime = await grains.GetGrain<IBrainRuntime>(BrainRuntimeKeys.For(brain)).Inspect(cancellationToken).WaitAsync(cancellationToken);
            return runtime.Behaviors.GroupBy(behavior => behavior.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogDebug(error, "No run history for brain {Brain}", brain);
            return new Dictionary<string, BehaviorView>(StringComparer.Ordinal);
        }
    }

    // The default generator's factory throws when nothing is configured; that host ranks by words.
    private static IEmbeddingGenerator<string, Embedding<float>>? Embeddings(IServiceProvider services)
    {
        try { return services.GetService<IEmbeddingGenerator<string, Embedding<float>>>(); }
        catch (InvalidOperationException) { return null; }
    }
}
