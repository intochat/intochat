using System.Collections.Concurrent;
using System.Net;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Sdk.Compute;
using IntoChat.OS.Sdk.Http.Compute;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Sdk.Http.Tests.Unit;

public sealed class ExternalCallHandlerFacts
{
    [Theory]
    [InlineData(200, ExternalCallOutcomeKind.Succeeded)]
    [InlineData(400, ExternalCallOutcomeKind.HttpFailed)]
    [InlineData(500, ExternalCallOutcomeKind.HttpFailed)]
    public async Task EachResponseHasOneAttemptAndLocalAccounting(int status, ExternalCallOutcomeKind outcome)
    {
        var meter = new Meter();
        var transport = new Transport((request, _) =>
        {
            meter.Events.Enqueue("transport");
            Assert.False(request.Headers.Contains("x-intochatos-funding"));
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        });
        using var client = Client(meter, transport);
        using var request = Request();
        request.Headers.Add("x-intochatos-funding", "integration");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(["begin", "mark", "transport", "complete"], meter.Events.ToArray());
        Assert.Equal(outcome, Assert.Single(meter.Outcomes).Kind);
        Assert.Equal(status, meter.Outcomes.Single().HttpStatusCode);
        Assert.Equal(ExternalAccountingStatus.Settled, response.GetExternalAccounting()!.Status);
        Assert.Equal(Funding.OwnConnection, Assert.Single(meter.Requests).Funding);
        Assert.DoesNotContain("secret", meter.Requests.Single().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessSurvivesCompletionFailureAndUsesIndependentCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        var meter = new Meter { FailComplete = true };
        using var client = Client(meter, new Transport((_, _) =>
        {
            cancelled.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        using var request = Request();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancelled.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExternalAccountingStatus.Pending, response.GetExternalAccounting()!.Status);
        Assert.False(meter.CompletionWasCancelled);
    }

    [Fact]
    public async Task TransportFailureSurvivesAccountingFailure()
    {
        var failure = new HttpRequestException("DNS failed");
        var meter = new Meter { FailComplete = true };
        using var client = Client(meter, new Transport((_, _) => throw failure));
        using var request = Request();
        Assert.Same(failure, await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, TestContext.Current.CancellationToken)));
        Assert.Equal(ExternalCallOutcomeKind.TransportFailed, Assert.Single(meter.Outcomes).Kind);
    }

    [Fact]
    public async Task CompletionDeadlineDoesNotDependOnMeterCooperation()
    {
        var clock = new FakeTimeProvider();
        var blocked = new TaskCompletionSource<ExternalCallCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        var meter = new Meter { CompletionTask = blocked.Task };
        using var client = new HttpClient(new ExternalCallHandler(meter,
            new ModulePriceListRegistry([new("test", typeof(Prices), [new("test.read", "test-api", "call")])]), new Context(), clock)
        { InnerHandler = new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))) });
        using var request = Request();
        var sending = client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Single(meter.Outcomes);
        clock.Advance(TimeSpan.FromSeconds(11));
        using var response = await sending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAccountingStatus.Pending, response.GetExternalAccounting()!.Status);
        blocked.SetResult(new(response.GetExternalAccounting()!.Handle, ExternalAccountingStatus.Settled));
    }

    [Fact]
    public async Task AnOuterRedirectPolicyCountsEachBoundedDispatch()
    {
        var meter = new Meter();
        var transport = new Transport((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://example.test/next");
            return Task.FromResult(response);
        });
        var services = Services(meter);
        services.AddMeteredHttpClient("redirect").AddHttpMessageHandler(() => new RedirectOnce())
            .ConfigurePrimaryHttpMessageHandler(() => transport);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("redirect");
        using var request = Request();
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(2, transport.Count);
        Assert.Equal(2, meter.Requests.Select(call => call.CostId).Distinct().Count());
        Assert.All(meter.Requests, call => Assert.Equal("invocation", call.InvocationId));
    }

    [Theory]
    [InlineData("provider-123", "provider-123")]
    [InlineData("https://provider.test/?token=secret", null)]
    public async Task ProviderCorrelationAllowsOnlyBoundedOpaqueTokens(string id, string? expected)
    {
        var meter = new Meter();
        using var client = Client(meter, new Transport((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("x-request-id", id);
            return Task.FromResult(response);
        }));
        using var request = Request();
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(expected, Assert.Single(meter.Outcomes).ProviderRequestId);
    }

    [Fact]
    public async Task DeniedAdmissionAndMissingMetadataNeverDispatch()
    {
        var transport = new Transport((_, _) => throw new InvalidOperationException("must not dispatch"));
        var meter = new Meter { FailBegin = true };
        using var client = Client(meter, transport);
        using var missing = new HttpRequestMessage(HttpMethod.Get, "https://example.test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(missing, TestContext.Current.CancellationToken));
        Assert.Empty(meter.Requests);
        using var request = Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task CancellationAfterAdmissionButBeforeTransportReleasesReservation()
    {
        using var cancelled = new CancellationTokenSource();
        var meter = new Meter { OnMark = cancelled.Cancel };
        var transport = new Transport((_, _) => throw new InvalidOperationException("must not dispatch"));
        using var client = Client(meter, transport);
        using var request = Request();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, cancelled.Token));
        Assert.Equal(0, transport.Count);
        Assert.Equal(ExternalCallOutcomeKind.CancelledBeforeDispatch, Assert.Single(meter.Outcomes).Kind);
        Assert.False(meter.CompletionWasCancelled);
    }

    [Fact]
    public async Task CancellationInTransportRecordsUncertainDispatch()
    {
        using var cancelled = new CancellationTokenSource();
        var meter = new Meter();
        var transport = new Transport((_, _) =>
        {
            cancelled.Cancel();
            throw new OperationCanceledException(cancelled.Token);
        });
        using var client = Client(meter, transport);
        using var request = Request();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, cancelled.Token));
        Assert.Equal(1, transport.Count);
        Assert.Equal(ExternalCallOutcomeKind.CancelledAfterDispatch, Assert.Single(meter.Outcomes).Kind);
    }

    [Fact]
    public async Task FactoryInstallsAttemptMeterBelowRetries()
    {
        var meter = new Meter();
        var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var services = Services(meter);
        services.AddMeteredHttpClient("test").AddHttpMessageHandler(() => new RetryTwice())
            .ConfigurePrimaryHttpMessageHandler(() => transport);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");
        using var request = Request();
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(3, transport.Count);
        Assert.Equal(3, meter.Requests.Count);
        Assert.Equal(3, meter.Requests.Select(call => call.CostId).Distinct().Count());
        Assert.All(meter.Requests, call => Assert.Equal("invocation", call.InvocationId));
    }

    [Fact]
    public async Task AnEarlierFactoryFilterCannotPutRetriesInsideTheAttemptBoundary()
    {
        var meter = new Meter();
        var transport = new Transport((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var services = Services(meter);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, RetryFilter>();
        services.AddMeteredHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => transport);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");
        using var request = Request();
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(3, transport.Count);
        Assert.Equal(3, meter.Requests.Count);
    }

    [Fact]
    public void FactoryDisablesAutomaticRedirectsAndRejectsOpaqueTransports()
    {
        var services = Services(new Meter());
        var primary = new HttpClientHandler { AllowAutoRedirect = true };
        services.AddMeteredHttpClient("known").ConfigurePrimaryHttpMessageHandler(() => primary);
        services.AddMeteredHttpClient("opaque").ConfigurePrimaryHttpMessageHandler(() => new OpaqueTransport());
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("known");
        Assert.False(primary.AllowAutoRedirect);
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IHttpClientFactory>().CreateClient("opaque"));
    }

    [Fact]
    public async Task PooledHandlerReadsTheCurrentContextForEachConcurrentRequest()
    {
        var accessor = new Context();
        var meter = new Meter();
        using var barrier = new SemaphoreSlim(0);
        var services = Services(meter, accessor);
        services.AddMeteredHttpClient("shared").ConfigurePrimaryHttpMessageHandler(() => new Transport(async (_, ct) =>
        {
            await barrier.WaitAsync(ct);
            return new(HttpStatusCode.OK);
        }));
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        async Task Send(string trace)
        {
            accessor.Current.Value = trace;
            using var client = factory.CreateClient("shared");
            using var request = Request();
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        }
        var first = Send("brain-one");
        var second = Send("brain-two");
        barrier.Release(2);
        await Task.WhenAll(first, second);
        Assert.Equal(["brain-one", "brain-two"], accessor.Seen.Order().ToArray());
        Assert.Equal(2, meter.Requests.Select(call => call.CostId).Distinct().Count());
    }

    private static ServiceCollection Services(Meter meter, Context? context = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICostMeter>(meter);
        services.AddSingleton<IExternalCallContextAccessor>(context ?? new());
        services.AddModulePriceList<Prices>("test", [new("test.read", "test-api", "call")]);
        return services;
    }

    private static HttpClient Client(Meter meter, HttpMessageHandler transport) => new(new ExternalCallHandler(
        meter, new ModulePriceListRegistry([new("test", typeof(Prices), [new("test.read", "test-api", "call")])]), new Context())
    { InnerHandler = transport });

    private static HttpRequestMessage Request()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/path?secret=private");
        request.SetExternalCall(new("test.read", Funding.OwnConnection, null, "invocation"));
        return request;
    }

    private sealed class Context : IExternalCallContextAccessor
    {
        public AsyncLocal<string?> Current { get; } = new();
        public ConcurrentQueue<string> Seen { get; } = new();
        public string RequireTraceId() { var trace = Current.Value ?? "trace"; Seen.Enqueue(trace); return trace; }
    }

    private sealed class Prices : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => 10;
    }

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler, ISingleAttemptHttpTransport
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return send(request, cancellationToken); }
    }

    private sealed class OpaqueTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class RetryTwice : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            { using var response = await base.SendAsync(request, cancellationToken); }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RetryFilter : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        { next(builder); builder.AdditionalHandlers.Add(new RetryTwice()); };
    }

    private sealed class RedirectOnce : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var first = await base.SendAsync(request, cancellationToken);
            request.RequestUri = first.Headers.Location;
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Meter : ICostMeter
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentQueue<ExternalCallRequest> Requests { get; } = new();
        public ConcurrentQueue<ExternalCallOutcome> Outcomes { get; } = new();
        public bool FailComplete { get; init; }
        public bool FailBegin { get; init; }
        public bool CompletionWasCancelled { get; private set; }
        public Action? OnMark { get; init; }
        public Task<ExternalCallCompletion>? CompletionTask { get; init; }
        public ValueTask<ExternalCallHandle> BeginExternalCallAsync(ExternalCallRequest request, CancellationToken cancellationToken)
        {
            Events.Enqueue("begin"); Requests.Enqueue(request);
            if (FailBegin) { throw new InvalidOperationException("denied"); }
            return ValueTask.FromResult(new ExternalCallHandle(request.CostId));
        }
        public ValueTask MarkExternalCallDispatchStartedAsync(ExternalCallHandle handle, CancellationToken cancellationToken)
        { Events.Enqueue("mark"); OnMark?.Invoke(); return ValueTask.CompletedTask; }
        public ValueTask<ExternalCallCompletion> CompleteExternalCallAsync(ExternalCallHandle handle, ExternalCallOutcome outcome, CancellationToken cancellationToken)
        {
            Events.Enqueue("complete"); Outcomes.Enqueue(outcome); CompletionWasCancelled = cancellationToken.IsCancellationRequested;
            if (FailComplete) { throw new IOException("storage unavailable"); }
            if (CompletionTask is not null) { return new(CompletionTask); }
            return ValueTask.FromResult(new ExternalCallCompletion(handle, ExternalAccountingStatus.Settled));
        }
        public ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding, string costId,
            CancellationToken cancellationToken, CostSubject? subject = null) => throw new NotSupportedException();
    }
}
