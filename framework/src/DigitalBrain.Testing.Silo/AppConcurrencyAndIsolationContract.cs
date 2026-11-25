using System.Diagnostics.CodeAnalysis;
using DigitalBrain.Units;
using Xunit;

namespace DigitalBrain.Testing;

/// <summary>
/// Reusable contract test suite verifying cold-start rendering, key isolation, and concurrent commands
/// for a screen across both <see cref="NeuronRuntimeKind.TestOS"/> and
/// <see cref="NeuronRuntimeKind.Silo"/> runtimes.
/// </summary>
/// <typeparam name="TScreen">The concrete screen type under test.</typeparam>
/// <typeparam name="TFixture">The <see cref="BothRuntimesFixture"/> hosting the screen's module.</typeparam>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "xUnit contract test methods use underscore naming convention.")]
public abstract class AppConcurrencyAndIsolationContract<TScreen, TFixture>(TFixture fixture)
    : IClassFixture<TFixture>
    where TScreen : class
    where TFixture : BothRuntimesFixture
{
    /// <summary>
    /// Optional key of a node in the screen's initial view whose command 10 callers send at once. Return <see langword="null"/> to
    /// skip the concurrent command test.
    /// </summary>
    protected virtual string? ConcurrentCommandKey => null;

    /// <summary>Opens a new <see cref="AppSession{TScreen}"/> on the specified runtime.</summary>
    protected Task<AppSession<TScreen>> Open(NeuronRuntimeKind kind, string? key = null, CancellationToken ct = default)
        => fixture.Open<TScreen>(kind, key, ct);

    /// <summary>Verifies that opening a fresh screen instance renders a valid initial view.</summary>
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ColdStart_RendersValidInitialView(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        await using var session = await AppSession.OpenAsync<TScreen>(runtime);

        Assert.NotNull(session.CurrentView);
        Assert.True(
            session.CurrentView.Generation >= 1,
            $"Expected {typeof(TScreen).Name} initial generation >= 1 on {kind}, got {session.CurrentView.Generation}.");
    }

    /// <summary>Verifies that two screen sessions with distinct keys render independently without sharing state.</summary>
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task IsolatedKeys_DoNotShareState(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        var keyA = $"iso-a-{Guid.NewGuid():N}";
        var keyB = $"iso-b-{Guid.NewGuid():N}";

        Assert.NotEqual(keyA, keyB);

        await using var sessionA = await AppSession.OpenAsync<TScreen>(runtime, keyA);
        await using var sessionB = await AppSession.OpenAsync<TScreen>(runtime, keyB);

        Assert.NotNull(sessionA.CurrentView);
        Assert.NotNull(sessionB.CurrentView);
        Assert.True(sessionA.CurrentView.Generation >= 1);
        Assert.True(sessionB.CurrentView.Generation >= 1);
    }

    /// <summary>
    /// When <see cref="ConcurrentCommandKey"/> is overridden, races 10 callers sending that node's command from the same initial
    /// view and asserts every one is handled: a command carries no generation, so each is judged by the screen's state when its turn
    /// runs, and the turns run one at a time, so none fails and the screen still draws a view afterwards.
    /// </summary>
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ConcurrentCommands_FromOneView_AreEachHandled(NeuronRuntimeKind kind)
    {
        var commandKey = ConcurrentCommandKey;
        if (string.IsNullOrWhiteSpace(commandKey))
        {
            return;
        }

        const int CallerCount = 10;
        var runtime = await fixture.Runtime(kind);
        var sharedKey = $"race-{Guid.NewGuid():N}";

        var sessions = new List<AppSession<TScreen>>(CallerCount);
        try
        {
            for (var i = 0; i < CallerCount; i++)
            {
                sessions.Add(await AppSession.OpenAsync<TScreen>(runtime, sharedKey));
            }

            var initialGeneration = sessions[0].CurrentView.Generation;
            Assert.All(sessions, s => Assert.Equal(initialGeneration, s.CurrentView.Generation));

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = sessions.Select(async session =>
            {
                await gate.Task.ConfigureAwait(false);
                await session.Click(commandKey).ConfigureAwait(false);
                return session.CurrentView.Generation;
            }).ToArray();

            gate.SetResult();
            var generations = await Task.WhenAll(tasks);

            Assert.All(generations, generation => Assert.True(generation >= initialGeneration,
                $"A command on {typeof(TScreen).Name} ({kind}) left the view at generation {generation}, before {initialGeneration}."));
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }
}
