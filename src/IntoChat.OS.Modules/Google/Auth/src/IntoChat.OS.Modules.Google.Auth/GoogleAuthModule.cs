using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Metering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Google.Auth;

// Google sign-in shared by Gmail and Drive: the one google-oauth integration, its client secrets,
// the token exchange and refresh operations and the metered Google HTTP client.
[Module(ModuleName)]
[Integration(GoogleAuth.Integration, "Google OAuth")]
[Secret("ClientId", Integration = GoogleAuth.Integration)]
[Secret("ClientSecret", Integration = GoogleAuth.Integration)]
[ModulePriceList(typeof(GooglePriceList))]
[ExternalOperation(GoogleAuthOperations.CodeExchange, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleAuthOperations.Refresh, GoogleAuth.Integration, "request")]
public sealed partial class GoogleAuthModule : Module<GoogleAuthModuleOptions>
{
    // R4-04: what the module needs from its host. It has no neurons; its registrations stay in Configure until the Framework
    // leaves (06-08).
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<GoogleAuthModuleOptions>()],
        Secrets: []);

    public const string ModuleName = "google-auth";

    partial void OnConfigure(IModuleServices services)
    {
        var collection = services.Services;
        collection.TryAddSingleton<GoogleAuthRegistration>();
        collection.AddMeteredHttpClient(GoogleHttp.ClientName).ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
    }
}

public sealed record GoogleAuthModuleOptions : IModuleOptions
{
    public void Validate() { }
}
