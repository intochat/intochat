using System.Reflection;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Kernel.Install;
using IntoChat.OS.Kernel.Mcp;
using IntoChat.OS.Modules.Microsoft.Azure;

namespace IntoChat.OS.Kernel;

/// <summary>
/// Composable entry point for an IntoChat OS host process, allowing hosts to supply additional module assemblies and hooks.
/// </summary>
public static class KernelHost
{
    public static async Task<int> RunAsync(
        string[] args,
        Assembly? rootAssembly = null,
        Action<IntoChatOSServerBuilder, IConfiguration>? configureServer = null,
        Action<WebApplication>? beforeAccountSession = null,
        Action<WebApplication>? configureApp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        // The installer runs before any cluster exists: it ensures the Azure foundation and exits.
        if (AzureInstaller.IsInstall(args))
        {
            var modules = OperatingSystemModules.Discover(rootAssembly);
            return await AzureInstaller.Run(args, new ArmAzure(), modules, Console.Out, Console.Error, cancellationToken);
        }

        var builder = WebApplication.CreateBuilder(args);
        LocalRunDefaults.Apply(builder.Configuration);
        var topology = builder.Configuration[IntoChatOSNames.TopologyKey];
        var catalog = (OperatingSystem.IsWindows()
                ? OperatingSystemModules.WithWindowsEdge(rootAssembly)
                : OperatingSystemModules.Discover(rootAssembly))
            .Where(module => module.LoadsUnder(topology))
            .ToArray();
        var dashboardEnabled = builder.Configuration.GetValue<bool>("OrleansDashboard:Enabled")
            || builder.Configuration.GetValue<bool>("IntoChatOS:OrleansDashboard:Enabled")
            || (builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]));
        builder.AddIntoChatOSServer(server =>
        {
            if (dashboardEnabled) { server.UseOrleansDashboard(); }
            server.AddOperatingSystem(catalog);
            configureServer?.Invoke(server, builder.Configuration);
        });
        builder.Services.AddIntoChatOSMcp();
        var app = builder.Build();
        app.MapIntoChatOSServer(beforeAccountSession);
        app.MapIntoChatOSMcp();
        configureApp?.Invoke(app);
        await app.RunAsync(cancellationToken);
        return Environment.ExitCode;
    }
}
