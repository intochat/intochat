using System.Buffers.Text;
using System.Security.Cryptography;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.Flutter.Aspire.Hosting;

public static class ShellHostingExtensions
{
    internal const string PresenceDartDefinePrefix = "--dart-define=INTOCHAT_PRESENCE_TOKEN=";

    public static IntoChatOSModuleBuilder<IntoChat.OS.Modules.Flutter.Aspire.Hosting.FlutterModuleHosting> RunDesktopApp(
        this IntoChatOSModuleBuilder<IntoChat.OS.Modules.Flutter.Aspire.Hosting.FlutterModuleHosting> module,
        Action<FlutterHostOptions>? configure = null)
        => ConfigureFlutterHost(module, FlutterHostKind.Window, configure);

    public static IntoChatOSModuleBuilder<IntoChat.OS.Modules.Flutter.Aspire.Hosting.FlutterModuleHosting> RunWebApp(
        this IntoChatOSModuleBuilder<IntoChat.OS.Modules.Flutter.Aspire.Hosting.FlutterModuleHosting> module,
        Action<FlutterHostOptions>? configure = null)
        => ConfigureFlutterHost(module, FlutterHostKind.Web, configure);

    private static IntoChatOSModuleBuilder<IntoChat.OS.Modules.Flutter.Aspire.Hosting.FlutterModuleHosting> ConfigureFlutterHost(
        IntoChatOSModuleBuilder<IntoChat.OS.Modules.Flutter.Aspire.Hosting.FlutterModuleHosting> module,
        FlutterHostKind kind,
        Action<FlutterHostOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(module);

        var options = new FlutterHostOptions();
        if (kind == FlutterHostKind.Web)
        {
            options.DeviceTarget = ShellNames.DefaultWebDeviceTarget;
        }

        FlutterHostOptions.Overlay(options, module.IntoChatOSBuilder.ApplicationBuilder.Configuration);
        options.Kind = kind;
        configure?.Invoke(options);
        GetOrCreateState(module).EnsureFlutterHost(kind, options);
        return module;
    }

    private static ShellHostingState GetOrCreateState(IntoChatOSModuleBuilder<IntoChat.OS.Modules.Flutter.Aspire.Hosting.FlutterModuleHosting> module)
    {
        var state = module.GetOrAddProjection(static brain => new ShellHostingState(brain));

        return state;
    }

    internal static string ResolveFlutterWorkingDirectory(string appHostDirectory, string? configured)
        => ShellHostingState.ResolveFlutterWorkingDirectory(appHostDirectory, configured);

    // The shell talks to the kernel through the front that owns its fixed port, not to one silo instance: a
    // silo's restart (a roll) must not drop the shell. Without a front the shell binds to the first silo.
    public static IntoChatOSBuilder WithShellFront(this IntoChatOSBuilder brain, IResourceBuilder<IResourceWithEndpoints> front)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(front);
        brain.GetOrAddState(static brain => new ShellHostingState(brain), out _).BindFront(front);
        return brain;
    }

    internal static string? TryGetPresenceToken(IntoChatOSBuilder? brain)
        => brain is not null && brain.TryGetState<ShellHostingState>(out var state) && state.HasShellHost
            ? state.PresenceToken
            : null;

    private sealed class ShellHostingState(IntoChatOSBuilder brain) : IntoChatOSModuleProjection
    {
        private readonly string _presenceToken = MintPresenceToken(out _);
        internal string PresenceToken => _presenceToken;
        internal bool HasShellHost => _flutterHost is not null || _shellContainer is not null;
        private string? _presenceTokenHash;
        private IResourceBuilder<ExecutableResource>? _flutterHost;
        private IResourceBuilder<ContainerResource>? _shellContainer;
        private string? _servedConfigPath;
        private string? _flutterWebDirectory;
        private FlutterHostKind _flutterKind;
        private IResourceBuilder<IResourceWithEndpoints>? _front;
        private EndpointReference? _firstKernel;

        private string PresenceTokenHash => _presenceTokenHash ??= PresenceProof.ComputeTokenHash(_presenceToken);

        private static string MintPresenceToken(out byte[] rawBytes)
        {
            rawBytes = RandomNumberGenerator.GetBytes(PresenceProof.SecretByteCount);
            return Base64Url.EncodeToString(rawBytes);
        }

        // Native gRPC needs TLS/ALPN through the front; the web shell uses gRPC-Web over HTTP.
        private EndpointReference Kernel => _front?.GetEndpoint(_flutterKind == FlutterHostKind.Window ? "https" : ShellNames.HttpEndpointName) ?? _firstKernel!;

        internal void BindFront(IResourceBuilder<IResourceWithEndpoints> front)
        {
            _front = front;
            var wait = new WaitAnnotation(front.Resource, WaitType.WaitUntilHealthy, exitCode: 0);
            _flutterHost?.WithAnnotation(wait);
            _shellContainer?.WithAnnotation(wait);
        }

        internal void EnsureFlutterHost(FlutterHostKind kind, FlutterHostOptions options)
        {
            if (_flutterHost is not null || _shellContainer is not null)
            {
                throw new InvalidOperationException(
                    $"Flutter host is already configured on brain '{brain.Name}'. " +
                    $"Call {nameof(RunDesktopApp)} or {nameof(RunWebApp)} exactly once.");
            }

            var appHost = brain.ApplicationBuilder;
            var packageRoot = ResolveFlutterWorkingDirectory(appHost.AppHostDirectory, options.WorkingDirectory);
            if (!Directory.Exists(packageRoot)
                || !File.Exists(Path.Combine(packageRoot, "pubspec.yaml")))
            {
                throw new InvalidOperationException(
                    $"Flutter host package was not found at '{packageRoot}'. " +
                    "Pass FlutterHostOptions.WorkingDirectory or place ui in the repo.");
            }

            var resourceName = string.IsNullOrWhiteSpace(options.ResourceName)
                ? ResourceNames.Pick(
                    appHost,
                    kind == FlutterHostKind.Web ? ShellNames.WebResourceName : ShellNames.DesktopResourceName,
                    ShellNames.LegacyResourceName)
                : options.ResourceName;
            var shell = string.IsNullOrWhiteSpace(options.ShellName)
                ? ShellNames.DefaultShellName
                : options.ShellName;
            var chat = string.IsNullOrWhiteSpace(options.ChatName)
                ? ShellNames.DefaultChatName
                : options.ChatName;
            _flutterKind = kind;
            _pendingShell = shell;
            _pendingChat = chat;

            if (kind == FlutterHostKind.Web && options.WebContainer)
            {
                EnsureShellContainer(appHost, packageRoot, resourceName);
                return;
            }

            // The dev web server below compiles in the shared source directory, so concurrent
            // AppHosts (parallel E2E suites) must not use it: they race on one build/ and a
            // losing server dies. E2E fixtures set FlutterHostOptions.WebContainerKey.

            var launch = FlutterHostLaunch.Resolve(kind, packageRoot, options, appHost.Configuration);

            var flutterNode = brain.GetOrAddModuleNode(ShellNames.ModuleId);
            var host = appHost
                .AddExecutable(resourceName, launch.Command, launch.WorkingDirectory, launch.Args)
                .WithEnvironment(static context => ChildEnvironmentPolicy.ScrubObjects(context.EnvironmentVariables))
                .WithEnvironment(ShellNames.ShellEnvironmentVariable, shell)
                .WithEnvironment(ShellNames.ChatEnvironmentVariable, chat)
                .WithParentRelationship(flutterNode);

            if (kind == FlutterHostKind.Window)
            {
                host.WithArgs($"{PresenceDartDefinePrefix}{_presenceToken}");
            }

            if (kind == FlutterHostKind.Web)
            {
                if (string.Equals(launch.DeviceTarget, ShellNames.DefaultWebDeviceTarget, StringComparison.OrdinalIgnoreCase))
                {
                    var buildCheck = resourceName + "-build";
                    appHost.Services.AddHealthChecks().Add(new HealthCheckRegistration(buildCheck,
                        services => new FlutterWebBuildHealthCheck(services.GetRequiredService<ResourceLoggerService>(), host.Resource,
                            host.GetEndpoint(ShellNames.HttpEndpointName)),
                        failureStatus: null, tags: null));
                    host.WithHealthCheck(buildCheck);
                }
                // HTTP verifies the server; the separate build check rejects old cached
                // assets until this process reports its current compilation is served.
                host
                    .WithHttpEndpoint(
                        name: ShellNames.HttpEndpointName,
                        env: "INTOCHATOS_WEB_PORT",
                        isProxied: false)
                    .WithEndpoint(
                        ShellNames.HttpEndpointName,
                        static endpoint => endpoint.TargetHost = ShellNames.FlutterWebHostname,
                        createIfNotExists: false)
                    .WithHttpHealthCheck("/")
                    .AsBrainBrowser(path: $"/?semantics=true#presence={_presenceToken}", readySelector: "flt-semantics");
                host.WithArgs(ReferenceExpression.Create($"--web-port={host.GetEndpoint(ShellNames.HttpEndpointName).Property(EndpointProperty.TargetPort)}"));
                _flutterWebDirectory = launch.WorkingDirectory;
            }

            // Hot reload rides the Dart VM service, which the headless web-server target no
            // longer exposes (it runs --release; see FlutterHostLaunch.ResolveWeb). Window and
            // browser-driving web targets (e.g. "chrome" via the configure hook) keep it.
            var hasVmService = !options.ReleaseBuild
                && (kind == FlutterHostKind.Window
                    || (kind == FlutterHostKind.Web
                        && !string.Equals(
                            launch.DeviceTarget,
                            ShellNames.DefaultWebDeviceTarget,
                            StringComparison.OrdinalIgnoreCase)));
            if (appHost.ExecutionContext.IsRunMode && hasVmService)
            {
                ArmHotReload(appHost, host, resourceName, launch.WorkingDirectory);
            }

            if (appHost.ExecutionContext.IsRunMode)
            {
                FlutterClientTargets.AddExplicitClientTargets(
                    appHost,
                    packageRoot,
                    flutterNode.Resource,
                    resourceName,
                    options,
                    _presenceToken,
                    () => _front?.GetEndpoint(ShellNames.HttpEndpointName) ?? _firstKernel);
            }

            _flutterHost = host;
        }

        // Compile once per source change, serve per session: the image bakes the release
        // bundle (docker layer cache is the staleness logic), and the kernel URL arrives at
        // serve time through the config.json this AppHost writes before the container starts.
        private void EnsureShellContainer(
            IDistributedApplicationBuilder appHost,
            string packageRoot,
            string resourceName)
        {
            var shellDirectory = FlutterHostLaunch.ResolveWebPackageDirectory(packageRoot)
                ?? throw new InvalidOperationException(
                    $"Containerized web shell needs lib/main.dart and a 'web/' folder under " +
                    $"'{packageRoot}'.");
            var dockerfile = Path.Combine(shellDirectory, "Dockerfile");
            if (!File.Exists(dockerfile))
            {
                throw new InvalidOperationException(
                    $"Containerized web shell needs a Dockerfile at '{dockerfile}'.");
            }

            var workspaceRoot = shellDirectory;

            // The bind-mount source must exist when docker creates the container; the real
            // content lands in Apply right before start, once the kernel endpoint is allocated.
            _servedConfigPath = Path.Combine(
                Directory.CreateTempSubdirectory("intochat-os-shell-").FullName, "config.json");
            File.WriteAllText(_servedConfigPath, "{}");

            _shellContainer = appHost
                .AddDockerfile(resourceName, workspaceRoot, Path.GetRelativePath(workspaceRoot, dockerfile))
                // Image identity is the docker layer cache's job: the Dockerfile and app/.dockerignore
                // define the inputs, and an unchanged-source rebuild is a cache hit.
                .WithEnvironment(static context => ChildEnvironmentPolicy.ScrubObjects(context.EnvironmentVariables))
                .WithHttpEndpoint(targetPort: 80, name: ShellNames.HttpEndpointName)
                .WithHttpHealthCheck("/")
                .WithBindMount(_servedConfigPath, ShellServedConfig.ContainerPath, isReadOnly: true)
                .WithParentRelationship(brain.GetOrAddModuleNode(ShellNames.ModuleId))
                .AsBrainBrowser(path: $"/?semantics=true#presence={_presenceToken}", readySelector: "flt-semantics");
        }

        private static void ArmHotReload(
            IDistributedApplicationBuilder appHost,
            IResourceBuilder<ExecutableResource> host,
            string resourceName,
            string workingDirectory)
        {
            var ddsPort = ShellNames.FlutterDdsPort;
            var vmCheck = resourceName + "-vm";
            appHost.Services.AddHealthChecks().Add(new HealthCheckRegistration(
                vmCheck,
                _ => new FlutterVmHealthCheck(ddsPort),
                failureStatus: null,
                tags: null));
            host.WithHealthCheck(vmCheck);
            var watchRoots = ResolveWatchRoots(workingDirectory);
            host
                .WithArgs(
                    $"--dds-port={ddsPort}",
                    $"--host-vmservice-port={ShellNames.FlutterVmServicePort}",
                    "--disable-service-auth-codes")
                .WithUrl("http://127.0.0.1:" + ddsPort + "/devtools", "DevTools")
                .WithCommand(
                    "hot-reload",
                    "Hot Reload",
                    async context =>
                    {
                        try
                        {
                            await FlutterVmService.ReloadAsync(ddsPort, context.CancellationToken)
                                .ConfigureAwait(false);
                            return CommandResults.Success();
                        }
                        catch (Exception ex)
                        {
                            OsConsoleLog.For(typeof(ShellHostingExtensions)).LogWarning(ex, "Flutter hot reload command failed.");
                            return CommandResults.Failure(ex.Message);
                        }
                    },
                    new CommandOptions
                    {
                        IconName = "ArrowSync",
                        UpdateState = static context =>
                            string.Equals(context.ResourceSnapshot.State?.Text, "Running", StringComparison.Ordinal)
                                ? ResourceCommandState.Enabled
                                : ResourceCommandState.Disabled,
                    });

            host.OnResourceReady((resource, @event, _) =>
            {
                var logger = @event.Services
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("IntoChat.OS.FlutterHotReload");
                var lifetime = @event.Services.GetRequiredService<IHostApplicationLifetime>();
                FlutterHotReloadWatch.Start(watchRoots, ddsPort, logger, lifetime.ApplicationStopping);
                return Task.CompletedTask;
            });
        }

        private static string[] ResolveWatchRoots(string workingDirectory)
        {
            // A Dart workspace can include application packages outside the shell's
            // module. Watch its sources too, including on the first pub resolution.
            for (var directory = new DirectoryInfo(workingDirectory); directory is not null; directory = directory.Parent)
            {
                var manifest = Path.Combine(directory.FullName, "pubspec.yaml");
                if (File.Exists(manifest) && File.ReadLines(manifest).Any(line => line.TrimEnd() == "workspace:"))
                { return [directory.FullName]; }
            }
            return
            [
                Path.Combine(workingDirectory, "lib"),
            ];
        }

        private string _pendingShell = ShellNames.DefaultShellName;
        private string _pendingChat = ShellNames.DefaultChatName;

        // Applied to every silo instance: each one carries the shell's CORS origin, since the front may route the
        // shell to any of them. The shell itself is bound once, to the front when there is one (Kernel, resolved
        // when the shell starts), and waits for that and for the first instance.
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            if (_shellContainer is null && _flutterHost is null)
            {
                return;
            }

            builder.WithEnvironment(PresenceProof.TokenHashEnvironmentVariable, PresenceTokenHash);
            var corsOrigin = ModuleConfiguration.EnvironmentName(IntoChat.OS.Host.Http.KernelCorsOptions.SectionName, nameof(IntoChat.OS.Host.Http.KernelCorsOptions.AllowedOrigin));

            if (_shellContainer is not null)
            {
                builder.WithEnvironment(corsOrigin, _shellContainer.GetEndpoint(ShellNames.HttpEndpointName));
                if (_firstKernel is not null) { return; }
                _firstKernel = builder.GetEndpoint(ShellNames.HttpEndpointName);
                var configPath = _servedConfigPath!;
                var shellName = _pendingShell;
                var chatName = _pendingChat;
                _shellContainer
                    .WithEnvironment(context => context.EnvironmentVariables["INTOCHATOS_KERNEL_UPSTREAM"] = Kernel)
                    .WithAnnotation(new WaitAnnotation(builder.Resource, WaitType.WaitUntilHealthy, exitCode: 0))
                    // Endpoints are allocated before any resource starts, so the kernel URL is real here.
                    .OnBeforeResourceStarted((_, _, ct) => File.WriteAllTextAsync(
                        configPath, ShellServedConfig.Payload("", shellName, chatName), ct));
                return;
            }

            if (_flutterHost is null)
            {
                return;
            }

            if (_flutterKind == FlutterHostKind.Web)
            {
                builder.WithEnvironment(corsOrigin, _flutterHost.GetEndpoint(ShellNames.HttpEndpointName));
            }
            if (_firstKernel is not null) { return; }
            _firstKernel = builder.GetEndpoint(ShellNames.HttpEndpointName);
            // Flutter executable waits until the kernel HTTP surface is healthy.
            _flutterHost
                .WithEnvironment(context => context.EnvironmentVariables[ShellNames.UIBaseEnvironmentVariable] = Kernel)
                .WithAnnotation(new WaitAnnotation(builder.Resource, WaitType.WaitUntilHealthy, exitCode: 0));

            if (_flutterKind == FlutterHostKind.Web)
            {
                // The kernel endpoint must not be compiled into the bundle: a baked define
                // differs every session and forces a full release recompile per boot. The dev
                // web server serves web/config.json the same way the deployed container serves
                // its config.json, so the bundle stays byte-identical across sessions and
                // flutter reuses its build cache.
                var configPath = Path.Combine(_flutterWebDirectory!, "web", "config.json");
                var shellName = _pendingShell;
                var chatName = _pendingChat;
                _flutterHost.OnBeforeResourceStarted((_, _, ct) => File.WriteAllTextAsync(
                    configPath, ShellServedConfig.Payload(Kernel.Url, shellName, chatName), ct));
            }
        }

        internal static string ResolveFlutterWorkingDirectory(string appHostDirectory, string? configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.IsPathRooted(configured)
                    ? Path.GetFullPath(configured)
                    : Path.GetFullPath(Path.Combine(appHostDirectory, configured));
            }

            // Walk up rather than guessing a fixed depth: the caller can be an application AppHost
            // or any test project, and those sit at different depths under the repository root.
            string[] packagePathsFromRepositoryRoot =
            [
                "ui",
                Path.Combine("src", "IntoChat.OS.Modules", "Google", "Flutter", "app"),
                Path.Combine("..", "modules", "src", "IntoChat.OS.Modules", "Google", "Flutter", "app"),
            ];
            for (var current = new DirectoryInfo(Path.GetFullPath(appHostDirectory)); current is not null; current = current.Parent)
            {
                var uiCandidate = Path.Combine(current.FullName, "ui");
                if (File.Exists(Path.Combine(uiCandidate, "pubspec.yaml"))) { return uiCandidate; }
                foreach (var packagePath in packagePathsFromRepositoryRoot)
                {
                    var candidate = Path.Combine(current.FullName, packagePath);
                    if (File.Exists(Path.Combine(candidate, "pubspec.yaml"))) { return candidate; }
                }
            }

            throw new DirectoryNotFoundException(
                $"No Flutter package above '{appHostDirectory}'. Expected ui/pubspec.yaml "
                + "under the repository root, or pass FlutterHostOptions.WorkingDirectory.");
        }

        private sealed class FlutterVmHealthCheck(int ddsPort) : IHealthCheck
        {
            public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
                => await FlutterVmService.IsReadyAsync(ddsPort, cancellationToken).ConfigureAwait(false)
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy("Waiting for the Flutter desktop window and Dart VM service.");
        }
    }
}
