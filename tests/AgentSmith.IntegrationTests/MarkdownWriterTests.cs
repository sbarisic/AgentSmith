using AgentSmith.Cli;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class MarkdownWriterTests
{
    [Fact]
    public void ArbitraryFragmentsMatchWholeResponseAndKeepCodeLiteral()
    {
        const string source = "### Title\n> A **bold** quote with `code`.\n\n- first\n1. second\n```csharp\n  var text = \"**literal**\";\n```\nFinal *emphasis* and [docs](https://example.test).";
        using var whole = new StringWriter(); using var fragmented = new StringWriter();
        var a = new MarkdownWriter(whole, false, 80); var b = new MarkdownWriter(fragmented, false, 80);
        a.Append(source); a.Finish();
        foreach (var c in source) b.Append(c.ToString());
        b.Finish();
        Assert.Equal(whole.ToString(), fragmented.ToString());
        var text = whole.ToString();
        Assert.Contains("│ ▌ A bold quote with code.", text);
        Assert.Contains("│ • first", text);
        Assert.Contains("│ 1. second", text);
        Assert.Contains("┌─ csharp", text);
        Assert.Contains("  var text = \"**literal**\";", text);
        Assert.Contains("docs (https://example.test)", text);
        Assert.DoesNotContain("###", text);
        Assert.False(text.Contains('\u001b'));
    }

    [Fact]
    public void LongProseWrapsInsideGuideAndFinalPartialLineIsFlushed()
    {
        using var output = new StringWriter();
        var writer = new MarkdownWriter(output, false, 40);
        writer.Append("A long sentence that should wrap at spaces and retain the guide on every line.\n```\n" + new string('x', 100));
        writer.Finish();
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => { Assert.StartsWith("  │ ", line); Assert.True(line.Length < 40); });
        Assert.Equal(100, output.ToString().Count(c => c == 'x'));
        Assert.EndsWith("└─" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void SourceModePreservesMarkupAndFormattingCannotInjectTerminalControls()
    {
        using var raw = new StringWriter();
        var writer = new MarkdownWriter(raw, false, 80, enabled: false);
        writer.Append("**bold** `literal`\u001b[2J\n"); writer.Finish();
        Assert.Contains("**bold** `literal`", raw.ToString());
        Assert.False(raw.ToString().Contains('\u001b'));
        using var styled = new StringWriter();
        var colored = new MarkdownWriter(styled, true, 80);
        colored.Append("**bold**\n"); colored.Finish();
        Assert.Contains("\u001b[1mbold\u001b[0m", styled.ToString());
    }
}
