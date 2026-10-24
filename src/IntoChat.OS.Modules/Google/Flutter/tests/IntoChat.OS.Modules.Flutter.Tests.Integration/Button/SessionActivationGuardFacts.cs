using IntoChat.OS.Apps;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Framework.Platform.Connections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Button;

// A button that switches the account, reconnects the shell to a new root, or signs an existing connection in again
// acts on the person's session in one click, so only the shipped app that owns that control may set one.
public sealed class SessionActivationGuardFacts(FlutterHost host)
{
    [Fact]
    public async Task OnlyTheOwningShippedAppsSetSessionActivations()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        var button = host.Brain.Get<IButton>(person.Key("apps/session-button"));
        var brain = person.BrainId;
        var account = """{"kind":"account"}""";
        var reconnect = $$"""{"kind":"reconnect","brainId":"{{brain}}","application":"desktop","operation":"layout","input":"classic"}""";
        var relogin = $$"""{"kind":"connections","brainId":"{{brain}}","connector":"gmail","connection":"conn-1"}""";
        var connect = $$"""{"kind":"connections","brainId":"{{brain}}","connector":"gmail"}""";

        async Task<IApp> Shipped(string name)
        {
            var app = host.Brain.Get<IApp>(person.Key("apps/" + name));
            // As the person: a caller left over from an earlier check is an app, which may not record an origin.
            using var _ = person.AsCaller();
            await app.Create(new AppManifest(name, name, [], [], []), "spec");
            await app.RecordOrigin(new(ShippedApps.Publisher, new AppSource.Shipped(), name, "1.0.0"));
            return app;
        }
        async Task Refused(IDisposable caller, string activation)
        {
            using (caller) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => button.SetActivation("Go", activation)); }
        }
        async Task Allowed(IDisposable caller, string activation)
        {
            using (caller) { await button.SetActivation("Go", activation); }
            Assert.Equal(activation, (await button.Read()).Activation);
        }

        await host.Brain.Get<IApp>(person.Key("apps/notes")).Create(new AppManifest("Notes", "Notes", [], [], []), "spec");
        foreach (var activation in new[] { account, reconnect, relogin, """{"kind":"Account"}""" })
        {
            await Refused(person.AsCaller(), activation);
            await Refused(person.AsApp(person.Key("apps/notes")), activation);
        }
        Assert.Null((await button.Read()).Activation);

        foreach (var name in new[] { "circle", "desktop", "workspace", "settings", "apps" }) { await Shipped(name); }
        // Each kind belongs to its own apps only.
        await Allowed(person.AsApp(person.Key("apps/circle")), account);
        await Refused(person.AsApp(person.Key("apps/apps")), account);
        await Allowed(person.AsApp(person.Key("apps/desktop")), reconnect);
        await Allowed(person.AsApp(person.Key("apps/workspace")), reconnect);
        await Refused(person.AsApp(person.Key("apps/settings")), reconnect);
        await Allowed(person.AsApp(person.Key("apps/settings")), relogin);
        await Allowed(person.AsApp(person.Key("apps/apps")), relogin);
        await Refused(person.AsApp(person.Key("apps/desktop")), relogin);
        // Never for another brain.
        await Refused(person.AsApp(person.Key("apps/desktop")), reconnect.Replace(brain, "another-brain", StringComparison.Ordinal));
        await Refused(person.AsApp(person.Key("apps/apps")), relogin.Replace(brain, "another-brain", StringComparison.Ordinal));
        await Refused(person.AsApp(person.Key("apps/circle")), """{"kind":"account","brainId":"another-brain"}""");
        // A first Connect (no connection named) starts a new sign-in and stays open to any app.
        await Allowed(person.AsApp(person.Key("apps/notes")), connect);

        // An edited copy loses the authority.
        await host.Brain.Get<IApp>(person.Key("apps/desktop")).SaveManifest(new AppManifest("desktop", "edited", [], [], []), Guid.NewGuid());
        await Refused(person.AsApp(person.Key("apps/desktop")), reconnect);
    }

    // The shell signs in the connection `connection` names when it is well formed (lower-case, at most 128), else the
    // one named like the connector: so a connections activation reaches an existing connection however it is spelled.
    [Fact]
    public async Task AnExistingConnectionIsGuardedHoweverTheActivationNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var person = await host.PersonAsync(ct: ct);
        var button = host.Brain.Get<IButton>(person.Key("apps/connect-button"));
        var brain = person.BrainId;
        await host.Brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IConnectionIndex>(brain).Track(ConnectionKeys.For(brain, "gmail", "gmail"));

        async Task Shipped(string name)
        {
            var app = host.Brain.Get<IApp>(person.Key("apps/" + name));
            // As the person: a caller left over from an earlier check is an app, which may not record an origin.
            using var _ = person.AsCaller();
            await app.Create(new AppManifest(name, name, [], [], []), "spec");
            await app.RecordOrigin(new(ShippedApps.Publisher, new AppSource.Shipped(), name, "1.0.0"));
        }
        await host.Brain.Get<IApp>(person.Key("apps/notes")).Create(new AppManifest("Notes", "Notes", [], [], []), "spec");
        foreach (var name in new[] { "settings", "apps", "desktop" }) { await Shipped(name); }

        async Task Refused(string name, string activation)
        {
            using (person.AsApp(person.Key("apps/" + name)))
            { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => button.SetActivation("Go", activation)); }
        }
        async Task Allowed(string name, string activation)
        {
            using (person.AsApp(person.Key("apps/" + name))) { await button.SetActivation("Go", activation); }
            Assert.Equal(activation, (await button.Read()).Activation);
        }

        string[] variants =
        [
            $$"""{"kind":"connections","brainId":"{{brain}}","connector":"gmail"}""",
            $$"""{"kind":"connections","brainId":"{{brain}}","connector":"gmail","connection":""}""",
            $$"""{"kind":"connections","brainId":"{{brain}}","connector":"gmail","connection":5}""",
            $$"""{"kind":"connections","brainId":"{{brain}}","connector":"gmail","connection":"GMAIL"}""",
            $$"""{"kind":"connections","brainId":"{{brain}}","connector":"gmail","connection":null}""",
            """{"kind":"connections","connector":"gmail"}""",
        ];
        foreach (var activation in variants)
        {
            await Refused("notes", activation);
            await Refused("desktop", activation);
            await Allowed("settings", activation);
            await Allowed("apps", activation);
        }
        // Another brain is never named, by any app.
        var other = variants[0].Replace(brain, "another-brain", StringComparison.Ordinal);
        await Refused("settings", other);
        await Refused("notes", other);
        // A connector with no connection yet is a first Connect and stays open to any app.
        await Allowed("notes", $$"""{"kind":"connections","brainId":"{{brain}}","connector":"salesforce"}""");
    }
}
