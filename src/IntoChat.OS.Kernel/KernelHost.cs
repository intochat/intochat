using System.Reflection;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Kernel.DevUI;
using IntoChat.OS.Kernel.Install;
using IntoChat.OS.Kernel.Mcp;
using IntoChat.OS.Modules.Microsoft.Azure;

namespace IntoChat.OS.Kernel;

/// <summary>
/// Composable WebApplicationBuilder and WebApplication extensions for an IntoChat OS Kernel host process.
/// </summary>
public static class KernelHost
{
    public static WebApplicationBuilder AddIntoChatOSKernel(
        this WebApplicationBuilder builder,
        Assembly? rootAssembly = null,
        Action<IntoChatOSServerBuilder, IConfiguration>? configureServer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
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
        builder.AddDevUIForIno();
        return builder;
    }

    public static WebApplication MapIntoChatOSKernel(
        this WebApplication app,
        Action<WebApplication>? beforeAccountSession = null,
        Action<WebApplication>? configureApp = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapIntoChatOSServer(beforeAccountSession);
        app.MapIntoChatOSMcp();
        app.MapDevUIForIno();
        configureApp?.Invoke(app);
        return app;
    }

    public static async Task<int> RunIntoChatOSKernelAsync(
        this WebApplication app,
        string[] args,
        Assembly? rootAssembly = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);
        if (AzureInstaller.IsInstall(args))
        {
            var modules = OperatingSystemModules.Discover(rootAssembly);
            return await AzureInstaller.Run(args, new ArmAzure(), modules, Console.Out, Console.Error, cancellationToken);
        }

        await app.RunAsync(cancellationToken);
        return Environment.ExitCode;
    }

    public static async Task<int> RunAsync(
        string[] args,
        Assembly? rootAssembly = null,
        Action<IntoChatOSServerBuilder, IConfiguration>? configureServer = null,
        Action<WebApplication>? beforeAccountSession = null,
        Action<WebApplication>? configureApp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (AzureInstaller.IsInstall(args))
        {
            var modules = OperatingSystemModules.Discover(rootAssembly);
            return await AzureInstaller.Run(args, new ArmAzure(), modules, Console.Out, Console.Error, cancellationToken);
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.AddIntoChatOSKernel(rootAssembly, configureServer);
        var app = builder.Build();
        app.MapIntoChatOSKernel(beforeAccountSession, configureApp);
        return await app.RunIntoChatOSKernelAsync(args, rootAssembly, cancellationToken);
    }
}
