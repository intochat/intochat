using IntoChat.OS.Modules.Web.Reading;

namespace IntoChat.OS.Modules.Web;

public sealed class HtmlMarkdownFacts
{
    [Fact]
    public void HeadingsLinksAndListsBecomeMarkdown()
    {
        var (title, md) = HtmlMarkdown.Convert(
            """<title>T</title><h2>Hi</h2><p>See <a href="/x">x</a></p><ul><li>a</li></ul><script>evil()</script>""",
            new Uri("https://e.org/p"));

        Assert.Equal("T", title);
        Assert.Equal("## Hi\n\nSee [x](https://e.org/x)\n\n- a", md.Trim());
    }

    [Fact]
    public void TitleFallsBackToFirstHeadingWhenTitleElementIsMissing()
    {
        var (title, md) = HtmlMarkdown.Convert(
            """<html><body><h1>Hello &amp; Welcome</h1><p>Body &lt;text&gt;.</p></body></html>""",
            new Uri("https://e.org/"));

        Assert.Equal("Hello & Welcome", title);
        Assert.Contains("# Hello & Welcome", md, StringComparison.Ordinal);
        Assert.Contains("Body <text>.", md, StringComparison.Ordinal);
    }

    [Fact]
    public void DropsChromeScriptsStylesCommentsAndSvg()
    {
        var (_, md) = HtmlMarkdown.Convert(
            """
            <header>Site Header</header>
            <nav><a href="/home">Home</a></nav>
            <style>.a { color: red; }</style>
            <!-- hidden comment -->
            <main><p>Keep me</p></main>
            <aside>Sidebar</aside>
            <form><input value="x" /><button>Submit</button></form>
            <svg><path d="M0 0" /></svg>
            <footer>Copyright</footer>
            <script>if (1 < 2) { alert("</p>"); }</script>
            """,
            new Uri("https://e.org/"));

        Assert.Equal("Keep me", md.Trim());
    }

    [Fact]
    public void CodeBlocksAndSimpleTablesConvertCleanly()
    {
        var (_, md) = HtmlMarkdown.Convert(
            """
            <p>Use <code>dotnet test</code>:</p>
            <pre><code>var x = 1 &lt; 2;
            Console.WriteLine(x);</code></pre>
            <table>
              <tr><th>Name</th><th>Value</th></tr>
              <tr><td>A</td><td>1</td></tr>
              <tr><td>B</td><td>2</td></tr>
            </table>
            """,
            new Uri("https://e.org/"));

        Assert.Contains("`dotnet test`", md, StringComparison.Ordinal);
        Assert.Contains("```\nvar x = 1 < 2;\nConsole.WriteLine(x);\n```", md, StringComparison.Ordinal);
        Assert.Contains("| Name | Value |\n| --- | --- |\n| A | 1 |\n| B | 2 |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void JavascriptLinksKeepTextWithoutUnsafeHref()
    {
        var (_, md) = HtmlMarkdown.Convert(
            """<p><a href="javascript:alert(1)">Click</a></p>""",
            new Uri("https://e.org/"));

        Assert.Equal("Click", md.Trim());
        Assert.DoesNotContain("javascript:", md, StringComparison.OrdinalIgnoreCase);
    }
}
