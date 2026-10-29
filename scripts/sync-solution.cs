#!/usr/bin/env -S dotnet --
// Rewrites Intochat.OS.slnx from the projects on disk: `dotnet run scripts/sync-solution.cs [-- --check]`.
// Every src/**/*.csproj (bin, obj and templates/ excluded) gets exactly one entry in the solution folder its directory
// implies (src/IntoChat.OS.Modules/AI/... -> /Modules/AI/, "IntoChat.OS." dropped from each segment, a direct child of
// src has no folder). A module tests/ project keeps whichever of its two allowed folders it already sits in. Files,
// empty folders and folder order are preserved; projects are written in ordinal order. --check changes nothing and
// exits 1 listing drift (RepositoryLayoutFacts holds the same rule). The folded-in framework (06-00) is walked too:
// framework/{src,tests,templates}/<Name>/<Name>.csproj sits in /framework/src/, /framework/tests/ or /framework/templates/
// (template payloads under content/ are not projects of the solution). Compiled feature projects under
// features/<owner>/<app>/compiled/ sit in /features/<owner>/<app>/.
using System.Text;
using System.Xml;
using System.Xml.Linq;

const string Prefix = "IntoChat.OS.";
var root = AppContext.GetData("EntryPointFileDirectoryPath") as string is { } entry
    ? Path.GetFullPath(Path.Combine(entry, ".."))
    : Directory.GetCurrentDirectory();
var check = args.Contains("--check");
var slnx = Path.Combine(root, "Intochat.OS.slnx");
var document = XDocument.Load(slnx);
var solution = document.Root!;

var disk = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
    .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
    .Where(path => !path.Split('/').Any(part => part is "bin" or "obj" or "templates"))
    .Concat(Directory.EnumerateFiles(Path.Combine(root, "features"), "*.csproj", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
        .Where(path => !path.Split('/').Any(part => part is "bin" or "obj")))
    .Concat((Directory.Exists(Path.Combine(root, "framework")) ? Directory.EnumerateFiles(Path.Combine(root, "framework"), "*.csproj", SearchOption.AllDirectories) : [])
        .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
        .Where(path => !path.Split('/').Any(part => part is "bin" or "obj" or "content")))
    .Order(StringComparer.Ordinal)
    .ToArray();

var current = solution.Descendants("Project")
    .ToDictionary(project => project.Attribute("Path")!.Value.Replace(Path.DirectorySeparatorChar, '/'), project => project.Parent!.Name.LocalName == "Folder" ? project.Parent.Attribute("Name")!.Value : null);

static string?[] Allowed(string path)
{
    var group = path[..path.LastIndexOf('/', path.LastIndexOf('/') - 1)];
    if (group.StartsWith("framework/", StringComparison.Ordinal) || group.StartsWith("features/", StringComparison.Ordinal)) { return ["/" + group + "/"]; }
    if (group == "src") { return [null]; }
    var segments = group["src/".Length..].Split('/').Select(s => s.StartsWith(Prefix, StringComparison.Ordinal) ? s[Prefix.Length..] : s).ToArray();
    var folder = "/" + string.Join('/', segments) + "/";
    return segments[^1] == "tests" ? [folder, "/" + string.Join('/', segments[..^1]) + "/"] : [folder];
}

var wanted = disk.ToDictionary(path => path, path =>
{
    var allowed = Allowed(path);
    return current.TryGetValue(path, out var existing) && allowed.Contains(existing) ? existing : allowed[^1];
});

var drift = new List<string>();
foreach (var path in disk.Where(path => !current.ContainsKey(path))) { drift.Add($"missing from the solution: {path}"); }
foreach (var path in current.Keys.Where(path => !wanted.ContainsKey(path))) { drift.Add($"in the solution but not on disk: {path}"); }
foreach (var path in disk.Where(path => current.TryGetValue(path, out var folder) && folder != wanted[path])) { drift.Add($"wrong folder ({current[path] ?? "<root>"} -> {wanted[path] ?? "<root>"}): {path}"); }

var folders = solution.Elements("Folder").ToDictionary(folder => folder.Attribute("Name")!.Value, StringComparer.Ordinal);
foreach (var project in solution.Descendants("Project").ToArray()) { project.Remove(); }
foreach (var name in wanted.Values.Where(name => name is not null).Distinct().Order(StringComparer.Ordinal))
{
    if (folders.ContainsKey(name!)) { continue; }
    var created = new XElement("Folder", new XAttribute("Name", name!));
    folders[name!] = created;
    var before = solution.Elements("Folder").FirstOrDefault(folder => string.CompareOrdinal(folder.Attribute("Name")!.Value, name) > 0
        && folder.Attribute("Name")!.Value != "/Solution Items/");
    if (before is not null) { before.AddBeforeSelf(created); } else { solution.Add(created); }
}
foreach (var group in wanted.GroupBy(pair => pair.Value))
{
    var elements = group.Select(pair => pair.Key).Order(StringComparer.OrdinalIgnoreCase).Select(path => new XElement("Project", new XAttribute("Path", path)));
    if (group.Key is null) { solution.Add(elements); } else { folders[group.Key].Add(elements); }
}
// A folder that held projects and now holds none is dropped; folders that were always empty or carry files stay.
foreach (var folder in solution.Elements("Folder").Where(folder => !folder.HasElements && current.Values.Contains(folder.Attribute("Name")!.Value) && !wanted.Values.Contains(folder.Attribute("Name")!.Value)).ToArray())
{
    folder.Remove();
}

var text = new StringBuilder();
using (var writer = XmlWriter.Create(text, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = "\r\n", NewLineHandling = NewLineHandling.Replace }))
{
    document.Save(writer);
}
var next = text.ToString().Replace("\"></Folder>", "\" />", StringComparison.Ordinal).TrimEnd() + "\r\n";
var existingText = File.ReadAllText(slnx);
if (check)
{
    if (next != existingText && drift.Count == 0) { drift.Add("Intochat.OS.slnx text differs from the canonical order/format; run `dotnet run scripts/sync-solution.cs`"); }
    foreach (var line in drift) { Console.Error.WriteLine(line); }
    return drift.Count == 0 ? 0 : 1;
}
if (next != existingText) { File.WriteAllText(slnx, next, new UTF8Encoding(false)); Console.WriteLine($"Intochat.OS.slnx updated ({drift.Count} drift items)"); }
else { Console.WriteLine("Intochat.OS.slnx already in sync"); }
return 0;
