// Shared: the synapse shape rule read from text. A file is a synapse when it has top-level statements and
// shared code when it holds only usings, namespaces and type declarations. Comments, literals and
// preprocessor lines are blanked first, so a call named inside a string never makes a file a synapse.
internal static class Shape
{
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "internal", "private", "protected", "static", "sealed", "abstract", "partial", "file", "readonly",
        "unsafe", "new", "ref",
    };

    private static readonly HashSet<string> Declarations = new(StringComparer.Ordinal)
    {
        "class", "struct", "record", "interface", "enum", "delegate",
    };

    // Every top-level chunk ends at a `;` or at the `}` that closes a block opened at the top level.
    public static bool IsSynapse(string source)
    {
        var code = Blank(source ?? "");
        var depth = 0;
        var start = 0;
        for (var index = 0; index < code.Length; index++)
        {
            switch (code[index])
            {
                case '(' or '[' or '{': depth++; break;
                case ')' or ']': depth--; break;
                case '}':
                    if (--depth != 0) { break; }
                    if (IsStatement(code[start..(index + 1)])) { return true; }
                    start = index + 1;
                    break;
                case ';' when depth == 0:
                    if (IsStatement(code[start..index])) { return true; }
                    start = index + 1;
                    break;
            }
        }
        return IsStatement(code[start..]);
    }

    private static bool IsStatement(string chunk)
    {
        var text = chunk.Trim();
        while (text.StartsWith('['))
        {
            var close = Closing(text);
            if (close < 0) { break; }
            text = text[(close + 1)..].TrimStart();
        }
        if (text.Length == 0) { return false; }
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var at = words[0] == "global" ? 1 : 0;
        if (at < words.Length && words[at] == "using") { return !IsUsingDirective(string.Join(' ', words.Skip(at + 1))); }
        if (words[0] is "namespace" or "extern") { return false; }
        while (at < words.Length && Modifiers.Contains(words[at])) { at++; }
        return at < words.Length && !Declarations.Contains(words[at]);
    }

    // `using X.Y`, `using static X` and `using A = B` are directives; `using var x = …` and `using (…)` are statements.
    private static bool IsUsingDirective(string rest)
    {
        if (rest.StartsWith("static ", StringComparison.Ordinal)) { return true; }
        var name = 0;
        while (name < rest.Length && (char.IsLetterOrDigit(rest[name]) || rest[name] is '_' or '.' or '@' or ':')) { name++; }
        if (name == 0) { return false; }
        var after = rest[name..].TrimStart();
        return after.Length == 0 || (after[0] == '=' && !rest[..name].Contains('.'));
    }

    private static int Closing(string text)
    {
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '[') { depth++; }
            else if (text[index] == ']' && --depth == 0) { return index; }
        }
        return -1;
    }

    private static string Blank(string source)
    {
        var code = new System.Text.StringBuilder(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            var end = LiteralEnd(source, index);
            if (end > index) { code.Append(' ', end - index); index = end; continue; }
            code.Append(source[index++]);
        }
        return code.ToString();
    }

    // Where the comment, literal or preprocessor line starting at index ends; index itself when none starts there.
    private static int LiteralEnd(string text, int index)
    {
        var current = text[index];
        var next = index + 1 < text.Length ? text[index + 1] : '\0';
        if (current == '/' && next == '/') { return LineEnd(text, index); }
        if (current == '/' && next == '*')
        {
            var close = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + 2;
        }
        if (current == '#' && AtLineStart(text, index)) { return LineEnd(text, index); }
        if (current == '\'') { return CharEnd(text, index); }
        if (current == '"') { return StringEnd(text, index); }
        return index;
    }

    private static int LineEnd(string text, int index)
    {
        var end = text.IndexOf('\n', index);
        return end < 0 ? text.Length : end;
    }

    private static bool AtLineStart(string text, int index)
    {
        for (var at = index - 1; at >= 0 && text[at] != '\n'; at--)
        {
            if (text[at] is not (' ' or '\t' or '\r')) { return false; }
        }
        return true;
    }

    private static int CharEnd(string text, int index)
    {
        var at = index + 1;
        while (at < text.Length && text[at] is not ('\'' or '\n')) { at += text[at] == '\\' ? 2 : 1; }
        return Math.Min(text.Length, at + 1);
    }

    // Regular, verbatim, interpolated and raw strings; an interpolation hole is code with its own literals.
    private static int StringEnd(string text, int index)
    {
        bool interpolated = false, verbatim = false;
        for (var at = index - 1; at >= 0 && text[at] is '$' or '@'; at--)
        {
            interpolated |= text[at] == '$';
            verbatim |= text[at] == '@';
        }
        var quotes = 0;
        while (index + quotes < text.Length && text[index + quotes] == '"') { quotes++; }
        if (!verbatim && quotes >= 3)
        {
            var close = text.IndexOf(new string('"', quotes), index + quotes, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + quotes;
        }
        var position = index + 1;
        while (position < text.Length)
        {
            var current = text[position];
            if (current == '"')
            {
                if (verbatim && position + 1 < text.Length && text[position + 1] == '"') { position += 2; continue; }
                return position + 1;
            }
            if (!verbatim && current == '\\') { position += 2; continue; }
            if (!verbatim && current == '\n') { return position; }
            if (interpolated && current == '{')
            {
                if (position + 1 < text.Length && text[position + 1] == '{') { position += 2; continue; }
                position = HoleEnd(text, position + 1);
                continue;
            }
            position++;
        }
        return text.Length;
    }

    private static int HoleEnd(string text, int index)
    {
        var depth = 0;
        while (index < text.Length)
        {
            var end = LiteralEnd(text, index);
            if (end > index) { index = end; continue; }
            var current = text[index++];
            if (current == '{') { depth++; }
            else if (current == '}' && depth-- == 0) { return index; }
        }
        return text.Length;
    }
}
