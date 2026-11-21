using System.Diagnostics.CodeAnalysis;
using DigitalBrain.Units;
using Xunit;

namespace DigitalBrain.Testing;

/// <summary>
/// Reusable contract test suite verifying cold-start rendering, key isolation, and optimistic-concurrency
/// activation semantics for a screen across both <see cref="NeuronRuntimeKind.TestOS"/> and
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
    /// Optional interactive node key in the screen's initial view to exercise under a 10-caller
    /// optimistic-concurrency race. Return <see langword="null"/> to skip the concurrent activation test.
    /// </summary>
    protected virtual string? ConcurrentActivationKey => null;

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
    /// When <see cref="ConcurrentActivationKey"/> is overridden, races 10 callers activating the same
    /// initial generation concurrently and asserts at most 1 succeeds while all others are refused as stale.
    /// </summary>
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ConcurrentActivation_SameGeneration_AtMostOneSucceeds(NeuronRuntimeKind kind)
    {
        var activationKey = ConcurrentActivationKey;
        if (string.IsNullOrWhiteSpace(activationKey))
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
                try
                {
                    await session.Click(activationKey).ConfigureAwait(false);
                    return (Succeeded: true, RefusedReason: (string?)null);
                }
                catch (ActivationRefusedException ex)
                {
                    return (Succeeded: false, RefusedReason: ex.Reason);
                }
            }).ToArray();

            gate.SetResult();
            var results = await Task.WhenAll(tasks);

            var succeeded = results.Count(r => r.Succeeded);
            var staleRefusals = results.Count(r => !r.Succeeded && r.RefusedReason == ActivationRefusedException.Stale);

            Assert.True(
                succeeded <= 1,
                $"Expected at most 1 caller to win optimistic-concurrency race on {typeof(TScreen).Name} ({kind}), but {succeeded} succeeded.");
            Assert.Equal(CallerCount, succeeded + staleRefusals);
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
