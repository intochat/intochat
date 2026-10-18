using System.Net.Http.Json;
using System.Text.Json;

namespace IntoChat.OS.Kernel.Tests.Integration;

// The operating system's composition, minus its container-backed modules, boots inside the test process.
public sealed class OsTestBrainFacts(OsHost host)
{
    [Fact]
    public async Task TheOperatingSystemStartsInProcessAndRegistersAPerson()
    {
        var ct = TestContext.Current.CancellationToken;

        using var ada = await host.PersonAsync("ada", ct);

        var session = await ada.Http.GetFromJsonAsync<JsonElement>("/identity/session", ct);
        Assert.Equal(ada.PrincipalId, session.GetProperty("principalId").GetString());
        Assert.Equal(ada.BrainId, session.GetProperty("brainId").GetString());
    }
}
