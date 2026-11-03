using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire.Journeys;

[Collection(OsAppHostCollection.Name)]
public sealed class ProvenanceFacts(OsAppHostFixture host)
{
    [Fact]
    public async Task EverySignalHeardIsKernelStamped()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var settings = host.Connection.Neuron<IApp>("apps/settings");
        var heard = new List<AppChanged>();
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var stream = host.Connection.On<AppChanged>(
            settings,
            () =>
            {
                subscribed.TrySetResult();
                return Task.CompletedTask;
            },
            ct);

        await using var enumerator = stream.GetAsyncEnumerator(ct);
        var moveNext = enumerator.MoveNextAsync().AsTask();
        await subscribed.Task.WaitAsync(ct);

        using var post = await host.Os.PostAsJsonAsync(
            $"/brains/{host.Scope}/apps/settings/request",
            new { operation = "open", input = "{}" },
            ct);
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);

        Assert.True(await moveNext.WaitAsync(ct));
        heard.Add(enumerator.Current);

        Assert.NotEmpty(heard);
        foreach (var signal in heard)
        {
            Assert.Equal("settings", signal.Name);
            var provenance = host.Connection.Provenance(signal);
            Assert.NotNull(provenance);
            Assert.True(Enum.IsDefined(provenance.StampedBy));
            Assert.True(provenance.StampedBy is TrustedEdge.AppProxy or TrustedEdge.AuthenticatedHttp);
            Assert.False(
                provenance.Kind == CallerKind.User
                && string.Equals(provenance.PrincipalId, host.Scope, StringComparison.Ordinal)
                && !string.Equals(provenance.PrincipalId, "owner", StringComparison.Ordinal));
            Assert.Equal(CallerKind.App, provenance.Kind);
            Assert.Equal(TrustedEdge.AppProxy, provenance.StampedBy);
            Assert.Equal($"{host.Scope}/apps/settings", provenance.AppId);
        }
    }

    [Fact]
    public async Task ARejectedRequestIsHeardAndChangesNothing()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var settings = host.Connection.Neuron<IApp>("apps/settings");

        using var beforeResponse = await host.Os.GetAsync($"/brains/{host.Scope}/apps/settings", ct);
        beforeResponse.EnsureSuccessStatusCode();
        var beforeBody = await beforeResponse.Content.ReadAsStringAsync(ct);

        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = host.Connection.On<Rejected>(
            settings,
            () =>
            {
                subscribed.TrySetResult();
                return Task.CompletedTask;
            },
            ct);

        await using var enumerator = stream.GetAsyncEnumerator(ct);
        var moveNext = enumerator.MoveNextAsync().AsTask();
        await subscribed.Task.WaitAsync(ct);

        using var post = await host.Os.PostAsJsonAsync(
            $"/brains/{host.Scope}/apps/settings/request",
            new { operation = "unknown-operation", input = "{}" },
            ct);
        Assert.Equal(HttpStatusCode.Conflict, post.StatusCode);

        using var conflictJson = await JsonDocument.ParseAsync(await post.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        Assert.Equal("Request", conflictJson.RootElement.GetProperty("operation").GetString());
        Assert.Contains(
            conflictJson.RootElement.GetProperty("rules").EnumerateArray().Select(item => item.GetString()),
            rule => rule == "apps.operation");

        Assert.True(await moveNext.WaitAsync(ct));
        var rejected = enumerator.Current;
        Assert.NotNull(rejected);
        Assert.Equal("Request", rejected.Operation);
        Assert.Contains("apps.operation", rejected.Rules);
        Assert.NotEmpty(rejected.Messages);

        using var afterResponse = await host.Os.GetAsync($"/brains/{host.Scope}/apps/settings", ct);
        afterResponse.EnsureSuccessStatusCode();
        var afterBody = await afterResponse.Content.ReadAsStringAsync(ct);
        Assert.Equal(beforeBody, afterBody);
    }
}
