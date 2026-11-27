using DigitalBrain;
using DigitalBrain.UI;

namespace IntoChat.OS.UI.Dispatch;

internal static class NodeOwnerNormalizer
{
    public static Node Normalize(Node node, string owner)
    {
        return node switch
        {
            Column col => col with { Children = NormalizeList(col.Children, owner) },
            Row row => row with { Children = NormalizeList(row.Children, owner) },
            Grid grid => grid with { Children = NormalizeList(grid.Children, owner) },
            SplitPane split => split with
            {
                Leading = Normalize(split.Leading, owner),
                Trailing = Normalize(split.Trailing, owner),
            },
            Surface surface => surface with { Children = NormalizeList(surface.Children, owner) },
            Card card => card with
            {
                Children = NormalizeList(card.Children, owner),
                Header = card.Header is null ? null : Normalize(card.Header, owner),
                Footer = card.Footer is null ? null : Normalize(card.Footer, owner),
            },
            Field field => field with { OnSubmit = NormalizeCommand(field.OnSubmit, owner) },
            DigitalBrain.UI.Button btn => btn with { Command = NormalizeCommand(btn.Command, owner) },
            DigitalBrain.UI.IconButton icon => icon with { Command = NormalizeCommand(icon.Command, owner) },
            InfoBar bar => bar with
            {
                Close = NormalizeCommand(bar.Close, owner),
                Action = bar.Action is null ? null : (DigitalBrain.UI.Button)Normalize(bar.Action, owner),
            },
            AppShellNode shell => shell with
            {
                Sidebar = shell.Sidebar is null ? null : Normalize(shell.Sidebar, owner),
                Body = Normalize(shell.Body, owner),
                Toolbar = shell.Toolbar is null ? null : Normalize(shell.Toolbar, owner),
                Inspector = shell.Inspector is null ? null : Normalize(shell.Inspector, owner),
            },
            NavItemNode nav => nav with { Command = NormalizeCommand(nav.Command, owner) },
            AppTileNode tile => tile with { Command = NormalizeCommand(tile.Command, owner) },
            DigitalBrain.UI.Conversation conv => conv with
            {
                Composer = conv.Composer with { OnSubmit = NormalizeCommand(conv.Composer.OnSubmit, owner)! },
                Waiting = conv.Waiting is null ? null : Normalize(conv.Waiting, owner),
                Messages = [.. conv.Messages.Select(m => m.Cards is null ? m : m with { Cards = NormalizeList(m.Cards, owner) })],
            },
            ChoiceCardNode choice => choice with
            {
                Options = [.. choice.Options.Select(o => o.Pick is null ? o : o with { Pick = (DigitalBrain.UI.Button)Normalize(o.Pick, owner) })],
            },
            DraftCardNode draft => draft with
            {
                Send = draft.Send is null ? null : (DigitalBrain.UI.Button)Normalize(draft.Send, owner),
                Discard = draft.Discard is null ? null : (DigitalBrain.UI.Button)Normalize(draft.Discard, owner),
            },
            RecordCardNode record => record with
            {
                Open = record.Open is null ? null : (DigitalBrain.UI.Button)Normalize(record.Open, owner),
            },
            SecretCardNode secret => secret with
            {
                Enter = secret.Enter is null ? null : (DigitalBrain.UI.Button)Normalize(secret.Enter, owner),
                Skip = secret.Skip is null ? null : (DigitalBrain.UI.Button)Normalize(secret.Skip, owner),
            },
            JobCardNode job => job with
            {
                Cancel = job.Cancel is null ? null : (DigitalBrain.UI.Button)Normalize(job.Cancel, owner),
                OpenArtifact = job.OpenArtifact is null ? null : (DigitalBrain.UI.Button)Normalize(job.OpenArtifact, owner),
            },
            ErrorLineNode error => error with
            {
                Action = error.Action is null ? null : (DigitalBrain.UI.Button)Normalize(error.Action, owner),
            },
            EmptyStateNode empty => empty with
            {
                Suggestions = empty.Suggestions is null ? null : NormalizeButtonList(empty.Suggestions, owner),
            },
            _ => node,
        };
    }

    private static IReadOnlyList<DigitalBrain.UI.Button> NormalizeButtonList(IReadOnlyList<DigitalBrain.UI.Button> buttons, string owner)
    {
        if (buttons.Count == 0)
        {
            return buttons;
        }

        var normalized = new DigitalBrain.UI.Button[buttons.Count];
        var changed = false;
        for (var i = 0; i < buttons.Count; i++)
        {
            var next = (DigitalBrain.UI.Button)Normalize(buttons[i], owner);
            if (!ReferenceEquals(next, buttons[i]))
            {
                changed = true;
            }

            normalized[i] = next;
        }

        return changed ? normalized : buttons;
    }

    private static IReadOnlyList<Node> NormalizeList(IReadOnlyList<Node> children, string owner)
    {
        if (children.Count == 0)
        {
            return children;
        }

        var normalized = new Node[children.Count];
        var changed = false;
        for (var i = 0; i < children.Count; i++)
        {
            var next = Normalize(children[i], owner);
            if (!ReferenceEquals(next, children[i]))
            {
                changed = true;
            }

            normalized[i] = next;
        }

        return changed ? normalized : children;
    }

    private static Command? NormalizeCommand(Command? command, string owner)
    {
        if (command is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(command.Target)
            || string.Equals(command.Target, owner, StringComparison.Ordinal)
            ? command with { Target = "." }
            : command;
    }
}
