using System.Net.Http.Json;
using System.Text.Json;

namespace IntoChat.OS.Kernel.Testing.Workspace;

// Explicit test-user authorization against the real HTTP edge. Never a production seed/fallback.
public static class UserConnections
{
    // Reference hosts run the Open posture: the owner connects the brain's database as a postgres connection named "supabase".
    public static async Task AuthorizeDatabaseAsync(ITestBrain brain, string brainId, CancellationToken cancellationToken)
        => await AuthorizeDatabaseAsync(brain, brain.Owner.Http, brainId, cancellationToken);

    public static async Task AuthorizeDatabaseAsync(ITestBrain brain, Person person, CancellationToken cancellationToken)
        => await AuthorizeDatabaseAsync(brain, person.Http, person.BrainId, cancellationToken);

    private static async Task AuthorizeDatabaseAsync(ITestBrain brain, HttpClient client, string brainId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        var connection = await brain.GetConnectionStringAsync("supabase-database", cancellationToken)
            ?? throw new InvalidOperationException("The reference fixture has no test database.");
        using var response = await client.PostAsJsonAsync($"/brains/{Uri.EscapeDataString(brainId)}/connections/supabase/connect", new
        {
            connectorId = "postgres",
            authMethodId = "connection-string",
            value = connection,
        }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"The test user's database connection failed ({(int)response.StatusCode}): {body}");
        }
        var row = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (row.GetProperty("status").GetString() != "Connected")
        { throw new InvalidOperationException("The test user's database connection is unavailable."); }
    }
}
