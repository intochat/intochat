using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Units;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Outcome of verifying and optionally deploying an imported micro-app folder or ZIP archive (<c>F-APP-05</c>).
/// </summary>
internal sealed record AppPackageImportResult(
    bool Succeeded,
    string Alias,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<string> Errors,
    DiscoveredAppSkill? Skill = null,
    UnitDeployResult? DeployResult = null);

/// <summary>
/// Exports and imports portable micro-app folders and <c>.zip</c> archives (<c>F-APP-05</c>) preserving
/// <c>{*.cs, scenarios.json, SKILL.md, spec.md}</c>, enforcing path-confinement, Roslyn closed-world boundary
/// verification, and <see cref="BuilderTools.Check"/> scenario execution before deployment.
/// </summary>
internal static partial class AppPackageArchive
{
    public const int MaxArchiveBytes = 4 * 1024 * 1024;
    public const int MaxEntryCount = 32;

    private static readonly HashSet<string> AllowedSidecarNames = new(StringComparer.OrdinalIgnoreCase)
    {
        UnitFiles.Scenarios,
        UnitFiles.Spec,
        SkillDiscovery.FileName,
    };

    public static async Task<byte[]> ExportZipAsync(string alias, ISynapseStudio studio, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(studio);

        var files = await CollectUnitFilesAsync(alias.Trim(), studio, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new KeyNotFoundException($"No deployed unit files found for '{alias}'.");
        }

        return ExportZip(alias.Trim(), files);
    }

    public static byte[] ExportZip(string alias, IReadOnlyDictionary<string, string> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(files);

        var portable = FilterPortableFiles(files);
        if (UnitFiles.Code(portable).Count == 0)
        {
            throw new ArgumentException($"Cannot export '{alias}': package contains no .cs source files.", nameof(files));
        }

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in portable.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
        }

        return ms.ToArray();
    }

    public static async Task ExportToDirectoryAsync(
        string alias,
        string directoryPath,
        ISynapseStudio studio,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(studio);

        var files = await CollectUnitFilesAsync(alias.Trim(), studio, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new KeyNotFoundException($"No deployed unit files found for '{alias}'.");
        }

        await ExportToDirectoryAsync(alias.Trim(), directoryPath, files, ct).ConfigureAwait(false);
    }

    public static async Task ExportToDirectoryAsync(
        string alias,
        string directoryPath,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(files);

        var portable = FilterPortableFiles(files);
        if (UnitFiles.Code(portable).Count == 0)
        {
            throw new ArgumentException($"Cannot export '{alias}': package contains no .cs source files.", nameof(files));
        }

        var fullDir = Path.GetFullPath(directoryPath);
        Directory.CreateDirectory(fullDir);
        foreach (var (name, content) in portable)
        {
            ct.ThrowIfCancellationRequested();
            var destPath = Path.GetFullPath(Path.Combine(fullDir, name));
            if (!destPath.StartsWith(fullDir + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsafe export entry path '{name}'.");
            }
            await File.WriteAllTextAsync(destPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
        }
    }

    public static async Task<AppPackageImportResult> ImportZipAsync(
        byte[] archiveBytes,
        ISynapseStudio? studio = null,
        string? aliasOverride = null,
        IReadOnlyDictionary<string, string>? bindings = null,
        IServiceProvider? services = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);

        if (archiveBytes.Length == 0 || archiveBytes.Length > MaxArchiveBytes)
        {
            return new AppPackageImportResult(false, aliasOverride ?? "", new Dictionary<string, string>(), [$"Archive size must be between 1 and {MaxArchiveBytes} bytes."]);
        }

        Dictionary<string, string> extracted;
        try
        {
            extracted = ExtractZipEntries(archiveBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            return new AppPackageImportResult(false, aliasOverride ?? "", new Dictionary<string, string>(), [ex.Message]);
        }

        return await VerifyAndDeployFilesAsync(extracted, studio, aliasOverride, bindings, services, ct).ConfigureAwait(false);
    }

    public static async Task<AppPackageImportResult> ImportFromDirectoryAsync(
        string directoryPath,
        ISynapseStudio? studio = null,
        string? aliasOverride = null,
        IReadOnlyDictionary<string, string>? bindings = null,
        IServiceProvider? services = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var fullDir = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullDir))
        {
            return new AppPackageImportResult(false, aliasOverride ?? "", new Dictionary<string, string>(), [$"Directory '{directoryPath}' does not exist."]);
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var filePath in Directory.EnumerateFiles(fullDir, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(filePath);
            if (!IsAllowedPackageFileName(name))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct).ConfigureAwait(false);
            if (content.Length > BuilderTools.MaxSidecarChars)
            {
                return new AppPackageImportResult(false, aliasOverride ?? "", new Dictionary<string, string>(), [$"File '{name}' exceeds the {BuilderTools.MaxSidecarChars}-character cap."]);
            }

            files[CanonicalSidecarOrCodeName(name)] = content;
        }

        var defaultAlias = aliasOverride ?? Path.GetFileName(fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return await VerifyAndDeployFilesAsync(files, studio, defaultAlias, bindings, services, ct).ConfigureAwait(false);
    }

    public static async Task<AppPackageImportResult> VerifyAndDeployFilesAsync(
        IReadOnlyDictionary<string, string> rawFiles,
        ISynapseStudio? studio = null,
        string? aliasOverride = null,
        IReadOnlyDictionary<string, string>? bindings = null,
        IServiceProvider? services = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawFiles);

        var files = FilterPortableFiles(rawFiles);
        var code = UnitFiles.Code(files);
        if (code.Count == 0)
        {
            return new AppPackageImportResult(false, aliasOverride ?? "", files, ["Imported package contains no .cs source files."]);
        }

        var inferredAlias = InferStateAlias(code.Values);
        var alias = !string.IsNullOrWhiteSpace(inferredAlias)
            ? inferredAlias!
            : (aliasOverride?.Trim() ?? "");

        if (string.IsNullOrWhiteSpace(alias))
        {
            return new AppPackageImportResult(false, "", files, ["Could not determine unit [State(\"<alias>\")] from .cs source files."]);
        }

        if (!string.IsNullOrWhiteSpace(aliasOverride) && !string.IsNullOrWhiteSpace(inferredAlias)
            && !string.Equals(aliasOverride.Trim(), inferredAlias, StringComparison.Ordinal))
        {
            // Keep the [State("<alias>")] declared by the C# unit authoritative unless caller explicitly asked for another without [State] match
            alias = inferredAlias;
        }

        IReadOnlyList<Scenario> scenarios = [];
        if (files.TryGetValue(UnitFiles.Scenarios, out var scenariosJson) && !string.IsNullOrWhiteSpace(scenariosJson))
        {
            try
            {
                scenarios = JsonSerializer.Deserialize<Scenario[]>(scenariosJson, JsonSerializerOptions.Web) ?? [];
            }
            catch (JsonException ex)
            {
                return new AppPackageImportResult(false, alias, files, [$"Invalid scenarios.json: {ex.Message}"]);
            }
        }

        var check = await BuilderTools.VerifyPackageAsync(services, alias, files, scenarios, ct).ConfigureAwait(false);
        if (!check.Passed)
        {
            var errors = check.Diagnostics
                .Select(d => $"{d.File}({d.Line}) {d.Id}: {d.Message}")
                .Concat(check.Failures)
                .ToArray();
            return new AppPackageImportResult(false, alias, files, errors);
        }

        var skill = SkillDiscovery.FindSkillSidecar(files) is { Length: > 0 } skillMd
            ? SkillDiscovery.ParseSkillMarkdown(alias, skillMd, "imported-package")
            : null;

        UnitDeployResult? deployResult = null;
        if (studio is not null)
        {
            deployResult = await studio.DeployFiles(
                alias,
                files,
                bindings ?? new Dictionary<string, string>(StringComparer.Ordinal)).ConfigureAwait(false);
            if (!deployResult.Ok)
            {
                return new AppPackageImportResult(false, alias, files, [deployResult.Text], skill, deployResult);
            }
        }

        return new AppPackageImportResult(true, alias, files, [], skill, deployResult);
    }

    private static async Task<Dictionary<string, string>> CollectUnitFilesAsync(string alias, ISynapseStudio studio, CancellationToken ct)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in await studio.SourceFiles(alias).WaitAsync(ct).ConfigureAwait(false))
        {
            if (IsAllowedPackageFileName(path))
            {
                files[CanonicalSidecarOrCodeName(path)] = content;
            }
        }

        foreach (var (path, content) in await studio.Sidecars(alias).WaitAsync(ct).ConfigureAwait(false))
        {
            if (IsAllowedPackageFileName(path))
            {
                files[CanonicalSidecarOrCodeName(path)] = content;
            }
        }

        return files;
    }

    private static Dictionary<string, string> ExtractZipEntries(byte[] archiveBytes)
    {
        using var ms = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("Archive contains no files.");
        }
        if (archive.Entries.Count > MaxEntryCount)
        {
            throw new InvalidDataException($"Archive contains {archive.Entries.Count} entries, exceeding the {MaxEntryCount}-entry cap.");
        }

        // Allow either flat zip entries (`HelloWorld.cs`, `scenarios.json`, `SKILL.md`) or a single top-level folder (`helloworld/HelloWorld.cs`)
        var rawPaths = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => ValidateSafeZipPath(e.FullName))
            .ToArray();

        string? commonFolder = null;
        if (rawPaths.Length > 0 && rawPaths.All(p => p.Count(c => c == '/') == 1))
        {
            var prefixes = rawPaths.Select(p => p[..p.IndexOf('/', StringComparison.Ordinal)]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (prefixes.Length == 1)
            {
                commonFolder = prefixes[0] + "/";
            }
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var safePath = ValidateSafeZipPath(entry.FullName);
            if (commonFolder is not null && safePath.StartsWith(commonFolder, StringComparison.OrdinalIgnoreCase))
            {
                safePath = safePath[commonFolder.Length..];
            }

            if (safePath.Contains('/', StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Nested subdirectories are not permitted in unit package entry '{entry.FullName}'.");
            }

            if (!IsAllowedPackageFileName(safePath))
            {
                throw new InvalidDataException($"Disallowed file '{safePath}' in unit package archive. Only .cs, scenarios.json, SKILL.md, and spec.md are permitted.");
            }

            if (entry.Length > BuilderTools.MaxSidecarChars * 4L)
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' exceeds maximum allowed size.");
            }

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            if (text.Length > BuilderTools.MaxSidecarChars)
            {
                throw new InvalidDataException($"Archive entry '{entry.FullName}' exceeds {BuilderTools.MaxSidecarChars} characters.");
            }

            files[CanonicalSidecarOrCodeName(safePath)] = text;
        }

        return files;
    }

    private static string ValidateSafeZipPath(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new InvalidDataException("Archive entry path must not be empty.");
        }

        var trimmed = fullName.Trim();
        if (Path.IsPathRooted(fullName) || !SafeRelativePath.TryParse(trimmed, out var safe))
        {
            throw new InvalidDataException($"Rooted, drive-qualified, or escaping path '{fullName}' is forbidden in unit archive.");
        }

        foreach (var seg in safe.Value.Split('/', StringSplitOptions.None))
        {
            if (seg.StartsWith('.'))
            {
                throw new InvalidDataException($"Unsafe path segment in archive entry '{fullName}'.");
            }
        }

        return safe.Value;
    }

    private static Dictionary<string, string> FilterPortableFiles(IReadOnlyDictionary<string, string> source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawKey, content) in source)
        {
            var leaf = ValidateSafeZipPath(rawKey);
            if (leaf.Contains('/', StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Package file name '{rawKey}' must not contain directory separators.");
            }
            if (IsAllowedPackageFileName(leaf))
            {
                result[CanonicalSidecarOrCodeName(leaf)] = content;
            }
        }
        return result;
    }

    private static bool IsAllowedPackageFileName(string name)
        => name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || AllowedSidecarNames.Contains(name);

    private static string CanonicalSidecarOrCodeName(string name)
    {
        if (string.Equals(name, UnitFiles.Scenarios, StringComparison.OrdinalIgnoreCase)) { return UnitFiles.Scenarios; }
        if (string.Equals(name, UnitFiles.Spec, StringComparison.OrdinalIgnoreCase)) { return UnitFiles.Spec; }
        if (string.Equals(name, SkillDiscovery.FileName, StringComparison.OrdinalIgnoreCase)) { return SkillDiscovery.FileName; }
        return name;
    }

    private static string? InferStateAlias(IEnumerable<string> codeFiles)
    {
        foreach (var source in codeFiles)
        {
            if (StateAttributePattern().Match(source) is { Success: true } match)
            {
                return match.Groups[1].Value;
            }
        }
        return null;
    }

    [GeneratedRegex(@"\[\s*(?:DigitalBrain\.)?State\s*\(\s*""([^""]+)""\s*\)\s*\]")]
    private static partial Regex StateAttributePattern();
}
