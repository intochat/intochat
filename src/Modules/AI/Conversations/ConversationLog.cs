using System.Buffers;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Modules.AI.Conversations;

internal sealed record LoggedEntry(long Revision, ConversationEntry Entry);

internal static class ConversationLog
{
    public const int MaxEntries = 1_024;
    public const int CoveredPreview = 512;

    public static LoggedEntry[] Append(LoggedEntry[] log, long revision, params ConversationEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Length == 0)
        {
            return log;
        }

        var seen = new HashSet<EntryId>(log.Length + entries.Length);
        foreach (var item in log)
        {
            seen.Add(item.Entry.Id);
        }

        var combined = new LoggedEntry[log.Length + entries.Length];
        Array.Copy(log, combined, log.Length);
        var writeAt = log.Length;
        var hasSummary = false;
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!seen.Add(entry.Id))
            {
                throw new ArgumentException($"Entry id '{entry.Id}' is already in the conversation log.", nameof(entries));
            }

            if (entry is Summarized)
            {
                hasSummary = true;
            }
            combined[writeAt++] = new LoggedEntry(revision, entry);
        }

        if (hasSummary)
        {
            ShrinkCoveredPayloads(combined);
        }

        if (combined.Length > MaxEntries)
        {
            return combined[(combined.Length - MaxEntries)..];
        }

        return combined;
    }

    public static ConversationEntry[] Visible(LoggedEntry[] log, long? since, bool owner)
    {
        ArgumentNullException.ThrowIfNull(log);
        var result = new List<ConversationEntry>(log.Length);
        foreach (var item in log)
        {
            if (since is { } rev && item.Revision <= rev)
            {
                continue;
            }
            if (!owner && item.Entry.Visible != Visibility.Everyone)
            {
                continue;
            }
            result.Add(item.Entry);
        }
        return [.. result];
    }

    public static bool IsSuperseded(LoggedEntry[] log, EntryId reply)
    {
        ArgumentNullException.ThrowIfNull(log);
        foreach (var item in log)
        {
            if (item.Entry is Said { Replaces: { } replaced } && replaced == reply)
            {
                return true;
            }
        }
        return false;
    }

    public static bool HasMessageId(LoggedEntry[] log, string messageId, Participant sender)
    {
        ArgumentNullException.ThrowIfNull(log);
        for (var i = log.Length - 1; i >= 0; i--)
        {
            if (log[i].Entry is Said said
                && string.Equals(said.MessageId, messageId, StringComparison.Ordinal)
                && said.By == sender)
            {
                return true;
            }
        }
        return false;
    }

    public static LoggedEntry[] Replace(LoggedEntry[] log, EntryId target, long revision, Func<Said, Said> transform)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(transform);
        var index = Array.FindIndex(log, item => item.Entry.Id == target);
        if (index < 0 || log[index].Entry is not Said existing)
        {
            throw new KeyNotFoundException($"Conversation entry '{target}' was not found.");
        }

        var updated = transform(existing);
        var copy = new LoggedEntry[log.Length];
        Array.Copy(log, copy, log.Length);
        copy[index] = new LoggedEntry(revision, updated);
        return copy;
    }

    // Index of the last entry covered by the latest Summarized, or -1 if none.
    internal static int LastSummaryIndex(LoggedEntry[] log)
    {
        for (var i = log.Length - 1; i >= 0; i--)
        {
            if (log[i].Entry is Summarized)
            {
                return i;
            }
        }
        return -1;
    }

    internal static int CoveredEndIndex(LoggedEntry[] log, int summaryIndex)
    {
        if (summaryIndex < 0 || log[summaryIndex].Entry is not Summarized sum)
        {
            return -1;
        }

        var upToIndex = -1;
        for (var i = 0; i < log.Length; i++)
        {
            if (log[i].Entry.Id == sum.UpTo)
            {
                upToIndex = i;
                break;
            }
        }

        // Normal compaction: UpTo is an earlier entry before Summarized.
        if (upToIndex >= 0 && upToIndex < summaryIndex)
        {
            return upToIndex;
        }

        // Legacy import: Summarized sits at index 0 before imported entries (UpTo > summaryIndex).
        if (upToIndex >= summaryIndex)
        {
            return summaryIndex - 1;
        }

        // UpTo was already pruned past MaxEntries: everything before Summarized was covered.
        return summaryIndex - 1;
    }

    private static void ShrinkCoveredPayloads(LoggedEntry[] log)
    {
        var summaryIndex = LastSummaryIndex(log);
        var coveredEnd = CoveredEndIndex(log, summaryIndex);
        if (coveredEnd < 0)
        {
            return;
        }

        for (var i = 0; i <= coveredEnd; i++)
        {
            var item = log[i];
            switch (item.Entry)
            {
                case Called called when called.ArgsJson.Length > CoveredPreview:
                    log[i] = item with { Entry = called with { ArgsJson = ShrinkJson(called.ArgsJson, CoveredPreview) } };
                    break;
                case Returned returned when returned.ResultJson.Length > CoveredPreview:
                    log[i] = item with { Entry = returned with { ResultJson = ShrinkJson(returned.ResultJson, CoveredPreview) } };
                    break;
            }
        }
    }

    internal static string ShrinkJson(string json, int maxTotalLength)
    {
        if (json.Length <= maxTotalLength)
        {
            return json;
        }

        // Binary-search / shrink the preview slice until the serialized JSON object fits within maxTotalLength.
        var maxPreview = Math.Max(0, maxTotalLength - 64);
        while (true)
        {
            var previewLen = Math.Min(maxPreview, json.Length);
            if (previewLen > 0 && char.IsHighSurrogate(json[previewLen - 1]))
            {
                previewLen--;
            }

            var encoded = EncodeTruncated(json, previewLen);
            if (encoded.Length <= maxTotalLength || previewLen == 0)
            {
                return encoded;
            }

            var excess = encoded.Length - maxTotalLength;
            maxPreview = Math.Max(0, previewLen - Math.Max(1, excess));
        }
    }

    private static string EncodeTruncated(string original, int previewLen)
    {
        var buffer = new ArrayBufferWriter<byte>(previewLen + 64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("truncated", true);
            writer.WriteNumber("characters", original.Length);
            writer.WriteString("preview", original.AsSpan(0, previewLen));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
