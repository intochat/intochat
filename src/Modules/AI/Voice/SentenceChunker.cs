using System.Text;

namespace IntoChat.OS.Modules.AI.Voice;

internal static class SentenceChunker
{
    public static IEnumerable<string> Complete(StringBuilder pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        if (pending.Length == 0)
        {
            return [];
        }

        var text = pending.ToString();
        var results = new List<string>();
        var start = 0;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\n')
            {
                var sentence = text[start..i].Trim();
                if (sentence.Length > 0)
                {
                    results.Add(sentence);
                }

                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                start = i;
                continue;
            }

            if (c is '.' or '!' or '?')
            {
                // Avoid splitting on decimal numbers such as 3.14.
                if (c == '.'
                    && i > 0
                    && i + 1 < text.Length
                    && char.IsDigit(text[i - 1])
                    && char.IsDigit(text[i + 1]))
                {
                    i++;
                    continue;
                }

                var end = i + 1;
                while (end < text.Length && text[end] is '.' or '!' or '?' or '"' or '\'' or ')' or ']')
                {
                    end++;
                }

                var sentence = text[start..end].Trim();
                if (sentence.Length > 0)
                {
                    results.Add(sentence);
                }

                i = end;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                start = i;
                continue;
            }

            i++;
        }

        if (start > 0)
        {
            pending.Remove(0, start);
        }

        return results;
    }
}
