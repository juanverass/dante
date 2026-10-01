using System.Net;
using System.Xml.Linq;
using Dante.Worker.Telegram;

namespace Dante.Tests;

public sealed class TelegramMessageFormatterTests
{
    [Theory]
    [InlineData("csharp", "csharp")]
    [InlineData("cs", "csharp")]
    [InlineData("python", "python")]
    [InlineData("py", "python")]
    [InlineData("bash", "bash")]
    [InlineData("sh", "bash")]
    [InlineData("diff", "diff")]
    [InlineData("patch", "diff")]
    public void FencedCodePreservesSurroundingTextAndEscapesContent(string info, string language)
    {
        var formatter = new TelegramMessageFormatter();
        var part = Assert.Single(formatter.Format($"Antes <b>\n```{info}\nvar x = a < b && c > d;\n```\nDepois & fim."));
        Assert.Equal("Antes <b>\nvar x = a < b && c > d;\nDepois & fim.", part.PlainText);
        Assert.Contains($"<pre><code class=\"language-{language}\">", part.Html);
        Assert.Contains("Antes &lt;b&gt;", part.Html);
        Assert.Contains("a &lt; b &amp;&amp; c &gt; d", part.Html);
        Assert.EndsWith("Depois &amp; fim.", part.Html);
        AssertValid(part);
    }

    [Fact]
    public void FenceStatePersistsAcrossBatchesAndClosingFenceRestoresPlainText()
    {
        var formatter = new TelegramMessageFormatter();
        Assert.Empty(formatter.Format("```python\n"));
        var first = Assert.Single(formatter.Format("print(1)\n"));
        var second = Assert.Single(formatter.Format("print(2)\n```\nConcluído\n"));
        Assert.Contains("language-python", first.Html);
        Assert.Contains("language-python", second.Html);
        Assert.EndsWith("</code></pre>" + WebUtility.HtmlEncode("Concluído\n"), second.Html);
        AssertValid(first);
        AssertValid(second);
    }

    [Fact]
    public void LargerFenceAllowsEmbeddedTripleBackticksAndUnclosedBlockIsStillValid()
    {
        var formatter = new TelegramMessageFormatter();
        var part = Assert.Single(formatter.Format("````text\n```\nconteúdo\n````\nfim\n"));
        Assert.Contains(WebUtility.HtmlEncode("```\nconteúdo\n") + "</code>", part.Html);
        AssertValid(part);
        AssertValid(Assert.Single(formatter.Format("~~~python\nprint(1)")));
    }

    [Fact]
    public void ModelHtmlAndLanguageAttributesAreNeverTrusted()
    {
        var part = Assert.Single(new TelegramMessageFormatter().Format(
            "```python\"onclick=\"evil\n<pre><code><b>unsafe</b>&</code></pre>\n```\n<a href=\"tg://evil\">click</a>"));
        Assert.DoesNotContain("class=", part.Html);
        Assert.Contains("&lt;pre&gt;", part.Html);
        Assert.Contains("&lt;a href=", part.Html);
        AssertValid(part);
    }

    [Fact]
    public void LongBlockSplitsIntoValidPartsWithRoomForBackgroundPrefixAndUnicode()
    {
        var code = string.Concat(Enumerable.Repeat("a<&>😀\n", 2000));
        var prefix = "[S000001] ";
        var parts = new TelegramMessageFormatter().Format("```diff\n" + code + "```\n", prefix.Length);
        Assert.True(parts.Count > 2);
        Assert.Equal(code, string.Concat(parts.Select(part => part.PlainText)));
        Assert.All(parts, part =>
        {
            Assert.Contains("language-diff", part.Html);
            Assert.EndsWith("\n", part.PlainText);
            Assert.InRange(part.Html.Length + prefix.Length, 1, 4000);
            AssertValid(part.WithPrefix(prefix));
        });
    }

    [Fact]
    public void SingleOversizedLineIsSplitWithoutLosingEscapesOrUnicode()
    {
        var line = string.Concat(Enumerable.Repeat("<😀&", 3000));
        var parts = new TelegramMessageFormatter().Format("```python\n" + line);
        Assert.True(parts.Count > 1);
        Assert.Equal(line, string.Concat(parts.Select(part => part.PlainText)));
        Assert.All(parts, part =>
        {
            Assert.InRange(part.Html.Length, 1, 4000);
            AssertValid(part);
        });
    }

    [Fact]
    public void CommandsCannotCloseTheCodeBlockThroughTheirOwnFences()
    {
        var command = "python3 - <<'EOF'\n```\nprint('<b>&')\nEOF";
        var part = Assert.Single(TelegramMessageFormatter.Command(command));
        Assert.Contains("→ Executando comando\n<pre><code class=\"language-bash\">", part.Html);
        Assert.Contains("```", part.PlainText);
        Assert.EndsWith("EOF\n</code></pre>", part.Html);
        AssertValid(part);
    }

    [Fact]
    public void BlankContentProducesNoMessages()
    {
        Assert.Empty(new TelegramMessageFormatter().Format("\n \n"));
        Assert.Empty(new TelegramMessageFormatter().Format("```\n```\n"));
    }

    internal static void AssertValid(TelegramFormattedMessage part)
    {
        var xml = XElement.Parse("<root>" + part.Html + "</root>", LoadOptions.PreserveWhitespace);
        Assert.Equal(part.PlainText, xml.Value);
        Assert.All(xml.Descendants(), element => Assert.Contains(element.Name.LocalName, new[] { "pre", "code" }));
    }
}
