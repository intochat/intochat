using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Brains;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Testing;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Microsoft.Windows;
using IntoChat.OS.Sandbox;
using IntoChat.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Integration.Sandbox;

public sealed class SandboxEndToEndIntegrationFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DownloadsInvoicesPackageProcessesHostFileArchivesPersistsRowAndNotifies()
    {
        var tempDownloads = Directory.CreateTempSubdirectory("intochat-downloads-");
        try
        {
            await using var harness = await DualServerHarness.StartAsync(Ct);
            using var grantResponse = await harness.KernelClient.PostAsJsonAsync(
                $"/brains/{harness.Scope}/windows/folders/grants",
                new
                {
                    appKey = "downloads-invoices",
                    slot = "downloads",
                    hostPath = tempDownloads.FullName,
                    access = FolderAccess.ReadWrite,
                    enabled = true,
                },
                Ct);
            Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

            var bundle = ShippedPackages.Find("downloads-invoices")!.ToAppBundle();
            using var importResponse = await harness.KernelClient.PostAsJsonAsync(
                $"/brains/{harness.Scope}/apps/import",
                bundle,
                Ct);
            Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);

            var invoicePath = Path.Combine(tempDownloads.FullName, "acme-invoice.txt");
            await File.WriteAllTextAsync(invoicePath, "Invoice #1042\nVendor: Acme Corp\nTotal: $249.00\n", Ct);

            var processedPath = Path.Combine(tempDownloads.FullName, "Processed", "acme-invoice.txt");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline && (!File.Exists(processedPath) || File.Exists(invoicePath)))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), Ct);
            }

            Assert.True(File.Exists(processedPath), $"Expected archived invoice at '{processedPath}'.");
            Assert.False(File.Exists(invoicePath), "Expected original invoice file to be moved into Processed/.");

            var tables = harness.KernelServices.GetRequiredService<SdkTablesAdapter>();
            var rows = await tables.ListAsync(harness.Scope, "downloads-invoices", "invoices", 0, 50, Ct);
            var row = Assert.Single(rows, r => string.Equals(r.Key, "acme-invoice.txt", StringComparison.Ordinal));
            Assert.Contains("249.00", row.Json, StringComparison.Ordinal);

            NotificationInboxPayload? notifications = null;
            deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                notifications = await harness.KernelClient.GetFromJsonAsync<NotificationInboxPayload>(
                    $"/brains/{harness.Scope}/notifications",
                    Ct);
                if (notifications?.Items.Any(item => item.Title.Contains("Invoice processed: acme-invoice.txt", StringComparison.Ordinal)) == true)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
            }

            Assert.NotNull(notifications);
            var alert = Assert.Single(
                notifications!.Items,
                item => item.Title.Contains("Invoice processed: acme-invoice.txt", StringComparison.Ordinal));
            Assert.Equal("downloads-invoices", alert.SourceApp);
            Assert.Contains("249.00", alert.Body, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempDownloads.FullName);
        }
    }

    [Fact]
    public async Task SandboxEdgeEnforcesAppTokenAndConfinesFolderTraversal()
    {
        var tempDownloads = Directory.CreateTempSubdirectory("intochat-confinement-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDownloads.FullName, "allowed.txt"), "safe-content", Ct);

            await using var harness = await DualServerHarness.StartAsync(Ct);
            using var grantResponse = await harness.KernelClient.PostAsJsonAsync(
                $"/brains/{harness.Scope}/windows/folders/grants",
                new
                {
                    appKey = "downloads-invoices",
                    slot = "downloads",
                    hostPath = tempDownloads.FullName,
                    access = FolderAccess.ReadWrite,
                    enabled = true,
                },
                Ct);
            Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

            var invokePayload = new
            {
                contract = typeof(IFolder).FullName,
                method = nameof(IFolder.ReadTextAsync),
                slot = "downloads",
                argsJson = JsonSerializer.Serialize(new[] { "../secret.txt" }),
            };

            // 1. Missing token -> 401 Unauthorized
            using var missingTokenResponse = await harness.KernelClient.PostAsJsonAsync("/edge/sandbox/invoke", invokePayload, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, missingTokenResponse.StatusCode);

            // 2. Forged token -> 401 Unauthorized
            using var forgedRequest = new HttpRequestMessage(HttpMethod.Post, "/edge/sandbox/invoke")
            {
                Content = JsonContent.Create(invokePayload),
            };
            forgedRequest.Headers.TryAddWithoutValidation("X-App-Token", "forged.token.value");
            forgedRequest.Headers.TryAddWithoutValidation("X-Brain-Id", harness.Scope);
            forgedRequest.Headers.TryAddWithoutValidation("X-App-Key", "downloads-invoices");
            using var forgedResponse = await harness.KernelClient.SendAsync(forgedRequest, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, forgedResponse.StatusCode);

            // 2b. Valid token reading legitimate file inside granted slot -> 200 OK
            var tokens = harness.KernelServices.GetRequiredService<AppTokenService>();
            var validToken = tokens.Issue(harness.Scope, "downloads-invoices");
            using var allowedRequest = new HttpRequestMessage(HttpMethod.Post, "/edge/sandbox/invoke")
            {
                Content = JsonContent.Create(new
                {
                    contract = typeof(IFolder).FullName,
                    method = nameof(IFolder.ReadTextAsync),
                    slot = "downloads",
                    argsJson = JsonSerializer.Serialize(new[] { "allowed.txt" }),
                }),
            };
            allowedRequest.Headers.TryAddWithoutValidation("X-App-Token", validToken);
            allowedRequest.Headers.TryAddWithoutValidation("X-Brain-Id", harness.Scope);
            allowedRequest.Headers.TryAddWithoutValidation("X-App-Key", "downloads-invoices");
            using var allowedResponse = await harness.KernelClient.SendAsync(allowedRequest, Ct);
            Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
            var allowedBody = await allowedResponse.Content.ReadAsStringAsync(Ct);
            Assert.Contains("safe-content", allowedBody, StringComparison.Ordinal);

            // 3. Valid token attempting '../secret.txt' traversal -> 403 Forbidden
            using var traversalRequest = new HttpRequestMessage(HttpMethod.Post, "/edge/sandbox/invoke")
            {
                Content = JsonContent.Create(invokePayload),
            };
            traversalRequest.Headers.TryAddWithoutValidation("X-App-Token", validToken);
            traversalRequest.Headers.TryAddWithoutValidation("X-Brain-Id", harness.Scope);
            traversalRequest.Headers.TryAddWithoutValidation("X-App-Key", "downloads-invoices");
            using var traversalResponse = await harness.KernelClient.SendAsync(traversalRequest, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, traversalResponse.StatusCode);

            // 4. Valid token for another ungranted app -> 403 Forbidden
            var ungrantedToken = tokens.Issue(harness.Scope, "other-app");
            using var ungrantedRequest = new HttpRequestMessage(HttpMethod.Post, "/edge/sandbox/invoke")
            {
                Content = JsonContent.Create(new
                {
                    contract = typeof(IFolder).FullName,
                    method = nameof(IFolder.ListAsync),
                    slot = "downloads",
                    argsJson = "[]",
                }),
            };
            ungrantedRequest.Headers.TryAddWithoutValidation("X-App-Token", ungrantedToken);
            ungrantedRequest.Headers.TryAddWithoutValidation("X-Brain-Id", harness.Scope);
            ungrantedRequest.Headers.TryAddWithoutValidation("X-App-Key", "other-app");
            using var ungrantedResponse = await harness.KernelClient.SendAsync(ungrantedRequest, Ct);
            Assert.Equal(HttpStatusCode.Forbidden, ungrantedResponse.StatusCode);
        }
        finally
        {
            DeleteDirectoryIfPresent(tempDownloads.FullName);
        }
    }

    private sealed record NotificationInboxPayload(NotificationItem[] Items, int UnreadCount);

    private sealed class DualServerHarness : IAsyncDisposable
    {
        private readonly WebApplication _sandboxApp;
        private readonly WebApplication _kernelApp;
        private readonly PortLease _ports;
        private readonly string _homeDir;

        private DualServerHarness(
            WebApplication sandboxApp,
            WebApplication kernelApp,
            HttpClient kernelClient,
            PortLease ports,
            string homeDir,
            string scope)
        {
            _sandboxApp = sandboxApp;
            _kernelApp = kernelApp;
            KernelClient = kernelClient;
            _ports = ports;
            _homeDir = homeDir;
            Scope = scope;
        }

        public HttpClient KernelClient { get; }
        public IServiceProvider KernelServices => _kernelApp.Services;
        public string Scope { get; }

        public static async Task<DualServerHarness> StartAsync(CancellationToken ct)
        {
            var enrollKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var signingMaterial = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var homeDir = Path.Combine(Path.GetTempPath(), "intochat-os-sandbox-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(homeDir);

            var ports = PortReservation.ReservePair();
            var sandboxApp = await StartSandboxWorkerAsync(enrollKey, ct);
            var sandboxUrl = sandboxApp.Urls.First();

            WebApplication? kernelApp = null;
            HttpClient? kernelClient = null;
            try
            {
                var kernelBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    EnvironmentName = "Development",
                    ApplicationName = typeof(OperatingSystemModules).Assembly.GetName().Name,
                    ContentRootPath = Path.GetTempPath(),
                });
                kernelBuilder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
                kernelBuilder.WebHost.UseUrls("http://127.0.0.1:0");

                var sandboxOptionsPrefix = ModuleConfiguration.ModuleOptionsSection(SandboxOptions.SectionName);
                kernelBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
                    [IntoChatOSNames.HomeKey] = homeDir,
                    [IntoChatOSNames.ShellEnabledKey] = "false",
                    [IntoChatOSNames.FrontEnabledKey] = "false",
                    [AuthOptions.PostureKey] = "Open",
                    [TopologyContext.SiloPortKey] = ports.SiloPort.ToString(CultureInfo.InvariantCulture),
                    [TopologyContext.GatewayPortKey] = ports.GatewayPort.ToString(CultureInfo.InvariantCulture),
                    [$"{sandboxOptionsPrefix}:Url"] = sandboxUrl,
                    [$"{sandboxOptionsPrefix}:EnrollKey"] = enrollKey,
                    [$"{sandboxOptionsPrefix}:TokenSigningMaterial"] = signingMaterial,
                });

                LocalRunDefaults.Apply(kernelBuilder.Configuration);
                OsModule[] modules =
                [
                    .. OperatingSystemModules.InProcessModules,
                    OsModule.FromDigital(typeof(WindowsModule)),
                ];
                kernelBuilder.AddIntoChatOSServer(server => server.AddOperatingSystem(modules));

                kernelApp = kernelBuilder.Build();
                kernelApp.MapIntoChatOSServer();
                await kernelApp.StartAsync(ct);

                var kernelUrl = kernelApp.Urls.First();
                kernelApp.Services.GetRequiredService<IOptions<SandboxOptions>>().Value.KernelCallbackUrl = kernelUrl;

                kernelClient = new HttpClient { BaseAddress = new Uri(kernelUrl, UriKind.Absolute) };
                var scope = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

                var ready = false;
                var healthDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
                while (DateTimeOffset.UtcNow < healthDeadline)
                {
                    try
                    {
                        using var readyRes = await kernelClient.GetAsync("/ready", ct);
                        using var foldersRes = await kernelClient.GetAsync($"/brains/{scope}/windows/folders", ct);
                        if (readyRes.IsSuccessStatusCode && foldersRes.IsSuccessStatusCode)
                        {
                            ready = true;
                            break;
                        }
                    }
                    catch (HttpRequestException)
                    {
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                }

                if (!ready)
                {
                    throw new TimeoutException($"Kernel server at '{kernelUrl}' did not become ready within 30 seconds.");
                }

                return new DualServerHarness(sandboxApp, kernelApp, kernelClient, ports, homeDir, scope);
            }
            catch
            {
                kernelClient?.Dispose();
                if (kernelApp is not null)
                {
                    await kernelApp.DisposeAsync();
                }

                await sandboxApp.DisposeAsync();
                ports.Dispose();
                DeleteDirectoryIfPresent(homeDir);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            KernelClient.Dispose();
            await _kernelApp.StopAsync();
            await _kernelApp.DisposeAsync();
            await _sandboxApp.StopAsync();
            await _sandboxApp.DisposeAsync();
            _ports.Dispose();
            DeleteDirectoryIfPresent(_homeDir);
        }

        private static async Task<WebApplication> StartSandboxWorkerAsync(string enrollKey, CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
                ApplicationName = typeof(SandboxEndToEndIntegrationFacts).Assembly.GetName().Name,
                ContentRootPath = Path.GetTempPath(),
            });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddHttpClient("SandboxKernel");
            builder.Services.AddSingleton(sp => new AppSandboxHost(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SandboxKernel"),
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<ILogger<AppSandboxHost>>()));

            var app = builder.Build();
            app.MapGet("/health", (AppSandboxHost host) => Results.Ok(new { status = "ok", loadedApps = host.LoadedAppCount }));

            var enrollKeyBytes = Encoding.UTF8.GetBytes(enrollKey);
            var apps = app.MapGroup("/apps");
            apps.AddEndpointFilter(async (context, next) =>
            {
                if (!context.HttpContext.Request.Headers.TryGetValue("X-Sandbox-Enroll-Key", out var provided)
                    || provided.Count != 1)
                {
                    return Results.Unauthorized();
                }

                var providedBytes = Encoding.UTF8.GetBytes(provided[0] ?? string.Empty);
                if (providedBytes.Length != enrollKeyBytes.Length
                    || !CryptographicOperations.FixedTimeEquals(providedBytes, enrollKeyBytes))
                {
                    return Results.Unauthorized();
                }

                return await next(context).ConfigureAwait(false);
            });

            apps.MapPost("/deploy", async (DeployAppRequest req, AppSandboxHost host, CancellationToken reqCt)
                => Results.Ok(await host.DeployAsync(req, reqCt).ConfigureAwait(false)));

            apps.MapPost("/dispatch", async (DispatchAppRequest req, AppSandboxHost host, CancellationToken reqCt) =>
            {
                try
                {
                    return Results.Ok(await host.DispatchAsync(req, reqCt).ConfigureAwait(false));
                }
                catch (KeyNotFoundException ex)
                {
                    return Results.NotFound(new { error = ex.Message });
                }
            });

            apps.MapPost("/unload", async (UnloadAppRequest req, AppSandboxHost host, CancellationToken reqCt)
                => Results.Ok(await host.UnloadAsync(req.BrainId, req.AppKey, reqCt).ConfigureAwait(false)));

            await app.StartAsync(ct);
            return app;
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
