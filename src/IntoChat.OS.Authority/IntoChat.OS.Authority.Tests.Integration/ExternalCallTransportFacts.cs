using System.Collections.Concurrent;
using System.Net;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Sdk.Http.Compute;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Authority.Tests.Integration;

[Trait("host", "own")]
public sealed class ExternalCallTransportFacts
{
    [Fact(Timeout = 60_000)]
    public async Task CancellationInOneBrainDoesNotMoveAnotherBrainsChargeOrPendingAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet("/cancel", async (HttpContext context) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { }
            return Results.Empty;
        });
        app.MapGet("/success", () => Results.Ok());
        await app.StartAsync(ct);
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.SuccessfulResponse))
            .ConfigureServices(services => services.AddMeteredHttpClient("provider")).StartAsync(ct);
        using var first = await brain.PersonAsync("cancelled", ct);
        using var second = await brain.PersonAsync("successful", ct);
        var factory = brain.Services.GetRequiredService<IHttpClientFactory>();
        async Task<string> Call(Person person, bool cancel)
        {
            using var caller = person.AsCaller();
            var trace = TraceContext.Current!.TraceId;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var client = factory.CreateClient("provider");
            using var request = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single() + (cancel ? "/cancel" : "/success"));
            request.SetExternalCall(new("test.read", Funding.Integration, null, "same-invocation"));
            var sending = client.SendAsync(request, cancellation.Token);
            if (cancel)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            }
            else
            {
                using var response = await sending;
                Assert.Equal(ExternalAccountingStatus.Settled, response.GetExternalAccounting()!.Status);
            }
            return trace;
        }
        var traces = await Task.WhenAll(Call(first, true), Call(second, false));
        var cancelled = await brain.Platform.Compute(first.BrainId).ReadTrace(traces[0], 100, null);
        Assert.Equal(ExternalCallState.Uncertain, Assert.Single(cancelled.Rows).AttemptState);
        Assert.Equal(1, cancelled.PendingCount);
        Assert.Equal(0, cancelled.ChargedMicros);
        Assert.Equal(0, await brain.Platform.Ledger(first.AccountId).Balance());
        var succeeded = await brain.Platform.Compute(second.BrainId).ReadTrace(traces[1], 100, null);
        Assert.Equal(ExternalCallState.Settled, Assert.Single(succeeded.Rows).AttemptState);
        Assert.Equal(0, succeeded.PendingCount);
        Assert.Equal(125, succeeded.ChargedMicros);
        Assert.Equal(-125, await brain.Platform.Ledger(second.AccountId).Balance());
    }

    [Fact(Timeout = 60_000)]
    public async Task RedirectIsOneRealAttemptAndPooledClientKeepsBrainAttribution()
    {
        var ct = TestContext.Current.CancellationToken;
        var received = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet("/{path}", (HttpContext context, string path) =>
        {
            Assert.False(context.Request.Headers.ContainsKey(MeteredHttpHandler.IntegrationFundingHeader));
            received.Enqueue(path);
            return path == "redirect" ? Results.Redirect("/target") : Results.Ok();
        });
        await app.StartAsync(ct);
        await using var brain = await ExternalCallFacts.Create(new(100, ExternalChargePolicy.EveryDispatchedAttempt))
            .ConfigureServices(services => services.AddMeteredHttpClient("provider")).StartAsync(ct);
        using var first = await brain.PersonAsync("first", ct);
        using var second = await brain.PersonAsync("second", ct);
        var factory = brain.Services.GetRequiredService<IHttpClientFactory>();

        async Task<string> Call(Person person, string path)
        {
            using var caller = person.AsCaller();
            var trace = TraceContext.Current!.TraceId;
            using var client = factory.CreateClient("provider");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{app.Urls.Single()}/{path}?secret=not-recorded");
            request.Headers.Add(MeteredHttpHandler.IntegrationFundingHeader, "integration");
            request.SetExternalCall(new("test.read", Funding.Integration, null, "same-invocation"));
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(path == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(ExternalAccountingStatus.Settled, response.GetExternalAccounting()!.Status);
            return trace;
        }
        var traces = await Task.WhenAll(Call(first, "redirect"), Call(second, "success"));
        Assert.Equal(["redirect", "success"], received.Order().ToArray());
        Assert.Equal(-125, await brain.Platform.Ledger(first.AccountId).Balance());
        Assert.Equal(-125, await brain.Platform.Ledger(second.AccountId).Balance());
        for (var index = 0; index < 2; index++)
        {
            var person = index == 0 ? first : second;
            var page = await brain.Platform.Compute(person.BrainId).ReadTrace(traces[index], 100, null);
            var cost = Assert.Single(page.Rows);
            Assert.Equal(CostKind.ExternalProvider, cost.Kind);
            Assert.Equal("test.read", cost.Item);
            Assert.Equal("same-invocation", cost.InvocationId);
            Assert.Equal(125, page.ChargedMicros);
            Assert.DoesNotContain("secret", cost.ToString(), StringComparison.Ordinal);
            var other = await brain.Platform.Compute(person.BrainId).ReadTrace(traces[1 - index], 100, null);
            Assert.Empty(other.Rows);
        }
    }
}
