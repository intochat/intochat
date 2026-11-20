using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Modules;

namespace IntoChat.OS.Authority;

// identity@1 and secrets@1 live in this assembly. The host registers one keyed capability over the directory and
// vault ports. A cloud host binds sessions through Cloud.Auth and DigitalBrain.Runtime.Http.ISessionIssuer.
[Module(Id)]
public sealed class IdentityModule : Module
{
    public const string Id = "identity";

    public static System.Reflection.Assembly? Contracts { get; } = typeof(IntoChat.OS.Connections.Platform.Identity.IIdentityClient).Assembly;

    public static ModuleManifest Manifest { get; } = new(
        Provides: [Capability.Identity.V1, Capability.Secrets.V1],
        Requires: [],
        Needs: [Need.State("directory"), Need.State("vault")],
        Settings: [],
        Secrets: []);
}
