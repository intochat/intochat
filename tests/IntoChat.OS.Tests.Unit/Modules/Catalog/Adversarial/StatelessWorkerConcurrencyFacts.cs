using System.Diagnostics;
using IntoChat.OS.Apps.Markdraw;
using IntoChat.OS.Modules.Apps;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Adversarial;

/// <summary>
/// Empirical adversarial verification of [StatelessWorker] grains:
/// 1. IDataAnalyticsWorker / DataAnalyticsEngine concurrent multi-threaded pipeline execution without deadlocks or state pollution.
/// 2. IMarkdrawSceneWorker / MarkdrawSceneEngine concurrent multi-threaded parsing, patching, and rendering.
/// </summary>
public sealed class StatelessWorkerConcurrencyFacts
{
    [Fact]
    public async Task DataAnalyticsWorker_ConcurrentMultiThreadedLoad_NoDeadlocksOrStatePollution()
    {
        const int concurrency = 50;
        var worker = new DataAnalyticsWorker();

        // 50 parallel requests with diverse queries, data shapes, and aggregations
        var tasks = Enumerable.Range(1, concurrency).Select(async i =>
        {
            var prompt = (i % 3) switch
            {
                0 => $"Pipeline {i} in pipeline.csv: Stage A: 100, Stage B: {200 + i}, Stage C: 50. Pie chart by Stage.",
                1 => $"SELECT * FROM deals WHERE Amount > 500 GROUP BY Category AVG",
                _ => $"Team {i} Sales: North: {1000 * i}, South: 2500, East: 1800. Bar chart total by Region.",
            };

            var csvData = $$"""
                Category,Amount
                Hardware,{{1000 + i}}
                Software,{{2000 + i}}
                Services,{{500 + i}}
                """;

            var result = await worker.ExecuteAsync(prompt, $"tenant-db-{i}", fileDatasetText: csvData, fileDatasetPath: $"/work/pipeline-{i}.csv");

            Assert.NotNull(result);
            Assert.NotEmpty(result.ChartTitle);
            Assert.True(result.TotalValue > 0, $"Total value for worker turn {i} should be > 0");
            Assert.NotNull(result.Summary);
            return result;
        }).ToArray();

        var stopwatch = Stopwatch.StartNew();
        var results = await Task.WhenAll(tasks);
        stopwatch.Stop();

        Assert.Equal(concurrency, results.Length);
        // All 50 executions must finish fast without deadlocks or static lock serialization
        Assert.True(stopwatch.ElapsedMilliseconds < 15000, $"50 concurrent executions took {stopwatch.ElapsedMilliseconds}ms, indicating severe serialization or contention.");
    }

    [Fact]
    public async Task MarkdrawSceneWorker_ConcurrentMultiThreadedLoad_ThreadSafeAndDeterministic()
    {
        const int concurrency = 50;

        const string baseScene = """
            # Cloud Infrastructure

            ```markdraw
            rect "API Gateway" id=gw at 100,100 size 200x80 fill=#1e3a8a color=#60a5fa
            rect "Auth Service" id=auth at 400,100 size 200x80 fill=#064e3b color=#34d399
            arrow gw -> auth "JWT Verify" color=#60a5fa
            ```
            """;

        var tasks = Enumerable.Range(1, concurrency).Select(i => Task.Run(() =>
        {
            // 1. Concurrent parsing
            var elements = MarkdrawSceneEngine.ParseSceneElements(baseScene);
            Assert.Equal(3, elements.Length);

            // 2. Concurrent patching
            var prompt = $"Add database 'WorkerDB-{i}' connected to auth";
            var (merged, summary) = MarkdrawSceneEngine.ApplyCoDrawingPatch(baseScene, prompt: prompt, explicitDsl: null, removeIdsCsv: null);
            Assert.Contains($"WorkerDB-{i}", merged, StringComparison.Ordinal);
            Assert.Contains("added", summary, StringComparison.OrdinalIgnoreCase);

            // 3. Concurrent SVG preview rendering
            var parsedMerged = MarkdrawSceneEngine.ParseSceneElements(merged);
            var svg = MarkdrawSceneEngine.RenderSvgPreview(parsedMerged);
            Assert.StartsWith("<svg", svg, StringComparison.OrdinalIgnoreCase);

            // 4. Concurrent Excalidraw JSON export
            var json = MarkdrawSceneEngine.RenderExcalidrawJson(parsedMerged);
            Assert.Contains("\"type\": \"excalidraw\"", json, StringComparison.Ordinal);

            return summary;
        })).ToArray();

        var stopwatch = Stopwatch.StartNew();
        var summaries = await Task.WhenAll(tasks);
        stopwatch.Stop();

        Assert.Equal(concurrency, summaries.Length);
        Assert.True(stopwatch.ElapsedMilliseconds < 15000, $"50 concurrent Markdraw worker turns took {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task DataAnalyticsAndMarkdrawWorkers_InterleavedConcurrentLoad_SurvivesWithoutDeadlock()
    {
        var dataWorker = new DataAnalyticsWorker();

        const int iterations = 30;

        var ct = TestContext.Current.CancellationToken;
        var tasks = new List<Task>();
        for (var i = 0; i < iterations; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                var query = $"Dataset {idx}: A: {10 + idx}, B: 20, C: 30";
                var res = await dataWorker.ExecuteAsync(query, $"db-{idx}");
                Assert.NotNull(res.Summary);
            }, ct));

            tasks.Add(Task.Run(() =>
            {
                var dsl = $"""
                    ```markdraw
                    rect "Node-{idx}" id=n{idx} at 10,10 size 100x50
                    ```
                    """;
                var elems = MarkdrawSceneEngine.ParseSceneElements(dsl);
                Assert.Single(elems);
                var svg = MarkdrawSceneEngine.RenderSvgPreview(elems);
                Assert.Contains($"Node-{idx}", svg, StringComparison.Ordinal);
            }, ct));
        }

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(tasks);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 10000, $"Interleaved worker executions took {sw.ElapsedMilliseconds}ms");
    }
}
