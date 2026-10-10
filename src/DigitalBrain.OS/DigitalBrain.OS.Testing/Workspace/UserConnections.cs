using System.Text.Json;

using System.Net.Http.Json;

namespace DigitalBrain.OS.Testing.Workspace;

// Explicit test-user authorization against the real HTTP edge. Never a production seed/fallback.
public static class UserConnections
{
    // Reference hosts run the Open posture: the owner connects the brain's database as a postgres connection named "supabase".
    public static async Task AuthorizeDatabaseAsync(ITestBrain brain, string brainId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        var connection = await brain.GetConnectionStringAsync("supabase-database", cancellationToken)
            ?? throw new InvalidOperationException("The reference fixture has no test database.");
        using var response = await brain.Owner.Http.PostAsJsonAsync($"/brains/{Uri.EscapeDataString(brainId)}/connections/supabase/connect", new
        {
            connectorId = "postgres",
            authMethodId = "connection-string",
            value = connection,
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var row = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (row.GetProperty("status").GetString() != "Connected")
        { throw new InvalidOperationException("The test user's database connection is unavailable."); }
    }
}
