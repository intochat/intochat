using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Yarp;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

[Collection(OsAppHostCollection.Name)]
public sealed class BootFacts(OsAppHostFixture host)
{
    [Fact]
    public async Task TheOperatingSystemBootsAsAnApp()
    {
        var ct = TestContext.Current.CancellationToken;
        await host.Healthy(ct);
        Assert.True(host.StartupDuration < TimeSpan.FromSeconds(300), $"Fixture startup took {host.StartupDuration.TotalSeconds:F1}s, exceeding 300s.");

        using var health = await host.Os.GetAsync("/health", ct);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var apps = await host.Os.GetAsync($"/brains/{host.Scope}/apps", ct);
        Assert.Equal(HttpStatusCode.OK, apps.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await apps.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var installed = doc.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("name").GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);

        // CircleRoom is only installed in circle brains (ShippedInstall.Missing skips circleroom for personal brains).
        foreach (var root in ShippedApps.Roots.Where(r => !string.Equals(r, "circleroom", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Contains(root, installed);
        }
    }

    [Fact]
    public void TheHostComposesTheSameResourcesAsHeadless()
    {
        var model = host.App.Services.GetRequiredService<DistributedApplicationModel>();
        var names = model.Resources.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("os", names);
        Assert.Empty(model.Resources.OfType<ContainerResource>());
        Assert.Empty(model.Resources.OfType<YarpResource>());
        Assert.DoesNotContain(model.Resources, r => r.Annotations.Any(a => a.GetType().Name.EndsWith("BuildAnnotation", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheProductTierDownloadsNothing()
    {
        var env = await KernelEnvironmentAsync(host.App, "os", TestContext.Current.CancellationToken);
        Assert.DoesNotContain(env.Keys, static k => k.StartsWith("IntoChatOS__Modules__devices__Options__Prefetch__", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(host.App.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<ContainerResource>());
    }

    private static async Task<Dictionary<string, object>> KernelEnvironmentAsync(
        DistributedApplication app,
        string resourceName,
        CancellationToken ct)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources, r => string.Equals(r.Name, resourceName, StringComparison.Ordinal));
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var env = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var context = new EnvironmentCallbackContext(executionContext, resource, env, ct);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }
        return env;
    }

    [Fact]
    public async Task AFailedStartLeavesNoProcessBehind()
    {
        var bad = new OsAppHostFixture([$"--{IntoChat.OS.Host.Configuration.IntoChatOSNames.TopologyKey}=nowhere"]);
        var tempHome = bad.Home;

        await Assert.ThrowsAnyAsync<Exception>(async () => await bad.InitializeAsync());

        Assert.False(Directory.Exists(tempHome), $"Temporary home {tempHome} still exists after failed start.");
        var lingering = Process.GetProcesses()
            .Where(p =>
            {
                try
                {
                    return !p.HasExited && p.MainModule?.FileName?.Contains(tempHome, StringComparison.OrdinalIgnoreCase) == true;
                }
                catch
                {
                    return false;
                }
            })
            .ToArray();
        Assert.Empty(lingering);
    }

    [Fact]
    public async Task TheKernelIsReadyUnderTheBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        string homePath;
        int processId;

        await using (var kernel = await KernelProcessHarness.StartAsync(new KernelProcessOptions(), ct))
        {
            homePath = kernel.Home.Path;
            processId = kernel.ProcessId;

            Assert.True(processId > 0);
            Assert.False(kernel.HasExited);
            Assert.True(
                kernel.StartupDuration < TimeSpan.FromSeconds(30),
                $"Headless kernel cold boot took {kernel.StartupDuration.TotalSeconds:F2}s, exceeding 30s budget.\n{kernel.ReadDiagnostics()}");

            using var ready = await kernel.Client.GetAsync("/ready", ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            using var health = await kernel.Client.GetAsync("/health", ct);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            using var apps = await kernel.Client.GetAsync($"/brains/{kernel.Scope}/apps", ct);
            Assert.Equal(HttpStatusCode.OK, apps.StatusCode);

            using var unproved = kernel.CreateClient(withPresence: false);
            using var denied = await unproved.GetAsync($"/brains/{kernel.Scope}/apps", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        Assert.True(IsProcessExited(processId), $"Kernel child process {processId} lingered after dispose.");
        Assert.False(Directory.Exists(homePath), $"Test home '{homePath}' lingered after dispose.");
    }

    [Fact]
    public async Task TheChatAndTheKeySurviveAKernelKill()
    {
        var ct = TestContext.Current.CancellationToken;
        using var home = new TestHome("intochat-kill-");
        await using var ollama = ScriptedOllama.Start();
        ollama.EnqueueReply("First kernel turn survived.", ollama.Models[0]);

        byte[] sealedMasterKeyBeforeKill;
        string unsealedMasterKeyBeforeKill;
        int firstProcessId;

        await using (var kernel1 = await KernelProcessHarness.StartAsync(
            new KernelProcessOptions
            {
                Home = home,
                KeepHomeOnDispose = true,
                OllamaEndpoint = ollama.BaseAddress,
                DefaultModel = ollama.Models[0],
            },
            ct))
        {
            firstProcessId = kernel1.ProcessId;
            Assert.True(File.Exists(home.MasterKeyPath));
            sealedMasterKeyBeforeKill = await File.ReadAllBytesAsync(home.MasterKeyPath, ct);
            unsealedMasterKeyBeforeKill = home.ReadSealedMasterKey();

            using var putEntry = await kernel1.Client.PutAsJsonAsync(
                $"/brains/{kernel1.Scope}/entries/provider:openai",
                new { kind = "secret", value = "sk-live-survive-9876" },
                ct);
            Assert.True(putEntry.IsSuccessStatusCode, $"PUT secret entry failed with {(int)putEntry.StatusCode}: {await putEntry.Content.ReadAsStringAsync(ct)}");

            await AssertSecretEntryAsync(kernel1.Client, kernel1.Scope, "provider:openai", "9876", "sk-live-survive-9876", ct);

            await OpenInoAsync(kernel1.Client, kernel1.Scope, ct);
            await SendInoPromptAsync(kernel1.Client, kernel1.Scope, "Remember our launch code ORBIT-7.", ct);
            await WaitForInoScreenTextAsync(kernel1.Client, kernel1.Scope, "Remember our launch code ORBIT-7.", ct);
            await WaitForInoScreenTextAsync(kernel1.Client, kernel1.Scope, "First kernel turn survived.", ct);

            var brainKeys = Directory.EnumerateFiles(home.StateDirectory, "brain.key", SearchOption.AllDirectories).ToArray();
            Assert.NotEmpty(brainKeys);

            await kernel1.KillAsync(ct);
            Assert.True(kernel1.HasExited);
            Assert.True(IsProcessExited(firstProcessId), $"Killed kernel process {firstProcessId} is still alive.");
        }

        Assert.True(File.Exists(home.MasterKeyPath));
        Assert.Equal(sealedMasterKeyBeforeKill, await File.ReadAllBytesAsync(home.MasterKeyPath, ct));
        Assert.Equal(unsealedMasterKeyBeforeKill, home.ReadSealedMasterKey());

        ollama.EnqueueReply("Second turn after kernel kill!", ollama.Models[0]);

        await using var kernel2 = await KernelProcessHarness.StartAsync(
            new KernelProcessOptions
            {
                Home = home,
                KeepHomeOnDispose = true,
                OllamaEndpoint = ollama.BaseAddress,
                DefaultModel = ollama.Models[0],
            },
            ct);

        Assert.NotEqual(firstProcessId, kernel2.ProcessId);
        Assert.Equal(sealedMasterKeyBeforeKill, await File.ReadAllBytesAsync(home.MasterKeyPath, ct));
        Assert.Equal(unsealedMasterKeyBeforeKill, home.ReadSealedMasterKey());

        await AssertSecretEntryAsync(kernel2.Client, kernel2.Scope, "provider:openai", "9876", "sk-live-survive-9876", ct);

        await OpenInoAsync(kernel2.Client, kernel2.Scope, ct);
        await WaitForInoScreenTextAsync(kernel2.Client, kernel2.Scope, "Remember our launch code ORBIT-7.", ct);
        await WaitForInoScreenTextAsync(kernel2.Client, kernel2.Scope, "First kernel turn survived.", ct);

        await SendInoPromptAsync(kernel2.Client, kernel2.Scope, "What launch code did I give you before the crash?", ct);
        await WaitForInoScreenTextAsync(kernel2.Client, kernel2.Scope, "Second turn after kernel kill!", ct);

        Assert.True(ollama.ChatRequests.Count >= 2, $"Expected at least 2 Ollama chat turns, observed {ollama.ChatRequests.Count}.");
        var followUpRequest = ollama.ChatRequests[^1];
        Assert.True(
            followUpRequest.HasMessageContaining("user", "ORBIT-7")
            || followUpRequest.HasMessageContaining("assistant", "First kernel turn survived."),
            $"Expected second turn sent to Ollama after Process.Kill to carry pre-kill history. Raw JSON:\n{followUpRequest.RawJson}");
    }

    private static async Task AssertSecretEntryAsync(
        HttpClient client,
        string scope,
        string entryName,
        string expectedTail,
        string rawSecret,
        CancellationToken ct)
    {
        using var response = await client.GetAsync($"/brains/{scope}/entries", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(rawSecret, body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);
        var entry = doc.RootElement.EnumerateArray()
            .FirstOrDefault(item => string.Equals(item.GetProperty("name").GetString(), entryName, StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Object, entry.ValueKind);
        Assert.Equal("secret", entry.GetProperty("kind").GetString());
        Assert.True(entry.GetProperty("isSet").GetBoolean());
        Assert.Equal(expectedTail, entry.GetProperty("tail").GetString());
    }

    private static async Task OpenInoAsync(HttpClient client, string scope, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var appCheck = await client.GetAsync($"/brains/{scope}/apps/ino", ct);
            if (appCheck.StatusCode == HttpStatusCode.OK)
            {
                break;
            }

            await Task.Delay(100, ct);
        }

        var composerSlotKey = $"{InoView.ConversationKey}/{InoView.ComposerKey}";
        using (var existingScreen = await client.GetAsync($"/brains/{scope}/screens/ino", ct))
        {
            if (existingScreen.StatusCode == HttpStatusCode.OK)
            {
                var existingJson = await existingScreen.Content.ReadAsStringAsync(ct);
                using var existingDoc = JsonDocument.Parse(existingJson);
                if (existingDoc.RootElement.TryGetProperty("controls", out var existingControls)
                    && existingControls.EnumerateArray().Any(c => string.Equals(c.GetProperty("key").GetString(), composerSlotKey, StringComparison.Ordinal)))
                {
                    return;
                }
            }
        }

        using var openResponse = await client.PostAsJsonAsync(
            $"/brains/{scope}/apps/ino/request",
            new { operation = "open", input = "{}" },
            ct);
        Assert.True(
            openResponse.StatusCode == HttpStatusCode.Accepted || openResponse.IsSuccessStatusCode,
            $"Open Ino returned {(int)openResponse.StatusCode}: {await openResponse.Content.ReadAsStringAsync(ct)}");

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var screenResponse = await client.GetAsync($"/brains/{scope}/screens/ino", ct);
            if (screenResponse.StatusCode == HttpStatusCode.OK)
            {
                var json = await screenResponse.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("controls", out var controls)
                    && controls.EnumerateArray().Any(c => string.Equals(c.GetProperty("key").GetString(), composerSlotKey, StringComparison.Ordinal)))
                {
                    return;
                }
            }

            await Task.Delay(100, ct);
        }

        throw new TimeoutException("Ino screen did not expose composer control within 30s.");
    }

    private static async Task SendInoPromptAsync(HttpClient client, string scope, string prompt, CancellationToken ct)
    {
        var composerSlotKey = $"{InoView.ConversationKey}/{InoView.ComposerKey}";
        using var screenResponse = await client.GetAsync($"/brains/{scope}/screens/ino", ct);
        screenResponse.EnsureSuccessStatusCode();
        using var snapshot = JsonDocument.Parse(await screenResponse.Content.ReadAsStringAsync(ct));
        long? generation = snapshot.RootElement.TryGetProperty("generation", out var genProp) && genProp.TryGetInt64(out var g)
            ? g
            : null;

        using var actResponse = await client.PostAsJsonAsync(
            $"/brains/{scope}/screens/ino/act",
            new { key = composerSlotKey, value = prompt, generation },
            ct);
        Assert.True(
            actResponse.IsSuccessStatusCode,
            $"POST /screens/ino/act failed with {(int)actResponse.StatusCode}: {await actResponse.Content.ReadAsStringAsync(ct)}");
    }

    private static async Task WaitForInoScreenTextAsync(HttpClient client, string scope, string expectedText, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        string lastSnapshot = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/brains/{scope}/screens/ino", ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                lastSnapshot = await response.Content.ReadAsStringAsync(ct);
                if (lastSnapshot.Contains(expectedText, StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(100, ct);
        }

        throw new TimeoutException($"Expected text '{expectedText}' did not appear on Ino screen within 30s. Last snapshot:\n{lastSnapshot}");
    }

    private static bool IsProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
