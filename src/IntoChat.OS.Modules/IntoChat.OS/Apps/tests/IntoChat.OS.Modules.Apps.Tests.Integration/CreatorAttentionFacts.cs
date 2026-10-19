using System.Reflection;
using IntoChat.OS.AI;
using IntoChat.OS.AI.Models.OpenAI;
using IntoChat.OS.Apps;
using IntoChat.OS.Testing;
using IntoChat.OS.Testing.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

// QUAL-04: the Creator reports a failure to the app as an attention note; when that report itself fails the
// failure is logged instead of vanishing, and the original error still reaches the caller.
public sealed class CreatorAttentionFacts
{
    private const string Brain = "creator-attention-facts";

    [Trait("host", "own")]
    [Fact]
    public async Task AFailedAttentionReportIsLoggedAndTheOriginalErrorSurvives()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        var logs = new CapturingLoggerProvider();
        await using var brain = await TestBrain.Create()
            .WithModule<AIModule>()
            .WithModule<AppsModule, AppsOptions>(apps =>
            {
                apps.AuthorModel = nameof(IGpt56Luna);
                apps.BuilderModel = nameof(IGpt56Luna);
            })
            .WithFakeModel(model)
            .ConfigureServices(services => services.AddSingleton<ILogger<Creator>>(new CapturingLoggerProvider.Typed(logs.CreateLogger(nameof(Creator)))))
            .StartAsync(ct);
        var creator = brain.Services.GetRequiredService<Creator>();
        var app = DispatchProxy.Create<IApp, FailingApp>();
        var fake = DispatchProxy.Create<IIntoChatOS, FakeBrain>();
        ((FakeBrain)(object)fake).App = app;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => creator.Author(fake, Brain, "shouter", "Make it louder.", ct));

        Assert.Equal("progress is down", thrown.Message);
        var entry = Assert.Single(logs.Entries, entry => entry.Category.EndsWith(nameof(Creator), StringComparison.Ordinal));
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("progress is down", entry.Exception?.Message);
    }

    public class FailingApp : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            nameof(IApp.Read) => Task.FromResult(new AppSnapshot("shouter", new AppManifest("Shouter", "", [], [], []), "# Shouter", [], [], [], "1",
                AppStatus.Live, [], [])),
            nameof(IApp.Progress) => Task.FromException(new InvalidOperationException("progress is down")),
            _ => throw new NotSupportedException(targetMethod.Name),
        };
    }

    public class FakeBrain : DispatchProxy
    {
        public IApp? App { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == nameof(IIntoChatOS.Get) ? App : throw new NotSupportedException(targetMethod.Name);
    }

    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose() { }

        public sealed class Typed(ILogger inner) : ILogger<Creator>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => inner.Log(logLevel, eventId, state, exception, formatter);
        }

        private sealed class Capture(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) { owner.Entries.Add((category, logLevel, exception)); }
            }
        }
    }
}
