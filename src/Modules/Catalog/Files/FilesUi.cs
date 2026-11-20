using System.Globalization;
using System.Text;
using System.Text.Json;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using IntoChat.OS.UI;
using IntoChat.OS.Host.Primitives;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Shared domain <see cref="Rule"/> invariants for <c>Files</c> path, name, and inline write validation (<c>UK-17</c>).
/// Enforced identically on UI controls (<c>.Guard(rule)</c>) and inside <see cref="FilesWorkspaceNeuron"/>.
/// </summary>
internal static class FilesPathRules
{
    public const int MaxInlineTextBytes = 256 * 1024;

    public static Rule ValidateSingleSegmentName(string? name) => Decision.Require(
        "single_segment_name",
        name,
        static candidate => !string.IsNullOrWhiteSpace(candidate)
            && SafeRelativePath.TryParse(candidate, out var safe)
            && !safe.Value.Contains('/', StringComparison.Ordinal),
        "A valid single-segment file or folder name is required.");

    public static Rule ValidateInlineTextSize(string? text) => Decision.Rule(
        "inline_text_limit",
        text,
        static content => !string.IsNullOrEmpty(content) && Encoding.UTF8.GetByteCount(content) > MaxInlineTextBytes,
        "Text files entered here are limited to 256 KiB.");

    public static Rule CanWriteFolder(ItemFlags folder) => Decision.Require(
        "folder_writable",
        folder,
        static flags => flags.HasFlag(ItemFlags.Write),
        "Current storage location is read-only.");

    public static Rule CanCreateSubfolder(ItemFlags folder) => Decision.Require(
        "folder_create_allowed",
        folder,
        static flags => flags.HasFlag(ItemFlags.CreateFolder),
        "Open a writable folder before creating a subfolder.");
}

internal sealed record FilesActEnvelope(long ExpectedRevision, FilesWorkspaceInput Input);

// The Files views' node vocabulary over IntoChat.OS.UI.Kit: every button emits to the files app with the revision it was drawn at.
internal static class FilesUi
{
    public const string Primary = ButtonTone.Accent;
    public const string Secondary = ButtonTone.Neutral;
    public const string Ghost = "ghost";
    public const string Danger = "danger";
    public const string Link = "ghost";
    public const string Neutral = "neutral";
    public const string Working = "working";

    // A files request as FilesRequests forwards it: the verb, the revision the person saw and the item index it names.
    public static Emit Act(string revision, string verb, int? index = null)
        => new("files", verb == "search-all" ? "search" : verb, DigitalBrain.Invocation.RuntimeProtocol.SerializeToElement(new FilesActEnvelope(
            long.Parse(revision, CultureInfo.InvariantCulture),
            new FilesWorkspaceInput(Index: index, Everywhere: verb == "search-all")), typeof(FilesActEnvelope)));

    public static Node Text(string text) => Kit.Text(text);

    public static Node Muted(string text) => MutedText(text);

    public static Node Button(
        string key,
        string text,
        Emit action,
        bool enabled = true,
        string style = Secondary,
        string? icon = null,
        Rule? guard = null,
        bool small = true)
    {
        ButtonBuilder builder = style switch
        {
            Primary => PrimaryButton(text, icon),
            Danger => DangerButton(text, icon),
            Ghost => GhostButton(text, icon),
            _ => Kit.Button(text, icon),
        };
        builder.Key(key).Small(small).Emits(action);
        if (guard is not null)
        {
            builder.Guard(guard);
        }

        if (!enabled)
        {
            builder.Disabled();
        }

        return builder.ToNode();
    }

    public static Node CopyButton(string key, string label, string text, bool small = true)
        => Kit.Button(label, "copy")
            .Key(key)
            .Small(small)
            .WithIntent(IntentKind.Copy, text)
            .ToNode();

    public static Node Column(Node[] children, double gap = Space.Md, double[]? extents = null)
    {
        var stack = VStack(gap, children);
        return extents is null ? stack.ToNode() : stack.Extents(extents).ToNode();
    }

    public static Node Row(Node[] children, double gap = Space.Sm, double[]? extents = null)
    {
        var stack = HStack(gap, children);
        return extents is null ? stack.ToNode() : stack.Extents(extents).ToNode();
    }

    public static Node List(Node[] rows) => VStack(Space.Xs, rows);

    public static Node Card(string title, string? detail = null, Node[]? children = null)
        => Kit.Card(title, detail ?? "", children ?? []);

    public static Node Panel(string title, string? subtitle = null, Node[]? children = null, string kind = SurfaceKind.Elevated)
        => Surface(kind, title, subtitle, 16, children ?? []);

    public static Node Badge(string text, string tone)
    {
        var status = tone switch
        {
            "done" => StatusKind.Done,
            Working => StatusKind.Working,
            "error" or Danger => StatusKind.Error,
            _ => StatusKind.Idle,
        };
        return StatusPill(status, text);
    }

    public static Node Divider(string? label = null) => Kit.Divider(label);

    public static Node Field(
        string key,
        string name,
        FieldKind kind = FieldKind.Text,
        string? placeholder = null,
        string? label = null,
        string? value = null)
    {
        FieldBuilder builder = kind switch
        {
            FieldKind.Search => SearchField(placeholder ?? "Search…", name),
            FieldKind.Secret => SecretField(name, placeholder),
            FieldKind.Multiline => MultilineField(name, placeholder),
            _ => TextField(name, placeholder),
        };
        builder.Key(key);
        if (label is not null)
        {
            if (label.Length == 0) { builder.NoLabel(); }
            else { builder.Label(label); }
        }
        if (value is not null)
        {
            builder.Value(value);
        }
        return builder.ToNode();
    }

    public static Node Reference(string kind, string name) => Slot(name, kind);
}
