using System.Collections.Concurrent;
using System.Diagnostics;
using IntoChat.OS.Authority.Integrations;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Tests.Integration.Integrations;

[Trait("host", "own")]
public sealed class IntegrationSeederFacts
{
    private const string Canary = "seed-retry-secret-canary";

    [Fact(Timeout = 60_000)]
    public async Task RetriesFailedRegistrationWithBackoffBeforeStartingConsumers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(2, ct);
        var storage = Storage(brain);
        var consumer = new ReadingConsumer(brain.Platform.Integration("openai"));
        var logger = new SeedLogger();

        await Seeder(brain, consumer, logger).SeedAsync(ct);

        Assert.Equal(IntegrationStatus.Ready, consumer.Observed?.Status);
        Assert.Equal(1, consumer.Calls);
        var attempts = storage.Attempts.ToArray();
        Assert.Equal(3, attempts.Length);
        Assert.True(Stopwatch.GetElapsedTime(attempts[0], attempts[1]) >= TimeSpan.FromSeconds(1));
        Assert.True(Stopwatch.GetElapsedTime(attempts[1], attempts[2]) >= TimeSpan.FromSeconds(2));
        await brain.DeactivateAsync(brain.Platform.Integration("openai"), ct);
        Assert.Equal(IntegrationStatus.Ready, (await brain.Platform.Integration("openai").Read()).Status);
        Assert.Equal(2, logger.Lines.Count(line => line.Level == LogLevel.Warning));
        Assert.DoesNotContain(logger.Lines, line => line.Level == LogLevel.Error);
        AssertSafeLogs(logger);
    }

    [Fact(Timeout = 60_000)]
    public async Task ExhaustedRetriesLeaveRegistrationUnconfiguredAndContinueOtherIntegrations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(int.MaxValue, ct);
        var consumer = new ReadingConsumer(brain.Platform.Integration("openai"));
        var logger = new SeedLogger();

        await Seeder(brain, consumer, logger).SeedAsync(ct);

        Assert.Equal(3, Storage(brain).Attempts.Count);
        Assert.Equal(1, consumer.Calls);
        Assert.Equal(IntegrationStatus.Unconfigured, consumer.Observed?.Status);
        Assert.Equal(IntegrationStatus.Ready, (await brain.Platform.Integration("anthropic").Read()).Status);
        Assert.Single(logger.Lines, line => line.Level == LogLevel.Error);
        AssertSafeLogs(logger);
    }

    [Fact(Timeout = 60_000)]
    public async Task CancellationDuringBackoffStopsRetriesAndConsumers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(int.MaxValue, ct);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var consumer = new ReadingConsumer(brain.Platform.Integration("openai"));
        var logger = new SeedLogger();
        var seeding = Seeder(brain, consumer, logger).SeedAsync(canceled.Token);
        await logger.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seeding.WaitAsync(TimeSpan.FromSeconds(2), ct));
        Assert.Single(Storage(brain).Attempts);
        Assert.Equal(0, consumer.Calls);
        Assert.Equal(IntegrationStatus.Unconfigured, (await brain.Platform.Integration("anthropic").Read()).Status);
        Assert.DoesNotContain(logger.Lines, line => line.Level == LogLevel.Error);
    }

    [Fact(Timeout = 60_000)]
    public async Task CancellationWhileRegistrationIsWritingStopsWaitingBeforeConsumersStart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(0, ct);
        var storage = Storage(brain);
        storage.HoldWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var consumer = new ReadingConsumer(brain.Platform.Integration("openai"));
        var logger = new SeedLogger();
        var seeding = Seeder(brain, consumer, logger).SeedAsync(canceled.Token);
        await storage.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        try
        {
            await canceled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seeding.WaitAsync(TimeSpan.FromSeconds(2), ct));
            Assert.Equal(0, consumer.Calls);
            Assert.Empty(logger.Lines);
        }
        finally
        {
            storage.HoldWrite.SetResult();
            await brain.Platform.Integration("openai").Read().WaitAsync(ct);
        }
    }

    private static Task<ITestBrain> StartAsync(int failures, CancellationToken ct) => TestBrain.Create()
        .WithModule<FakeAiModule>()
        .ConfigureServices(services => services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage,
            (provider, _) => new FailingRegistrationStorage(
                new OrleansGrainStorageSerializer(provider.GetRequiredService<Serializer>()), failures)))
        .StartAsync(ct);

    private static FailingRegistrationStorage Storage(ITestBrain brain) =>
        (FailingRegistrationStorage)brain.Services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage);

    private static IntegrationSeeder Seeder(ITestBrain brain, ReadingConsumer consumer, SeedLogger logger) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [IntegrationSchema.Key("openai", "ApiKey")] = Canary,
            [IntegrationSchema.Key("anthropic", "ApiKey")] = Canary,
        }).Build(),
        FakeAiModule.Integrations,
        brain.Platform,
        logger,
        consumers: [consumer]);

    private static void AssertSafeLogs(SeedLogger logger) => Assert.All(logger.Lines, line =>
    {
        Assert.DoesNotContain(Canary, line.Text, StringComparison.Ordinal);
        Assert.Null(line.Error);
    });

    private sealed class ReadingConsumer(IIntegration registration) : IIntegrationStartup
    {
        public int Calls { get; private set; }
        public IntegrationSnapshot? Observed { get; private set; }
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Observed = await registration.Read().WaitAsync(cancellationToken);
        }
    }

    private sealed class SeedLogger : ILogger<IntegrationSeeder>
    {
        public ConcurrentQueue<(LogLevel Level, string Text, Exception? Error)> Lines { get; } = new();
        public TaskCompletionSource FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            Lines.Enqueue((logLevel, format(state, error), error));
            if (logLevel >= LogLevel.Warning) { FirstFailure.TrySetResult(); }
        }
    }

    private sealed class FailingRegistrationStorage(IGrainStorageSerializer serializer, int failures) : IGrainStorage
    {
        private readonly ConcurrentDictionary<(string, GrainId), BinaryData> data = new();
        public ConcurrentQueue<long> Attempts { get; } = new();
        public TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? HoldWrite { get; set; }

        public Task ReadStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        {
            if (data.TryGetValue((name, id), out var bytes)) { state.State = serializer.Deserialize<T>(bytes); state.RecordExists = true; }
            return Task.CompletedTask;
        }

        public async Task WriteStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        {
            if (typeof(T) == typeof(IntegrationState) && id.Key.ToString() == "integration/openai")
            {
                Attempts.Enqueue(Stopwatch.GetTimestamp());
                FirstWrite.TrySetResult();
                if (HoldWrite is { } held) { await held.Task; }
                if (Attempts.Count <= failures) { throw new IOException(Canary); }
            }
            data[(name, id)] = serializer.Serialize(state.State);
            state.RecordExists = true;
        }

        public Task ClearStateAsync<T>(string name, GrainId id, IGrainState<T> state)
        {
            data.TryRemove((name, id), out _);
            state.RecordExists = false;
            return Task.CompletedTask;
        }
    }
}
