using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Connections.Platform.Integrations;
using IntoChat.OS.Integrations;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Google.Auth;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Google.Drive;

// Google Drive storage, signed in through google-auth's Google OAuth integration.
// Its own connection keeps existing Gmail grants unchanged: adding a scope to them would force re-consent.
[Module(ModuleName)]
[UsesIntegration(GoogleAuth.Integration)]
[ModulePriceList(typeof(GooglePriceList))]
[ExternalOperation(GoogleDriveOperations.About, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.List, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.Metadata, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.Media, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.Export, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.Create, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.Update, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.Copy, GoogleAuth.Integration, "request")]
[ExternalOperation(GoogleDriveOperations.Delete, GoogleAuth.Integration, "request")]
public sealed partial class DriveModule : Module<DriveModuleOptions>
{
    public const string ModuleName = DriveScopes.Connector;

    public static DigitalBrain.ModuleManifest Manifest => DriveNeurons.Manifest;

    // The connection neuron is plain (06-08): DriveNeurons' assembly [Neuron] classes run on the runtime's host grains.
    public override Type? Neurons => typeof(DriveNeurons);

    // The connector carries auth and neurons only; storage@1 is declared on the module's manifest (05-07, R4-01).
    public static ConnectorDefinition Connector { get; } = new(DriveScopes.Connector, "Google Drive", [GoogleAuth.Integration], [GoogleAuth.Integration])
    {
        Neurons = [typeof(ILegacyStorage)],
        OAuthMethods = [GoogleAuth.OAuth(GoogleAuth.Integration, [DriveScopes.Write])],
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
    public const string Create = "google.drive.files.create";
    public const string Update = "google.drive.files.update";
    public const string Copy = "google.drive.files.copy";
    public const string Delete = "google.drive.files.delete";
}

internal static class DriveScopes
{
    internal const string Connector = "google-drive";
    internal const string Read = "https://www.googleapis.com/auth/drive.readonly";
    internal const string Write = "https://www.googleapis.com/auth/drive";
    internal const string Files = "https://www.googleapis.com/auth/drive.file";
}

[GenerateSerializer, Alias("db.google-drive.unavailable")]
public sealed class DriveUnavailableException(string message) : InvalidOperationException(message);

// The DigitalBrain module of this connector (06-08): its assembly's [Neuron] classes run on the runtime's host grains.
[DigitalBrain.Module(DriveModule.ModuleName)]
public sealed class DriveNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [DigitalBrain.Capabilities.Capability.Storage.V1],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<DriveModuleOptions>()],
        Secrets: []);
}
