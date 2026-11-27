using System.Globalization;
using System.Text;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Attachments;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.AI.Conversations;

internal sealed record ModelWindow(AiMessage[] Messages, EntryId[] Sources, string? Summary);

internal static class ModelContext
{
    public static AiContent[] ProjectSaidContent(Said said, Func<AttachmentRef, AiImage?>? resolveImage = null)
    {
        ArgumentNullException.ThrowIfNull(said);

        if (said.Attachments is not { Length: > 0 })
        {
            return [new AiText(said.Text)];
        }

        var parts = new List<AiContent>(said.Attachments.Length + 1);
        var textBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(said.Text))
        {
            textBuilder.Append(said.Text);
        }

        foreach (var attachment in said.Attachments)
        {
            if (textBuilder.Length > 0)
            {
                textBuilder.AppendLine();
            }

            if (string.Equals(attachment.Kind, AttachmentValidator.KindVoiceNote, StringComparison.Ordinal))
            {
                var duration = FormatDuration(attachment.DurationMs);
                if (!string.IsNullOrWhiteSpace(attachment.Transcript))
                {
                    textBuilder.Append("[Voice note (")
                        .Append(duration)
                        .Append(", ")
                        .Append(attachment.FileName)
                        .Append(")]: ")
                        .Append(attachment.Transcript);
                }
                else
                {
                    textBuilder.Append("[Voice note (")
                        .Append(duration)
                        .Append(", ")
                        .Append(attachment.FileName)
                        .Append(", ")
                        .Append(attachment.MediaType)
                        .Append(")]");
                }
            }
            else if (string.Equals(attachment.Kind, AttachmentValidator.KindImage, StringComparison.Ordinal))
            {
                textBuilder.Append("[Attached image: ")
                    .Append(attachment.FileName)
                    .Append(" (")
                    .Append(attachment.MediaType)
                    .Append(", ")
                    .Append(attachment.SizeBytes.ToString(CultureInfo.InvariantCulture))
                    .Append(" bytes)]");
                if (resolveImage?.Invoke(attachment) is { } image)
                {
                    parts.Add(image);
                }
            }
            else
            {
                textBuilder.Append("[Attached file: ")
                    .Append(attachment.FileName)
                    .Append(" (")
                    .Append(attachment.MediaType)
                    .Append(", ")
                    .Append(attachment.SizeBytes.ToString(CultureInfo.InvariantCulture))
                    .Append(" bytes)]");
            }
        }

        parts.Insert(0, new AiText(textBuilder.ToString()));
        return [.. parts];
    }

    private static string FormatDuration(int? durationMs)
    {
        if (durationMs is not > 0)
        {
            return "0:00";
        }

        var totalSeconds = (int)Math.Round(durationMs.Value / 1000.0);
        return string.Create(CultureInfo.InvariantCulture, $"{totalSeconds / 60}:{totalSeconds % 60:D2}");
    }

    public static ModelWindow From(LoggedEntry[] log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (log.Length == 0)
        {
            return new ModelWindow([], [], null);
        }

        var summaryIndex = ConversationLog.LastSummaryIndex(log);
        var summary = summaryIndex >= 0 ? ((Summarized)log[summaryIndex].Entry).Summary : null;
        var startIndex = summaryIndex >= 0 ? ConversationLog.CoveredEndIndex(log, summaryIndex) + 1 : 0;

        // Detect multi-human transcripts so authenticated speaker attribution is preserved in both Sender metadata and prompt text.
        var humanAccounts = new HashSet<string>(StringComparer.Ordinal);
        for (var i = startIndex; i < log.Length; i++)
        {
            if (log[i].Entry is Said { By: Participant.Human human } said)
            {
                var id = said.Sender?.AccountId ?? human.Account.Value;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    humanAccounts.Add(id);
                }
            }
        }
        var multiHuman = humanAccounts.Count > 1;

        // Collect every superseded reply and its run, so both the old reply and its run's tool calls/results are excluded.
        var supersededReplies = new HashSet<EntryId>();
        var supersededRuns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in log)
        {
            if (item.Entry is Said { Replaces: { } replaced })
            {
                supersededReplies.Add(replaced);
                if (EntryIds.RunOf(replaced) is { } runId)
                {
                    supersededRuns.Add(runId);
                }
            }
        }

        // Only include Called+Returned pairs that have both sides in the active slice (avoids dangling tool calls).
        var returnedPairs = new HashSet<(string RunId, string CallId)>();
        var calledPairs = new HashSet<(string RunId, string CallId)>();
        for (var i = startIndex; i < log.Length; i++)
        {
            var entry = log[i].Entry;
            if (entry is Called && EntryIds.RunOf(entry.Id) is { } callRun && EntryIds.CallOf(entry.Id) is { } callId && !supersededRuns.Contains(callRun))
            {
                calledPairs.Add((callRun, callId));
            }
            else if (entry is Returned && EntryIds.RunOf(entry.Id) is { } resRun && EntryIds.CallOf(entry.Id) is { } resCallId && !supersededRuns.Contains(resRun))
            {
                returnedPairs.Add((resRun, resCallId));
            }
        }

        var messages = new List<AiMessage>();
        var sources = new List<EntryId>();
        var pendingCalls = new List<AiContent>();
        string? pendingCallRun = null;
        EntryId pendingCallLastId = default;

        void FlushPendingCalls()
        {
            if (pendingCalls.Count == 0)
            {
                return;
            }
            messages.Add(new AiMessage("assistant", [.. pendingCalls]));
            sources.Add(pendingCallLastId);
            pendingCalls.Clear();
            pendingCallRun = null;
        }

        for (var i = startIndex; i < log.Length; i++)
        {
            var entry = log[i].Entry;
            switch (entry)
            {
                case Summarized:
                    continue;

                case Said said:
                    if (supersededReplies.Contains(said.Id))
                    {
                        continue;
                    }
                    FlushPendingCalls();
                    var sender = ResolveSender(said);
                    var role = said.By is Participant.Human ? "user" : "assistant";
                    var projectedSaid = multiHuman && said.By is Participant.Human
                        ? said with { Text = FormatSpeakerAttributedText(sender, said.Text) }
                        : said;
                    messages.Add(new AiMessage(role, ProjectSaidContent(projectedSaid)) { Sender = sender });
                    sources.Add(said.Id);
                    break;

                case Called called:
                    if (EntryIds.RunOf(called.Id) is not { } runId
                        || EntryIds.CallOf(called.Id) is not { } cid
                        || supersededRuns.Contains(runId)
                        || !returnedPairs.Contains((runId, cid)))
                    {
                        continue;
                    }
                    if (pendingCallRun is not null && !string.Equals(pendingCallRun, runId, StringComparison.Ordinal))
                    {
                        FlushPendingCalls();
                    }
                    pendingCallRun = runId;
                    pendingCallLastId = called.Id;
                    pendingCalls.Add(new AiToolCall(cid, called.Tool, called.ArgsJson));
                    break;

                case Returned returned:
                    if (EntryIds.RunOf(returned.Id) is not { } rRun
                        || EntryIds.CallOf(returned.Id) is not { } rCid
                        || supersededRuns.Contains(rRun)
                        || !calledPairs.Contains((rRun, rCid)))
                    {
                        continue;
                    }
                    FlushPendingCalls();
                    messages.Add(new AiMessage("tool", [new AiToolResult(rCid, returned.ResultJson)]));
                    sources.Add(returned.Id);
                    break;
            }
        }

        FlushPendingCalls();
        return new ModelWindow([.. messages], [.. sources], summary);
    }

    private static SenderIdentity ResolveSender(Said said)
    {
        if (said.Sender is { } explicitSender)
        {
            return explicitSender;
        }

        return said.By switch
        {
            Participant.Human human => new SenderIdentity(human.Account.Value, human.Account.Value, SenderKind.Human),
            Participant.Assistant assistant => new SenderIdentity(assistant.AgentId, assistant.AgentId, SenderKind.Agent),
            _ => new SenderIdentity("unknown", "Unknown", SenderKind.Human),
        };
    }

    private static string FormatSpeakerAttributedText(SenderIdentity sender, string text)
    {
        var safeName = sender.DisplayName.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("[", "", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal)
            .Trim();
        var safeId = sender.AccountId.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("[", "", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal)
            .Trim();
        var framedBody = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "\n  ", StringComparison.Ordinal);
        return $"[{safeName} ({safeId})]: {framedBody}";
    }
}
