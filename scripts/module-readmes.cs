#!/usr/bin/env -S dotnet --
#:project ../src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj
#:property DisableMSBuildAssemblyCopyCheck=true
#:property PublishAot=false
// Generates the options/secrets table of every module README: `dotnet run scripts/module-readmes.cs [-- --check]`.
// Reflects over OsCatalog.Modules plus the Windows edge. Writes only between the BEGIN/END GENERATED markers, and creates a
// short README (title, description, how to test, docs link) where a module has none. Secrets are listed by NAME from
// [Secret] attributes; no configuration is loaded and no value is read. --check changes nothing and exits 1 listing stale files.
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Modules;
using Module = IntoChat.OS.Framework.Modules.Module;
using IntoChat.OS.Kernel;

const string Begin = "<!-- BEGIN GENERATED options -->";
const string End = "<!-- END GENERATED -->";
var root = AppContext.GetData("EntryPointFileDirectoryPath") as string is { } entry
    ? Path.GetFullPath(Path.Combine(entry, ".."))
    : Directory.GetCurrentDirectory();
var check = args.Contains("--check");

var projects = XDocument.Load(Path.Combine(root, "Intochat.OS.slnx")).Descendants("Project")
    .Select(project => project.Attribute("Path")!.Value.Replace('\\', '/'))
    .ToArray();
string? Csproj(string assembly) => projects.FirstOrDefault(path => Path.GetFileNameWithoutExtension(path) == assembly);

var stale = new List<string>();
var written = 0;
var seen = 0;
foreach (var module in OsCatalog.WithWindowsEdge().DistinctBy(m => m.Id.ToString()))
{
    var type = module.GetType();
    var assembly = type.Assembly.GetName().Name!;
    var csprojRelative = Csproj(assembly) ?? throw new InvalidOperationException($"{assembly} is not in Intochat.OS.slnx.");
    var csproj = Path.Combine(root, csprojRelative);
    var folder = Path.GetDirectoryName(csproj)!;
    var readme = Path.Combine(folder, "README.md");
    var description = XDocument.Load(csproj).Descendants("Description").FirstOrDefault()?.Value.Trim() ?? "";
    var moduleRoot = (Path.GetDirectoryName(csprojRelative) ?? "").Replace('\\', '/') + "/";
    var testProject = projects
        .Where(path => path.StartsWith(Path.GetDirectoryName(moduleRoot.TrimEnd('/'))!.Replace('\\', '/') + "/", StringComparison.Ordinal)
            && path.Contains("/tests/", StringComparison.Ordinal)
            && Path.GetFileNameWithoutExtension(path).StartsWith(assembly + ".", StringComparison.Ordinal))
        .OrderBy(path => path.Contains(".Tests.Unit", StringComparison.Ordinal) ? 0 : 1).ThenBy(path => path, StringComparer.Ordinal)
        .FirstOrDefault();

    seen++;
    var table = Render(module, type);
    var existing = File.Exists(readme) ? File.ReadAllText(readme).Replace("\r\n", "\n") : null;
    var updated = existing is null ? Stub(module, assembly, description, testProject) + "\n" + Begin + "\n" + table + End + "\n" : Replace(existing, table);
    if (existing == updated) { continue; }
    stale.Add(Path.GetRelativePath(root, readme).Replace('\\', '/'));
    if (!check) { File.WriteAllText(readme, updated.Replace("\n", "\r\n"), new UTF8Encoding(false)); written++; }
}

if (check && stale.Count > 0)
{
    Console.Error.WriteLine("Module READMEs are stale:\n  " + string.Join("\n  ", stale) + "\nFix: dotnet run scripts/module-readmes.cs");
    return 1;
}
Console.WriteLine(check ? $"Module READMEs are current ({seen})." : $"Module READMEs: {seen} modules, {written} written.");
return 0;

static string Replace(string text, string table)
{
    var start = text.IndexOf(Begin, StringComparison.Ordinal);
    var end = text.IndexOf(End, StringComparison.Ordinal);
    var block = Begin + "\n" + table + End;
    if (start < 0 || end < start) { return text.TrimEnd('\n') + "\n\n" + block + "\n"; }
    return text[..start] + block + text[(end + End.Length)..];
}

static string Stub(Module module, string assembly, string description, string? testProject)
{
    var builder = new StringBuilder();
    builder.Append($"# {assembly}\n\n");
    if (description.Length > 0) { builder.Append(description).Append("\n\n"); }
    builder.Append($"Module id: `{module.Id}`.\n\n");
    builder.Append(testProject is null ? "How to test: this module has no tests project yet.\n\n"
        : $"How to test: `dotnet test --project {testProject}`\n\n");
    builder.Append("Documentation: https://github.com/intochat/os.docs (modules).\n");
    return builder.ToString();
}

static string Render(Module module, Type type)
{
    var builder = new StringBuilder();
    var section = IntoChatOSConfiguration.ModuleOptionsSection(module.Id);
    builder.Append($"Configuration section: `{section}` (environment prefix `{IntoChatOSConfiguration.EnvironmentName(section)}__`).\n\n");

    var optionsType = OptionsType(type);
    var rows = new List<string>();
    if (optionsType is not null)
    {
        var defaults = Activator.CreateInstance(optionsType);
        foreach (var property in optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var setting = property.GetCustomAttribute<SettingAttribute>();
            if (setting is null) { continue; }
            var value = property.GetIndexParameters().Length == 0 ? property.GetValue(defaults) : null;
            var shown = value is null ? "unset" : value is System.Collections.IEnumerable and not string ? "(collection)" : $"`{value}`";
            rows.Add($"| `{property.Name}` | `{TypeName(property.PropertyType)}` | {shown} | {(setting.Required ? "yes" : "no")} |");
        }
    }
    foreach (var setting in type.GetCustomAttributes<SettingAttribute>().OrderBy(s => s.Name, StringComparer.Ordinal))
    {
        rows.Add($"| `{setting.Name}` | integration setting | unset | {(setting.Optional ? "no" : "yes")} |");
    }
    builder.Append("Options:\n\n");
    if (rows.Count == 0) { builder.Append("None.\n\n"); }
    else
    {
        builder.Append("| Option | Type | Default | Required |\n| --- | --- | --- | --- |\n");
        foreach (var row in rows) { builder.Append(row).Append('\n'); }
        builder.Append('\n');
    }

    var secrets = type.GetCustomAttributes<SecretAttribute>().OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
    builder.Append("Secrets (names only; values are sealed per brain, never configuration):\n\n");
    if (secrets.Length == 0) { builder.Append("None.\n\n"); }
    else
    {
        builder.Append("| Secret | Required |\n| --- | --- |\n");
        foreach (var secret in secrets) { builder.Append($"| `{secret.Name}` | {(secret.Optional ? "no" : "yes")} |\n"); }
        builder.Append('\n');
    }

    var contracts = module.Contracts;
    builder.Append("Contracts: ");
    if (contracts is null) { builder.Append("none.\n"); }
    else
    {
        var names = contracts.GetExportedTypes().Where(t => t.IsInterface).Select(t => $"`{t.Name}`").Order(StringComparer.Ordinal).ToArray();
        builder.Append($"assembly `{contracts.GetName().Name}`{(names.Length > 0 ? " (" + string.Join(", ", names) + ")" : "")}.\n");
    }
    builder.Append(module.RequiresContainer ? "Hosting: needs a container.\n" : "Hosting: runs in process.\n");
    return builder.ToString();
}

static Type? OptionsType(Type type)
{
    for (var current = type; current is not null; current = current.BaseType)
    {
        if (current.IsGenericType && current.GetGenericTypeDefinition().Name.StartsWith("Module`", StringComparison.Ordinal)) { return current.GetGenericArguments()[0]; }
    }
    return null;
}

static string TypeName(Type type)
    => Nullable.GetUnderlyingType(type) is { } inner ? TypeName(inner) + "?"
        : type.IsGenericType ? type.Name[..type.Name.IndexOf('`')] + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">"
        : type.Name;
