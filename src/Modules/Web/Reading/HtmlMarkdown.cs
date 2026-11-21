using System.Net;
using System.Text;

namespace IntoChat.OS.Modules.Web.Reading;

internal static class HtmlMarkdown
{
    private static readonly HashSet<string> RawDropTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style",
    };

    private static readonly HashSet<string> DropTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nav", "footer", "header", "aside", "form", "svg", "noscript", "template",
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    public static (string Title, string Markdown) Convert(string html, Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        if (string.IsNullOrEmpty(html))
        {
            return (string.Empty, string.Empty);
        }

        var state = new Converter(html, baseUrl);
        return state.Run();
    }

    private sealed class Converter(string html, Uri baseUrl)
    {
        private readonly StringBuilder _output = new(Math.Min(html.Length, 4096));
        private readonly StringBuilder _title = new();
        private readonly Stack<string> _skipStack = new();
        private readonly Stack<ListFrame> _lists = new();

        private bool _inTitle;
        private bool _inPre;
        private bool _inBlockquote;
        private StringBuilder? _h1Capture;
        private string? _firstH1;

        private bool _inLink;
        private string? _linkHref;
        private StringBuilder? _linkText;

        private bool _inTable;
        private List<List<string>>? _tableRows;
        private List<string>? _currentRow;
        private StringBuilder? _cellText;

        public (string Title, string Markdown) Run()
        {
            var i = 0;
            var len = html.Length;
            while (i < len)
            {
                var lt = html.IndexOf('<', i);
                if (lt < 0)
                {
                    EmitText(html.AsSpan(i));
                    break;
                }

                if (lt > i)
                {
                    EmitText(html.AsSpan(i, lt - i));
                }

                // HTML comment <!-- ... -->
                if (lt + 3 < len && html[lt + 1] == '!' && html[lt + 2] == '-' && html[lt + 3] == '-')
                {
                    var endComment = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = endComment < 0 ? len : endComment + 3;
                    continue;
                }

                // Processing instruction or declaration (<!DOCTYPE ...>, <?xml ...?>)
                if (lt + 1 < len && (html[lt + 1] == '!' || html[lt + 1] == '?'))
                {
                    var gt = html.IndexOf('>', lt + 2);
                    i = gt < 0 ? len : gt + 1;
                    continue;
                }

                var tagEnd = FindTagEnd(html, lt + 1);
                if (tagEnd < 0)
                {
                    EmitText(html.AsSpan(lt));
                    break;
                }

                i = HandleTag(lt + 1, tagEnd);
            }

            FlushTable();
            FlushLink();

            var title = CollapseWhitespace(WebUtility.HtmlDecode(_title.ToString()));
            if (string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(_firstH1))
            {
                title = _firstH1;
            }

            return (title, NormalizeMarkdown(_output.ToString()));
        }

        private int HandleTag(int start, int end)
        {
            var span = html.AsSpan(start, end - start).Trim();
            if (span.IsEmpty)
            {
                return end + 1;
            }

            var closing = span[0] == '/';
            if (closing)
            {
                span = span[1..].TrimStart();
            }

            var selfClosing = !closing && span.EndsWith('/');
            if (selfClosing)
            {
                span = span[..^1].TrimEnd();
            }

            var nameEnd = 0;
            while (nameEnd < span.Length && !char.IsWhiteSpace(span[nameEnd]) && span[nameEnd] != '/')
            {
                nameEnd++;
            }

            if (nameEnd == 0)
            {
                return end + 1;
            }

            var tag = span[..nameEnd].ToString().ToLowerInvariant();
            var attrs = nameEnd < span.Length ? span[nameEnd..] : ReadOnlySpan<char>.Empty;

            if (!closing && RawDropTags.Contains(tag))
            {
                if (selfClosing)
                {
                    return end + 1;
                }

                var closeMarker = "</" + tag;
                var closeStart = html.IndexOf(closeMarker, end + 1, StringComparison.OrdinalIgnoreCase);
                if (closeStart < 0)
                {
                    return html.Length;
                }

                var closeEnd = html.IndexOf('>', closeStart + closeMarker.Length);
                return closeEnd < 0 ? html.Length : closeEnd + 1;
            }

            if (_skipStack.Count > 0)
            {
                if (closing)
                {
                    if (_skipStack.Contains(tag))
                    {
                        while (_skipStack.Count > 0)
                        {
                            var popped = _skipStack.Pop();
                            if (string.Equals(popped, tag, StringComparison.Ordinal))
                            {
                                break;
                            }
                        }
                    }
                }
                else if (!selfClosing && !VoidTags.Contains(tag) && DropTags.Contains(tag))
                {
                    _skipStack.Push(tag);
                }

                return end + 1;
            }

            if (!closing && DropTags.Contains(tag))
            {
                if (!selfClosing)
                {
                    _skipStack.Push(tag);
                }

                return end + 1;
            }

            if (tag == "title")
            {
                _inTitle = !closing;
                return end + 1;
            }

            if (_inTitle)
            {
                return end + 1;
            }

            DispatchTag(tag, closing, selfClosing, attrs);
            return end + 1;
        }

        private void DispatchTag(string tag, bool closing, bool selfClosing, ReadOnlySpan<char> attrs)
        {
            switch (tag)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    if (!closing)
                    {
                        EnsureBlockBreak();
                        var level = tag[1] - '0';
                        ActiveTarget().Append('#', level).Append(' ');
                        if (level == 1 && _firstH1 is null)
                        {
                            _h1Capture = new StringBuilder();
                        }
                    }
                    else
                    {
                        if (tag == "h1" && _h1Capture is not null)
                        {
                            _firstH1 = CollapseWhitespace(_h1Capture.ToString());
                            _h1Capture = null;
                        }

                        EnsureBlockBreak();
                    }

                    break;

                case "p" or "div" or "section" or "article" or "main":
                    EnsureBlockBreak();
                    break;

                case "blockquote":
                    if (!closing)
                    {
                        EnsureBlockBreak();
                        _inBlockquote = true;
                        ActiveTarget().Append("> ");
                    }
                    else
                    {
                        _inBlockquote = false;
                        EnsureBlockBreak();
                    }

                    break;

                case "br":
                    if (!closing)
                    {
                        ActiveTarget().Append('\n');
                        if (_inBlockquote)
                        {
                            ActiveTarget().Append("> ");
                        }
                    }

                    break;

                case "hr":
                    if (!closing)
                    {
                        EnsureBlockBreak();
                        ActiveTarget().Append("---");
                        EnsureBlockBreak();
                    }

                    break;

                case "ul" or "ol":
                    if (!closing)
                    {
                        if (_lists.Count == 0)
                        {
                            EnsureBlockBreak();
                        }
                        else
                        {
                            EnsureLineBreak();
                        }

                        _lists.Push(new ListFrame(tag == "ol"));
                    }
                    else
                    {
                        if (_lists.Count > 0)
                        {
                            _lists.Pop();
                        }

                        if (_lists.Count == 0)
                        {
                            EnsureBlockBreak();
                        }
                        else
                        {
                            EnsureLineBreak();
                        }
                    }

                    break;

                case "li":
                    if (!closing)
                    {
                        EnsureLineBreak();
                        var indent = Math.Max(0, _lists.Count - 1) * 2;
                        var target = ActiveTarget();
                        if (indent > 0)
                        {
                            target.Append(' ', indent);
                        }

                        if (_lists.TryPeek(out var frame) && frame.Ordered)
                        {
                            frame.Index++;
                            target.Append(frame.Index).Append(". ");
                        }
                        else
                        {
                            target.Append("- ");
                        }
                    }
                    else
                    {
                        EnsureLineBreak();
                    }

                    break;

                case "a":
                    if (!closing && !selfClosing)
                    {
                        FlushLink();
                        _inLink = true;
                        _linkHref = ResolveLink(ExtractAttribute(attrs, "href"));
                        _linkText = new StringBuilder();
                    }
                    else if (closing)
                    {
                        FlushLink();
                    }

                    break;

                case "pre":
                    if (!closing)
                    {
                        EnsureBlockBreak();
                        _inPre = true;
                        ActiveTarget().Append("```\n");
                    }
                    else if (_inPre)
                    {
                        var target = ActiveTarget();
                        if (target.Length > 0 && target[^1] != '\n')
                        {
                            target.Append('\n');
                        }

                        target.Append("```");
                        _inPre = false;
                        EnsureBlockBreak();
                    }

                    break;

                case "code":
                    if (!_inPre)
                    {
                        ActiveTarget().Append('`');
                    }

                    break;

                case "strong" or "b":
                    if (!_inPre)
                    {
                        ActiveTarget().Append("**");
                    }

                    break;

                case "em" or "i":
                    if (!_inPre)
                    {
                        ActiveTarget().Append('*');
                    }

                    break;

                case "table":
                    if (!closing)
                    {
                        FlushTable();
                        EnsureBlockBreak();
                        _inTable = true;
                        _tableRows = [];
                    }
                    else
                    {
                        FlushTable();
                    }

                    break;

                case "tr":
                    if (_inTable)
                    {
                        if (!closing)
                        {
                            FlushCell();
                            _currentRow = [];
                        }
                        else
                        {
                            FlushCell();
                            if (_currentRow is { Count: > 0 })
                            {
                                _tableRows!.Add(_currentRow);
                            }

                            _currentRow = null;
                        }
                    }

                    break;

                case "th" or "td":
                    if (_inTable)
                    {
                        if (!closing)
                        {
                            FlushCell();
                            _currentRow ??= [];
                            _cellText = new StringBuilder();
                        }
                        else
                        {
                            FlushCell();
                        }
                    }

                    break;
            }
        }

        private void EmitText(ReadOnlySpan<char> raw)
        {
            if (raw.IsEmpty || _skipStack.Count > 0)
            {
                return;
            }

            if (_inTitle)
            {
                _title.Append(raw);
                return;
            }

            var decoded = WebUtility.HtmlDecode(raw.ToString());
            if (_inPre)
            {
                ActiveTarget().Append(decoded.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                return;
            }

            var collapsed = CollapseRuns(decoded);
            if (collapsed.Length == 0)
            {
                return;
            }

            var target = ActiveTarget();
            if (collapsed == " ")
            {
                if (target.Length == 0 || target[^1] is '\n' or ' ' or '(' or '[' or '`')
                {
                    return;
                }

                target.Append(' ');
                _h1Capture?.Append(' ');
                return;
            }

            if (collapsed[0] == ' ' && (target.Length == 0 || target[^1] is '\n' or ' '))
            {
                collapsed = collapsed[1..];
            }

            target.Append(collapsed);
            _h1Capture?.Append(collapsed);
        }

        private StringBuilder ActiveTarget()
        {
            if (_inLink && _linkText is not null)
            {
                return _linkText;
            }

            if (_cellText is not null)
            {
                return _cellText;
            }

            return _output;
        }

        private void FlushLink()
        {
            if (!_inLink)
            {
                return;
            }

            var text = CollapseWhitespace(_linkText?.ToString() ?? string.Empty);
            var href = _linkHref;
            _inLink = false;
            _linkHref = null;
            _linkText = null;

            if (text.Length == 0)
            {
                return;
            }

            var target = ActiveTarget();
            if (href is not null)
            {
                target.Append('[').Append(text).Append("](").Append(href).Append(')');
            }
            else
            {
                target.Append(text);
            }

            _h1Capture?.Append(text);
        }

        private void FlushCell()
        {
            if (_cellText is null)
            {
                return;
            }

            FlushLink();
            var value = CollapseWhitespace(_cellText.ToString()).Replace("|", "\\|", StringComparison.Ordinal);
            _currentRow?.Add(value);
            _cellText = null;
        }

        private void FlushTable()
        {
            if (!_inTable)
            {
                return;
            }

            FlushCell();
            if (_currentRow is { Count: > 0 })
            {
                _tableRows?.Add(_currentRow);
            }

            var rows = _tableRows;
            _inTable = false;
            _tableRows = null;
            _currentRow = null;
            _cellText = null;

            if (rows is null || rows.Count == 0)
            {
                return;
            }

            var cols = rows.Max(static r => r.Count);
            if (cols == 0)
            {
                return;
            }

            EnsureBlockBreak();
            AppendTableRow(rows[0], cols);
            _output.Append('|');
            for (var c = 0; c < cols; c++)
            {
                _output.Append(" --- |");
            }

            _output.Append('\n');
            for (var r = 1; r < rows.Count; r++)
            {
                AppendTableRow(rows[r], cols);
            }

            EnsureBlockBreak();
        }

        private void AppendTableRow(List<string> row, int cols)
        {
            _output.Append('|');
            for (var c = 0; c < cols; c++)
            {
                _output.Append(' ').Append(c < row.Count ? row[c] : string.Empty).Append(" |");
            }

            _output.Append('\n');
        }

        private void EnsureBlockBreak()
        {
            if (_cellText is not null || _inLink)
            {
                return;
            }

            TrimTrailingSpaces(_output);
            if (_output.Length == 0)
            {
                return;
            }

            if (_output[^1] != '\n')
            {
                _output.Append("\n\n");
            }
            else if (_output.Length >= 2 && _output[^2] != '\n')
            {
                _output.Append('\n');
            }
        }

        private void EnsureLineBreak()
        {
            if (_cellText is not null || _inLink)
            {
                return;
            }

            TrimTrailingSpaces(_output);
            if (_output.Length > 0 && _output[^1] != '\n')
            {
                _output.Append('\n');
            }
        }

        private string? ResolveLink(string? rawHref)
        {
            if (string.IsNullOrWhiteSpace(rawHref))
            {
                return null;
            }

            var decoded = WebUtility.HtmlDecode(rawHref.Trim());
            if (decoded.StartsWith('#'))
            {
                return null;
            }

            if (Uri.TryCreate(baseUrl, decoded, out var resolved)
                && resolved.IsAbsoluteUri
                && resolved.Scheme is "http" or "https")
            {
                return resolved.AbsoluteUri;
            }

            return null;
        }

        private static string? ExtractAttribute(ReadOnlySpan<char> attrs, string name)
        {
            var i = 0;
            while (i < attrs.Length)
            {
                while (i < attrs.Length && char.IsWhiteSpace(attrs[i]))
                {
                    i++;
                }

                var nameStart = i;
                while (i < attrs.Length && !char.IsWhiteSpace(attrs[i]) && attrs[i] != '=' && attrs[i] != '/')
                {
                    i++;
                }

                var attrName = attrs[nameStart..i];
                while (i < attrs.Length && char.IsWhiteSpace(attrs[i]))
                {
                    i++;
                }

                ReadOnlySpan<char> value = default;
                if (i < attrs.Length && attrs[i] == '=')
                {
                    i++;
                    while (i < attrs.Length && char.IsWhiteSpace(attrs[i]))
                    {
                        i++;
                    }

                    if (i < attrs.Length && (attrs[i] == '"' || attrs[i] == '\''))
                    {
                        var quote = attrs[i++];
                        var valStart = i;
                        while (i < attrs.Length && attrs[i] != quote)
                        {
                            i++;
                        }

                        value = attrs[valStart..i];
                        if (i < attrs.Length)
                        {
                            i++;
                        }
                    }
                    else
                    {
                        var valStart = i;
                        while (i < attrs.Length && !char.IsWhiteSpace(attrs[i]))
                        {
                            i++;
                        }

                        value = attrs[valStart..i];
                    }
                }

                if (attrName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return value.ToString();
                }
            }

            return null;
        }

        private static int FindTagEnd(string text, int from)
        {
            char quote = '\0';
            for (var i = from; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '>')
                {
                    return i;
                }
            }

            return -1;
        }

        private static string CollapseRuns(string text)
        {
            var sb = new StringBuilder(text.Length);
            var prevSpace = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace)
                    {
                        sb.Append(' ');
                        prevSpace = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }

            return sb.ToString();
        }

        private static string CollapseWhitespace(string text) => CollapseRuns(text).Trim();

        private static void TrimTrailingSpaces(StringBuilder sb)
        {
            while (sb.Length > 0 && sb[^1] == ' ')
            {
                sb.Length--;
            }
        }

        private static string NormalizeMarkdown(string raw)
        {
            var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var sb = new StringBuilder(raw.Length);
            var blankRun = 0;
            var inFence = false;
            foreach (var line in lines)
            {
                var trimmed = inFence ? line : line.TrimEnd();
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                }

                if (!inFence && trimmed.Length == 0)
                {
                    if (sb.Length > 0 && blankRun < 1)
                    {
                        sb.Append('\n');
                        blankRun++;
                    }

                    continue;
                }

                blankRun = 0;
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(trimmed);
            }

            return sb.ToString().Trim();
        }

        private sealed class ListFrame(bool ordered)
        {
            public bool Ordered { get; } = ordered;
            public int Index { get; set; }
        }
    }
}
