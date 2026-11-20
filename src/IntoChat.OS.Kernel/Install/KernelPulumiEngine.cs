using System.Diagnostics;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Microsoft.Azure;
using IntoChat.OS.Host.Modules;

namespace IntoChat.OS.Kernel.Install;

/// <summary>
/// Full Kernel-wired <see cref="IPulumiEngine"/> that synthesizes the complete <see cref="PulumiStackPlan"/>
/// across all composed <see cref="OsModule"/>s and executes the Pulumi CLI when requested.
/// </summary>
internal sealed class KernelPulumiEngine(IReadOnlyList<OsModule> modules) : IPulumiEngine
{
    public async Task<PulumiStackSummary> SynthesizeAsync(string env, bool includeCloud, bool includeSandbox, CancellationToken cancellationToken = default)
    {
        var hasCloudModules = includeCloud || modules.Any(static m =>
            string.Equals(m.Id.Value, "lobby", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Id.Value, "organisations", StringComparison.OrdinalIgnoreCase));

        var config = InstallConfig.Universal(
            env,
            includeCloudApp: hasCloudModules,
            includeSandboxApp: includeSandbox);

        var plan = PulumiStackSynthesizer.Synthesize(config, modules);
        var workDir = Path.Combine(Path.GetTempPath(), "intochat-pulumi", env);
        await PulumiStackSynthesizer.WriteWorkspaceAsync(plan, workDir, cancellationToken);

        return ToSummary(plan, "synthesize", $"Synthesized Pulumi workspace in '{workDir}' ({plan.Resources.Count} resources, {plan.Secrets.Count} secrets, 0 SessionPools).");
    }

    public async Task<PulumiStackSummary> RunAsync(string env, string verb, bool includeCloud, bool includeSandbox, CancellationToken cancellationToken = default)
    {
        var hasCloudModules = includeCloud || modules.Any(static m =>
            string.Equals(m.Id.Value, "lobby", StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Id.Value, "organisations", StringComparison.OrdinalIgnoreCase));

        var config = InstallConfig.Universal(
            env,
            includeCloudApp: hasCloudModules,
            includeSandboxApp: includeSandbox);

        var plan = PulumiStackSynthesizer.Synthesize(config, modules);
        var workDir = Path.Combine(Path.GetTempPath(), "intochat-pulumi", env);
        await PulumiStackSynthesizer.WriteWorkspaceAsync(plan, workDir, cancellationToken);

        var (whoamiExit, _) = await RunPulumiProcessAsync(["whoami"], workDir, cancellationToken);
        if (whoamiExit != 0)
        {
            var stateDir = Path.Combine(workDir, ".pulumi-state");
            Directory.CreateDirectory(stateDir);
            _ = await RunPulumiProcessAsync(["login", $"file://{stateDir.Replace('\\', '/')}"], workDir, cancellationToken);
        }
        _ = await RunPulumiProcessAsync(["stack", "select", "--create", env], workDir, cancellationToken);

        string[] args = verb switch
        {
            "up" => ["up", "--yes", "--stack", env],
            "refresh" => ["refresh", "--yes", "--stack", env],
            "destroy" => ["destroy", "--yes", "--stack", env],
            _ => ["preview", "--stack", env],
        };
        var (_, output) = await RunPulumiProcessAsync(args, workDir, cancellationToken);
        return ToSummary(plan, verb, output);
    }

    private static PulumiStackSummary ToSummary(PulumiStackPlan plan, string action, string output)
    {
        var cloudApp = plan.Resources.OfType<ContainerAppSpec>().FirstOrDefault(a => string.Equals(a.Name, plan.Config.CloudName, StringComparison.Ordinal));
        var sandboxApp = plan.Resources.OfType<ContainerAppSpec>().FirstOrDefault(a => string.Equals(a.Name, plan.Config.SandboxName, StringComparison.Ordinal));
        return new PulumiStackSummary(
            Env: plan.Config.Env,
            ResourceGroup: plan.Config.GroupName,
            Location: plan.Config.Location,
            VaultName: plan.Config.VaultName,
            OsAppName: plan.OsApp.Name,
            CloudAppName: cloudApp?.Name,
            SandboxAppName: sandboxApp?.Name,
            ResourceCount: plan.Resources.Count,
            SecretBindingCount: plan.Secrets.Count,
            VariableCount: plan.Variables.Count,
            PulumiYaml: plan.PulumiYaml,
            StackConfigYaml: plan.StackConfigYaml,
            LastAction: action,
            LastOutput: output,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static async Task<(int ExitCode, string Output)> RunPulumiProcessAsync(string[] args, string workingDirectory, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pulumi",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!psi.Environment.ContainsKey("PULUMI_CONFIG_PASSPHRASE"))
        {
            psi.Environment["PULUMI_CONFIG_PASSPHRASE"] = "";
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 'pulumi' CLI.");
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : $"{stdout}\n{stderr}".Trim());
    }
}
