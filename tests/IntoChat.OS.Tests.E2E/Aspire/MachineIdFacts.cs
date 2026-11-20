using System.Net;
using System.Text.Json;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

// The booted OS reports one machine id: the host's MachineId, the same one the Windows module addresses its device by.
[Collection(OsAppHostCollection.Name)]
public sealed class MachineIdFacts(OsAppHostFixture host)
{
    [Fact]
    public async Task TheDeviceReportsOneStableMachineId()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The device endpoint reports a machine only on Windows.");
        var ct = TestContext.Current.CancellationToken;
        await host.Healthy(ct);

        var first = await DeviceMachineId(ct);
        var second = await DeviceMachineId(ct);

        Assert.True(Guid.TryParseExact(first, "N", out _), $"'{first}' is not a machine id.");
        Assert.Equal(first, second);
        // The fixture sets no IntoChatOS:Machine:IdFile, so the kernel keeps the id where installs always had it.
        Assert.Equal(first, File.ReadAllText(IntoChat.OS.Kernel.Composition.Machine.MachineHosting.IdFile(null)).Trim());
    }

    private async Task<string> DeviceMachineId(CancellationToken ct)
    {
        using var response = await host.Os.GetAsync($"/brains/{host.Scope}/windows/device", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return body.RootElement.GetProperty("machineId").GetString() ?? "";
    }
}
