using System.Reflection;
using System.Text;
using System.Xml.Linq;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class ModuleDocumentationFacts
{
    private const string Begin = "<!-- BEGIN GENERATED options -->";
    private const string End = "<!-- END GENERATED -->";

    [Fact]
    public void EveryComposedModuleHasAReadmeMatchingItsManifest()
    {
        ModuleConfigurationOptions.Current = new() { Root = "IntoChatOS" };

        var root = Repository.Root;
        var updateDocs = Environment.GetEnvironmentVariable("UPDATE_DOCS") == "1";

        var projects = XDocument.Load(Path.Combine(root, "Intochat.OS.slnx")).Descendants("Project")
            .Select(project => project.Attribute("Path")!.Value.Replace('\\', '/'))
            .ToArray();

        string? Csproj(string assembly) => projects.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path) == assembly);

        var stale = new List<string>();
        var readmes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in OperatingSystemModules.WithWindowsEdge().DistinctBy(m => m.Id.ToString())
            .OrderBy(m => m.Type.Name.EndsWith("Module", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(m => m.Id.Value, StringComparer.Ordinal))
        {
            var type = module.Type;
            var assembly = type.Assembly.GetName().Name!;
            var csprojRelative = Csproj(assembly);
            if (csprojRelative is null)
            {
                continue;
            }

            var csproj = Path.Combine(root, csprojRelative);
            var folder = Path.GetDirectoryName(csproj)!;
            var readme = Path.Combine(folder, "README.md");
            if (!readmes.Add(readme))
            {
                continue;
            }

            var description = XDocument.Load(csproj).Descendants("Description").FirstOrDefault()?.Value.Trim() ?? "";
            var moduleRoot = (Path.GetDirectoryName(csprojRelative) ?? "").Replace('\\', '/') + "/";
            var testProject = projects
                .Where(path => path.StartsWith(Path.GetDirectoryName(moduleRoot.TrimEnd('/'))!.Replace('\\', '/') + "/", StringComparison.Ordinal)
                    && path.Contains("/tests/", StringComparison.Ordinal)
                    && Path.GetFileNameWithoutExtension(path).StartsWith(assembly + ".", StringComparison.Ordinal))
                .OrderBy(path => path.Contains(".Tests.Unit", StringComparison.Ordinal) ? 0 : 1).ThenBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();

            var table = Render(module, type);
            var existing = File.Exists(readme) ? File.ReadAllText(readme).Replace("\r\n", "\n") : null;
            var updated = existing is null ? Stub(module, assembly, description, testProject) + "\n" + Begin + "\n" + table + End + "\n" : Replace(existing, table);
            if (existing == updated)
            {
                continue;
            }

            var relReadme = Path.GetRelativePath(root, readme).Replace('\\', '/');
            stale.Add(relReadme);

            if (updateDocs)
            {
                File.WriteAllText(readme, updated.Replace("\n", "\r\n"), new UTF8Encoding(false));
            }
        }

        Assert.True(stale.Count == 0,
            "Module READMEs are stale:\n  " + string.Join("\n  ", stale) + "\nFix with: UPDATE_DOCS=1 dotnet test --filter ModuleDocumentationFacts");
    }

    private static string Replace(string text, string table)
    {
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        var block = Begin + "\n" + table + End;
        if (start < 0 || end < start)
        {
            return text.TrimEnd('\n') + "\n\n" + block + "\n";
        }
        return text[..start] + block + text[(end + End.Length)..];
    }

    private static string Stub(OsModule module, string assembly, string description, string? testProject)
    {
        var builder = new StringBuilder();
        builder.Append($"# {assembly}\n\n");
        if (description.Length > 0)
        {
            builder.Append(description).Append("\n\n");
        }
        builder.Append($"Module id: `{module.Id}`.\n\n");
        builder.Append(testProject is null ? "How to test: this module has no tests project yet.\n\n"
            : $"How to test: `dotnet test --project {testProject}`\n\n");
        builder.Append("Documentation: https://github.com/intochat/os.docs (modules).\n");
        return builder.ToString();
    }

    private static string Render(OsModule module, Type type)
    {
        var builder = new StringBuilder();
        var section = ModuleConfiguration.ModuleOptionsSection(module.Id);
        builder.Append($"Configuration section: `{section}` (environment prefix `{ModuleConfiguration.EnvironmentName(section)}__`).\n\n");

        var optionsType = OptionsType(type);
        var rows = new List<string>();
        if (optionsType is not null)
        {
            var defaults = Activator.CreateInstance(optionsType);
            foreach (var property in optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var setting = property.GetCustomAttribute<SettingAttribute>();
                if (setting is null)
                {
                    continue;
                }
                var value = property.GetIndexParameters().Length == 0 ? property.GetValue(defaults) : null;
                var shown = value is null ? "unset" : value is System.Collections.IEnumerable and not string ? "(collection)" : $"`{value}`";
                rows.Add($"| `{property.Name}` | `{TypeName(property.PropertyType)}` | {shown} | {(setting.Required ? "yes" : "no")} |");
            }
        }
        foreach (var setting in SchemaSettings(type).OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            rows.Add($"| `{setting.Name}` | integration setting | unset | {(setting.Optional ? "no" : "yes")} |");
        }
        builder.Append("Options:\n\n");
        if (rows.Count == 0)
        {
            builder.Append("None.\n\n");
        }
        else
        {
            builder.Append("| Option | Type | Default | Required |\n| --- | --- | --- | --- |\n");
            foreach (var row in rows)
            {
                builder.Append(row).Append('\n');
            }
            builder.Append('\n');
        }

        var secrets = SchemaSecrets(type).OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
        builder.Append("Secrets (names only; values are sealed per brain, never configuration):\n\n");
        if (secrets.Length == 0)
        {
            builder.Append("None.\n\n");
        }
        else
        {
            builder.Append("| Secret | Required |\n| --- | --- |\n");
            foreach (var secret in secrets)
            {
                builder.Append($"| `{secret.Name}` | {(secret.Optional ? "no" : "yes")} |\n");
            }
            builder.Append('\n');
        }

        var contracts = module.Contracts;
        builder.Append("Contracts: ");
        if (contracts is null)
        {
            builder.Append("none.\n");
        }
        else
        {
            var names = contracts.GetExportedTypes().Where(t => t.IsInterface).Select(t => $"`{t.Name}`").Order(StringComparer.Ordinal).ToArray();
            builder.Append($"assembly `{contracts.GetName().Name}`{(names.Length > 0 ? " (" + string.Join(", ", names) + ")" : "")}.\n");
        }
        builder.Append(module.RequiresContainer ? "Hosting: needs a container.\n" : "Hosting: runs in process.\n");
        return builder.ToString();
    }

    private static Type? OptionsType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition().Name.StartsWith("Module`", StringComparison.Ordinal))
            {
                return current.GetGenericArguments()[0];
            }
        }
        var manifest = type.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (manifest?.GetType().GetProperty("Settings")?.GetValue(manifest) is System.Collections.IEnumerable settings)
        {
            foreach (var setting in settings)
            {
                if (setting?.GetType().GetProperty("OptionsType")?.GetValue(setting) is Type options)
                {
                    return options;
                }
            }
        }
        return null;
    }

    private static string TypeName(Type type)
        => Nullable.GetUnderlyingType(type) is { } inner ? TypeName(inner) + "?"
            : type.IsGenericType ? type.Name[..type.Name.IndexOf('`')] + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">"
            : type.Name;

    private static IEnumerable<(string Name, bool Optional)> SchemaSettings(Type type)
    {
        foreach (var schema in OsModule.ReadSchemas(type))
        {
            foreach (var name in schema.SettingFields)
            {
                yield return (name, false);
            }
            foreach (var name in schema.OptionalSettingFields)
            {
                yield return (name, true);
            }
        }
    }

    private static IEnumerable<(string Name, bool Optional)> SchemaSecrets(Type type)
    {
        var manifest = type.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DigitalBrain.ModuleManifest;
        if (manifest is not null)
        {
            foreach (var secret in manifest.Secrets)
            {
                yield return (secret.Name, false);
            }
        }
        foreach (var schema in OsModule.ReadSchemas(type))
        {
            foreach (var name in schema.SecretFields)
            {
                yield return (name, false);
            }
            foreach (var name in schema.OptionalSecretFields)
            {
                yield return (name, true);
            }
        }
    }
}
