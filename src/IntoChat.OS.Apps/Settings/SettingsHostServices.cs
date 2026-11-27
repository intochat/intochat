using DigitalBrain;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Kernel;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Identity.Contracts.Connections;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.Modules.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Apps.Settings;

/// <summary>
/// Cohesive bundle of optional host/DI collaborators for <see cref="SettingsApp"/>.
/// Keeps <see cref="SettingsApp"/>'s constructor clean and compatible with Orleans
/// <see cref="ActivatorUtilitiesConstructorAttribute"/> and standalone unit tests.
/// </summary>
internal sealed class SettingsHostServices
{
    public ISecretVault? SecretVault { get; set; }
    public BrainPreferenceStore? PreferenceStore { get; set; }
    public MachineId? ThisMachine { get; set; }
    public IBrainObserver? Observer { get; set; }
    public IOptions<WebModuleOptions>? WebOptions { get; set; }
    public IOptions<McpModuleOptions>? McpOptions { get; set; }
    public IClientCodeSandboxConsent? ClientComputeConsent { get; set; }
    public IClientResourceConsent? ClientResourceConsent { get; set; }
    public IConnectedComputeRouter? ConnectedComputeRouter { get; set; }
    public IConfiguration? Configuration { get; set; }
    public Action<Uri>? OpenBrowserAction { get; set; }
    public IPendingDeviceEnrollmentStore? PendingEnrollmentStore { get; set; }
    public IReadOnlyList<ConnectorDefinition>? Connectors { get; set; }
    public ISecretVaultPort? SecretVaultPort { get; set; }
    public IOAuthDeviceFlow? OAuthDeviceFlow { get; set; }
    public IConnectionSecretBinder? ConnectionSecretBinder { get; set; }

    public static SettingsHostServices From(IServiceProvider? services)
    {
        if (services is null)
        {
            return new SettingsHostServices();
        }

        return new SettingsHostServices
        {
            SecretVault = services.GetService<ISecretVault>(),
            PreferenceStore = services.GetService<BrainPreferenceStore>(),
            ThisMachine = services.GetService<MachineId>(),
            Observer = services.GetService<IBrainObserver>(),
            WebOptions = services.GetService<IOptions<WebModuleOptions>>(),
            McpOptions = services.GetService<IOptions<McpModuleOptions>>(),
            ClientComputeConsent = services.GetService<IClientCodeSandboxConsent>(),
            ClientResourceConsent = services.GetService<IClientResourceConsent>(),
            ConnectedComputeRouter = services.GetService<IConnectedComputeRouter>(),
            Configuration = services.GetService<IConfiguration>(),
            OpenBrowserAction = services.GetService<Action<Uri>>(),
            PendingEnrollmentStore = services.GetService<IPendingDeviceEnrollmentStore>(),
            Connectors = services.GetService<IReadOnlyList<ConnectorDefinition>>(),
            SecretVaultPort = services.GetService<ISecretVaultPort>(),
            OAuthDeviceFlow = services.GetService<IOAuthDeviceFlow>(),
            ConnectionSecretBinder = services.GetService<IConnectionSecretBinder>(),
        };
    }
}
