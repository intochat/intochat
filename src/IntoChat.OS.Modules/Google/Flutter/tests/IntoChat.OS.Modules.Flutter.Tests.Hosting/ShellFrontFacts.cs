using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Aspire.Testing;
using IntoChat.OS.Modules.Flutter.Aspire.Hosting;

namespace IntoChat.OS.Modules.Flutter.Tests.Hosting;

// A roll restarts silo instances one at a time; the shell must not ride on any one of them.
public sealed class ShellFrontFacts
{
    private const string AllowedOrigin = IntoChat.OS.Connections.Identity.Configuration.KernelCorsOptions.SectionName + ":" + nameof(IntoChat.OS.Connections.Identity.Configuration.KernelCorsOptions.AllowedOrigin);

    [Fact]
    public async Task TheDesktopShellUsesTheFrontsHttpsEndpointForNativeHttp2()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = AspireCompositionTest.CreateBuilder();
        var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions())
            .WithHosting<FlutterModuleHosting, FlutterModuleOptions>(flutter => flutter.RunDesktopApp());
        builder.AddExecutable("OperatingSystem-a", "unused", ".")
            .WithHttpEndpoint(name: "http").WithHttpsEndpoint(name: "https").WithReference(brain);
        var front = builder.AddExecutable("OperatingSystem-front", "unused", ".")
            .WithHttpEndpoint(port: 5080, name: "http").WithHttpsEndpoint(name: "https");
        brain.WithShellFront(builder.CreateResourceBuilder<IResourceWithEndpoints>(front.Resource));

        var shell = Assert.Single(builder.Resources.OfType<ExecutableResource>(), resource => resource.Name.Contains("FlutterShell", StringComparison.Ordinal));
        var environment = await AspireCompositionTest.EnvironmentOf(builder, shell, ct);
        var kernel = Assert.IsType<EndpointReference>(environment[ShellNames.UIBaseEnvironmentVariable]);
        Assert.Same(front.Resource, kernel.Resource);
        Assert.Equal("https", kernel.EndpointName);
    }

    [Fact]
    public async Task TheShellReachesTheKernelThroughTheFrontAndEveryInstanceAllowsItsOrigin()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = AspireCompositionTest.CreateBuilder();
        var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions())
            .WithHosting<FlutterModuleHosting, FlutterModuleOptions>(flutter => flutter.RunWebApp());
        var instances = new[] { "OperatingSystem-a", "OperatingSystem-b" }
            .Select(name => builder.AddExecutable(name, "unused", ".").WithHttpEndpoint(name: "http").WithReference(brain))
            .ToArray();
        var front = builder.AddExecutable("OperatingSystem-front", "unused", ".").WithHttpEndpoint(port: 5080, name: "http");

        brain.WithShellFront(builder.CreateResourceBuilder<IResourceWithEndpoints>(front.Resource));

        var shell = Assert.Single(builder.Resources.OfType<ExecutableResource>(), resource => resource.Annotations.OfType<BrainBrowserAnnotation>().Any());
        var shellEnvironment = await AspireCompositionTest.EnvironmentOf(builder, shell, ct);
        var kernel = Assert.IsType<EndpointReference>(shellEnvironment[ShellNames.UIBaseEnvironmentVariable]);
        Assert.Same(front.Resource, kernel.Resource);
        Assert.Contains(shell.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource == front.Resource);
        foreach (var instance in instances)
        {
            var environment = await AspireCompositionTest.ConfigurationOf(builder, instance.Resource, ct);
            var origin = Assert.IsType<EndpointReference>(environment.Single(pair => pair.Key.Equals(AllowedOrigin, StringComparison.OrdinalIgnoreCase)).Value);
            Assert.Same(shell, origin.Resource);
        }
    }
}
