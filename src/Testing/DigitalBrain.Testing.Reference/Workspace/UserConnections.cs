using DigitalBrain.Framework;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DigitalBrain.Testing.E2E.Workspace;

// Explicit test-user authorization against the real HTTP edge. Never a production seed/fallback.
public static class UserConnections
{
    private static readonly ConditionalWeakTable<E2EBrain, FixtureIdentity> Identities = new();
    public static async Task AuthorizeSupabaseAsync(E2EBrain brain, string brainId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        using var session = await brain.HttpClient.GetAsync("/identity/session", cancellationToken);
        session.EnsureSuccessStatusCode();
        // Reference hosts explicitly use Open posture. Without a cookie, the HTTP edge is the
        // synthetic owner; cookie-authenticated fixtures use the actual session identity.
        var identity = session.StatusCode == HttpStatusCode.NoContent ? new FixtureIdentity("owner", "owner")
            : await session.Content.ReadFromJsonAsync<FixtureIdentity>(cancellationToken)
                ?? throw new InvalidOperationException("The test fixture session has no identity.");
        Identities.Remove(brain);
        Identities.Add(brain, identity);
        var connection = await brain.GetConnectionStringAsync("supabase-database", cancellationToken)
            ?? throw new InvalidOperationException("The reference fixture has no Supabase test database.");
        using var response = await brain.HttpClient.PostAsJsonAsync($"/brains/{Uri.EscapeDataString(brainId)}/connections/connect", new
        {
            connectorId = "supabase",
            connectionId = "supabase",
            authMethodId = "connection-string",
            value = connection,
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var row = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (row.GetProperty("status").GetString() != "Configured")
        { throw new InvalidOperationException("The test user's Supabase connection is unavailable."); }
    }

    // Direct grain assertions have no HTTP middleware; match the reference host's authenticated
    // owner and requested brain explicitly, then restore the surrounding test context.
    public static IDisposable UseBrain(E2EBrain brain, string brainId)
    {
        var identity = Identities.TryGetValue(brain, out var known) ? known
            : throw new InvalidOperationException("Authorize the fixture connection before using its direct caller scope.");
        var previous = Orleans.Runtime.RequestContext.Get(CallerContextStamper.RequestContextKey);
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = identity.PrincipalId,
            AccountId = identity.AccountId,
            BrainId = brainId,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        });
        return new CallerScope(previous);
    }

    private sealed record FixtureIdentity(string PrincipalId, string AccountId);

    private sealed class CallerScope(object? previous) : IDisposable
    {
        public void Dispose()
        {
            if (previous is null) { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
            else { Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, previous); }
        }
    }
}
