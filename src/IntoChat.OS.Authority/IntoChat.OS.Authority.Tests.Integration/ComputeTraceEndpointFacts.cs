using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Authority.Compute;
using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration;

public sealed class ComputeTraceEndpointFacts(AuthorityHost host)
{
    [Fact]
    public async Task TraceRouteExposesBoundedDetailAndHonestHistoricalAbsence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync("trace-owner", ct);
        using var caller = person.AsCaller();
        var meter = host.Brain.Services.GetRequiredService<ICostMeter>();
        var trace = TraceContext.Current!.TraceId;
        await meter.RecordAsync(CostKind.Script, "cpu-ms", 1, "ms", Funding.Local, "cpu", ct);
        await meter.RecordAsync(CostKind.Model, "tokens", 1, "token", Funding.OwnConnection, "model", ct);
        var page = await person.Http.GetFromJsonAsync<JsonElement>($"/brains/{person.BrainId}/compute/traces/{trace}?take=1", ct);
        Assert.Equal("complete", page.GetProperty("status").GetString());
        Assert.Single(page.GetProperty("rows").EnumerateArray());
        Assert.Equal(2, page.GetProperty("unpricedCount").GetInt64());
        Assert.Equal(0, page.GetProperty("chargedMicros").GetInt64());
        Assert.NotNull(page.GetProperty("continuationToken").GetString());
        using var invalid = await person.Http.GetAsync($"/brains/{person.BrainId}/compute/traces/{trace}?take=501", ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var missing = await person.Http.GetFromJsonAsync<JsonElement>($"/brains/{person.BrainId}/compute/traces/historical", ct);
        Assert.Equal("detailunavailable", missing.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AnotherBrainCannotReadTheTraceOrUseItsContinuation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var owner = await host.PersonAsync("trace-owner", ct);
        using var other = await host.PersonAsync("trace-other", ct);
        string trace;
        using (owner.AsCaller())
        {
            trace = TraceContext.Current!.TraceId;
            await host.Brain.Services.GetRequiredService<ICostMeter>().RecordAsync(CostKind.Script, "cpu", 1, "ms", Funding.Local, "one", ct);
        }
        using var forbidden = await other.Http.GetAsync($"/brains/{owner.BrainId}/compute/traces/{trace}", ct);
        Assert.False(forbidden.IsSuccessStatusCode);
        var missing = await other.Http.GetFromJsonAsync<JsonElement>($"/brains/{other.BrainId}/compute/traces/{trace}", ct);
        Assert.Empty(missing.GetProperty("rows").EnumerateArray());
    }
}
