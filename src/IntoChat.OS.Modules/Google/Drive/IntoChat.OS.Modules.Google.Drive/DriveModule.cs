using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Integrations;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Google.Auth;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Google.Drive;

// Google Drive as read-only storage, signed in through google-auth's Google OAuth integration.
// Its own connection keeps existing Gmail grants unchanged: adding a scope to them would force re-consent.
[Module(ModuleName)]
[UsesIntegration(GoogleAuth.Integration)]
[ModulePriceList(typeof(GooglePriceList))]
[ExternalOperation(GoogleDriveOperations.About, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.List, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.Metadata, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.Media, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.Export, GoogleAuth.Integration, "request")]
public sealed partial class DriveModule : Module<DriveModuleOptions>
{
    public const string ModuleName = DriveScopes.Connector;

    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<DriveModuleOptions>()],
        Secrets: []);

    // Registration still goes through ConnectorDefinition until Plan 05-07 moves Provides onto the manifest.
    public static ConnectorDefinition Connector { get; } = new(DriveScopes.Connector, "Google Drive", [GoogleAuth.Integration], [GoogleAuth.Integration])
    {
        Neurons = [typeof(IStorage)],
        Capabilities = [IStorage.Capability],
        OAuthMethods = [GoogleAuth.OAuth(GoogleAuth.Integration, [DriveScopes.Read])],
    };

    partial void OnConfigure(IModuleServices services)
    {
        services.Services.TryAddSingleton(services.Options<DriveModuleOptions>());
        GoogleAuth.Require(services.Services, ModuleName);
    }
}

public static class GoogleDriveOperations
{
    public const string About = "google.drive.about.get";
    public const string List = "google.drive.files.list";
    public const string Metadata = "google.drive.files.get.metadata";
    public const string Media = "google.drive.files.get.media";
    public const string Export = "google.drive.files.export";
}

internal static class DriveScopes
{
    internal const string Connector = "google-drive";
    internal const string Read = "https://www.googleapis.com/auth/drive.readonly";
}

[GenerateSerializer, Alias("db.google-drive.unavailable")]
public sealed class DriveUnavailableException(string message) : InvalidOperationException(message);
