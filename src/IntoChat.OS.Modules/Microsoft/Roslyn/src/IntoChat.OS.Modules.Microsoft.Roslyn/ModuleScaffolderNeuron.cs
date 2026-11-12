using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Microsoft.Roslyn.Contracts;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

[Neuron("roslyn.module-scaffolder")]
internal sealed class ModuleScaffolderNeuron : DigitalBrain.Neurons.Neuron, IModuleScaffolder
{
    private readonly string? _overrideRoot;

    public ModuleScaffolderNeuron()
    {
    }

    public ModuleScaffolderNeuron(string overrideRoot)
    {
        _overrideRoot = overrideRoot;
    }

    public Task<ScaffoldModuleResult> Scaffold(ScaffoldModuleRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Category);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        var category = request.Category.Trim();
        var name = request.Name.Trim();
        var moduleId = $"{category.ToLowerInvariant()}.{name.ToLowerInvariant()}";
        var baseNamespace = $"IntoChat.OS.Modules.{category}.{name}";
        var contractsNamespace = $"{baseNamespace}.Contracts";

        // Determine destination folder
        var moduleDir = _overrideRoot is not null
            ? Path.Combine(_overrideRoot, "src", "IntoChat.OS.Modules", category, name)
            : ResolveDefaultModuleDir(category, name);

        Directory.CreateDirectory(moduleDir);

        var createdFiles = new List<string>();

        // 1. Contracts project
        var contractsFolder = Path.Combine(moduleDir, "Contracts", $"{baseNamespace}.Contracts");
        Directory.CreateDirectory(contractsFolder);

        var contractsCsprojPath = Path.Combine(contractsFolder, $"{baseNamespace}.Contracts.csproj");
        var contractsCsproj =
$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <Description>{name} module contracts.</Description>
    <RootNamespace>{contractsNamespace}</RootNamespace>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DigitalBrain" />
  </ItemGroup>
</Project>
""";
        WriteFile(contractsCsprojPath, contractsCsproj, createdFiles);

        // Contracts interface and action records
        var interfaceName = $"I{name}Neuron";
        var neuronAlias = $"{moduleId}.main";

        var actionsBuilder = new System.Text.StringBuilder();
        foreach (var action in request.Actions ?? [])
        {
            if (string.IsNullOrWhiteSpace(action))
            {
                continue;
            }
            var actionTrimmed = action.Trim();
            var recordName = $"{actionTrimmed}Request";
            var resultName = $"{actionTrimmed}Result";
            actionsBuilder.AppendLine(
$$"""

public sealed record {{recordName}}(string Payload);
public sealed record {{resultName}}(string Detail, bool Success);
""");
        }

        var interfaceMethodsBuilder = new System.Text.StringBuilder();
        if (request.Actions is { Count: > 0 })
        {
            foreach (var action in request.Actions)
            {
                if (string.IsNullOrWhiteSpace(action))
                {
                    continue;
                }
                var actionTrimmed = action.Trim();
                interfaceMethodsBuilder.AppendLine(
$$"""
    Task<{{actionTrimmed}}Result> {{actionTrimmed}}({{actionTrimmed}}Request request, CancellationToken ct = default);
""");
            }
        }
        else
        {
            interfaceMethodsBuilder.AppendLine(
"""
    Task<string> Ping(string message, CancellationToken ct = default);
""");
        }

        var contractsInterfacePath = Path.Combine(contractsFolder, $"{interfaceName}.cs");
        var contractsInterface =
$$"""
using DigitalBrain.Contracts;

namespace {{contractsNamespace}};

[NeuronAlias("{{neuronAlias}}"), DefaultNeuronType("{{neuronAlias}}")]
public interface {{interfaceName}}
{
{{interfaceMethodsBuilder.ToString().TrimEnd()}}
}
{{actionsBuilder.ToString().TrimEnd()}}
""";
        WriteFile(contractsInterfacePath, contractsInterface, createdFiles);

        // 2. Implementation project
        var srcFolder = Path.Combine(moduleDir, "src", baseNamespace);
        Directory.CreateDirectory(srcFolder);

        var srcCsprojPath = Path.Combine(srcFolder, $"{baseNamespace}.csproj");
        var srcCsproj =
$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <Description>{name} module implementation.</Description>
    <RootNamespace>{baseNamespace}</RootNamespace>
    <NoWarn>$(NoWarn);ORLEANSEXP005;CA1812</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="{baseNamespace}.Tests.Unit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../Contracts/{baseNamespace}.Contracts/{baseNamespace}.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="DigitalBrain" />
    <PackageReference Include="DigitalBrain.Capabilities.Connections" />
    <PackageReference Include="DigitalBrain.Runtime.Orleans" />
  </ItemGroup>
</Project>
""";
        WriteFile(srcCsprojPath, srcCsproj, createdFiles);

        // Module Options
        var optionsName = $"{name}Options";
        var optionsPath = Path.Combine(srcFolder, $"{optionsName}.cs");
        var optionsContent =
$$"""
using DigitalBrain.Modules.Options;

namespace {{baseNamespace}};

public sealed class {{optionsName}} : IModuleOptions
{
    [Setting]
    public string ServiceEndpoint { get; set; } = "default";

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ServiceEndpoint);
    }
}
""";
        WriteFile(optionsPath, optionsContent, createdFiles);

        // Module class
        var moduleClassName = $"{name}Module";
        var secretsList = (request.Secrets ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => $"DigitalBrain.Modules.Secret.Named(\"{s.Trim()}\")").ToArray();
        var secretsParam = secretsList.Length > 0 ? string.Join(", ", secretsList) : "";

        var actionsList = (request.Actions ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => $"new DigitalBrain.Policies.ActionDescriptor(\"{moduleId}.{a.Trim().ToLowerInvariant()}\", \"{a.Trim()} action\", null)").ToArray();
        var actionsParam = actionsList.Length > 0 ? $"Actions = [{string.Join(", ", actionsList)}]" : "";

        var manifestInit = actionsParam.Length > 0
            ? $$"""
        new(
            Provides: [],
            Requires: [],
            Needs: [],
            Settings: [DigitalBrain.Modules.Setting.Of<{{optionsName}}>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
            Secrets: [{{secretsParam}}])
        {
            {{actionsParam}}
        };
"""
            : $$"""
        new(
            Provides: [],
            Requires: [],
            Needs: [],
            Settings: [DigitalBrain.Modules.Setting.Of<{{optionsName}}>(ModuleConfiguration.ModuleOptionsSection(ManifestName))],
            Secrets: [{{secretsParam}}]);
""";

        var modulePath = Path.Combine(srcFolder, $"{moduleClassName}.cs");
        var moduleContent =
$$"""
using DigitalBrain.Modules.Configuration;
using {{contractsNamespace}};
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace {{baseNamespace}};

[DigitalBrain.Modules.Module(ManifestName)]
public sealed class {{moduleClassName}} : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "{{moduleId}}";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } =
{{manifestInit}}

    public static System.Reflection.Assembly? Contracts => typeof({{interfaceName}}).Assembly;

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<{{optionsName}}>()
            .Validate(options => !string.IsNullOrWhiteSpace(options.ServiceEndpoint), "{{name}} service endpoint must not be empty.")
            .ValidateOnStart();
    }
}
""";
        WriteFile(modulePath, moduleContent, createdFiles);

        // Neuron implementation
        var neuronClassName = $"{name}Neuron";
        var neuronMethodsBuilder = new System.Text.StringBuilder();
        if (request.Actions is { Count: > 0 })
        {
            foreach (var action in request.Actions)
            {
                if (string.IsNullOrWhiteSpace(action))
                {
                    continue;
                }
                var actionTrimmed = action.Trim();
                neuronMethodsBuilder.AppendLine(
$$"""
    public Task<{{actionTrimmed}}Result> {{actionTrimmed}}({{actionTrimmed}}Request request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new {{actionTrimmed}}Result($"Executed {request.Payload}", true));
    }
""");
            }
        }
        else
        {
            interfaceMethodsBuilder.AppendLine(
"""
    public Task<string> Ping(string message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return Task.FromResult($"Pong: {message}");
    }
""");
        }

        var neuronPath = Path.Combine(srcFolder, $"{neuronClassName}.cs");
        var neuronContent =
$$"""
using DigitalBrain;
using DigitalBrain.Contracts;
using {{contractsNamespace}};
using Microsoft.Extensions.Options;

namespace {{baseNamespace}};

[Neuron("{{neuronAlias}}")]
internal sealed class {{neuronClassName}}(IOptions<{{optionsName}}> options) : DigitalBrain.Neurons.Neuron, {{interfaceName}}
{
{{neuronMethodsBuilder.ToString().TrimEnd()}}
}
""";
        WriteFile(neuronPath, neuronContent, createdFiles);

        // 3. Tests project
        var testFolder = Path.Combine(moduleDir, "tests", $"{baseNamespace}.Tests.Unit");
        Directory.CreateDirectory(testFolder);

        var testCsprojPath = Path.Combine(testFolder, $"{baseNamespace}.Tests.Unit.csproj");
        var testCsproj =
$"""
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="DigitalBrain.Testing" />
    <ProjectReference Include="../../src/{baseNamespace}/{baseNamespace}.csproj" />
  </ItemGroup>
</Project>
""";
        WriteFile(testCsprojPath, testCsproj, createdFiles);

        // Test facts
        var factsClassName = $"{name}ModuleFacts";
        var factsPath = Path.Combine(testFolder, $"{factsClassName}.cs");
        var factsContent =
$$"""
using DigitalBrain.Testing;

namespace {{baseNamespace}}.Tests;

public sealed class {{factsClassName}} : ModuleOnTestOSFacts<{{moduleClassName}}>
{
}
""";
        WriteFile(factsPath, factsContent, createdFiles);

        // 4. README.md
        var readmePath = Path.Combine(moduleDir, "README.md");
        var readmeBuilder = new System.Text.StringBuilder();
        readmeBuilder.AppendLine($"# {baseNamespace}");
        readmeBuilder.AppendLine();
        readmeBuilder.AppendLine($"{name} module implementation.");
        readmeBuilder.AppendLine();
        readmeBuilder.AppendLine($"Module id: `{moduleId}`.");
        readmeBuilder.AppendLine();
        readmeBuilder.AppendLine($"How to test: `dotnet test --project src/IntoChat.OS.Modules/{category}/{name}/tests/{baseNamespace}.Tests.Unit/{baseNamespace}.Tests.Unit.csproj`");
        readmeBuilder.AppendLine();
        readmeBuilder.AppendLine("Documentation: https://github.com/intochat/os.docs (modules).");
        readmeBuilder.AppendLine();
        readmeBuilder.AppendLine("<!-- BEGIN GENERATED options -->");
        readmeBuilder.AppendLine($"Configuration section: `IntoChatOS:Modules:{category}:{name}` (environment prefix `INTOCHATOS__MODULES__{category.ToUpperInvariant()}__{name.ToUpperInvariant()}__`).");
        readmeBuilder.AppendLine();
        readmeBuilder.AppendLine("Options:");
        readmeBuilder.AppendLine();
        readmeBuilder.AppendLine("| Option | Type | Default | Required |");
        readmeBuilder.AppendLine("| --- | --- | --- | --- |");
        readmeBuilder.AppendLine("| `ServiceEndpoint` | `string` | `\"default\"` | no |");
        readmeBuilder.AppendLine();
        readmeBuilder.AppendLine("Secrets (names only; values are sealed per brain, never configuration):");
        readmeBuilder.AppendLine();
        if (request.Secrets is { Count: > 0 })
        {
            readmeBuilder.AppendLine("| Secret | Required |");
            readmeBuilder.AppendLine("| --- | --- |");
            foreach (var secret in request.Secrets)
            {
                if (string.IsNullOrWhiteSpace(secret))
                {
                    continue;
                }
                readmeBuilder.AppendLine($"| `{secret.Trim()}` | yes |");
            }
            readmeBuilder.AppendLine();
        }
        else
        {
            readmeBuilder.AppendLine("None.");
            readmeBuilder.AppendLine();
        }
        readmeBuilder.AppendLine($"Contracts: assembly `{baseNamespace}.Contracts` (`{interfaceName}`).");
        readmeBuilder.AppendLine("Hosting: runs in process.");
        readmeBuilder.AppendLine("<!-- END GENERATED -->");

        WriteFile(readmePath, readmeBuilder.ToString(), createdFiles);

        // Also place a copy inside srcFolder for consistency with other modules if needed
        var srcReadmePath = Path.Combine(srcFolder, "README.md");
        WriteFile(srcReadmePath, readmeBuilder.ToString(), createdFiles);

        return Task.FromResult(new ScaffoldModuleResult(moduleDir, createdFiles));
    }

    private static string ResolveDefaultModuleDir(string category, string name)
    {
        var repoRoot = FindRepoRoot();
        return repoRoot is not null
            ? Path.Combine(repoRoot, "src", "IntoChat.OS.Modules", category, name)
            : Path.Combine(Directory.GetCurrentDirectory(), category, name);
    }

    private static void WriteFile(string path, string content, List<string> createdFiles)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\n", "\r\n");
        File.WriteAllText(path, normalized, new System.Text.UTF8Encoding(false));
        createdFiles.Add(path);
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx")))
            {
                return dir.FullName;
            }
        }
        return null;
    }
}
