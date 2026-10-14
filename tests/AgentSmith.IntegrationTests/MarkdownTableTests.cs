using AgentSmith.Cli;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class MarkdownTableTests
{
    [Fact]
    public void StreamedTableWrapsColumnsAndMatchesWholeResponse()
    {
        const string source = "What's left\n\n| Area | Remaining work |\n|---|---|\n" +
            "| **Combat responsiveness** | Local movement prediction and server reconciliation, latency testing. |\n" +
            "| Lifecycle | Exercise death and respawn in the browser, including remote observers. |\n\nNext steps.";
        var whole = Render(source, 80);
        Assert.Equal(whole, Render(source, 80, fragmented: true));
        Assert.Contains("┌", whole); Assert.Contains("┬", whole); Assert.Contains("┼", whole); Assert.Contains("┘", whole);
        Assert.DoesNotContain("|---|", whole);
        Assert.DoesNotContain("**", whole);
        Assert.Contains("Next steps.", whole);
        var rows = Lines(whole).Where(line => line.StartsWith("  │ │", StringComparison.Ordinal)).ToArray();
        Assert.True(rows.Length > 3);
        Assert.Single(rows.Select(line => line.Length).Distinct());
        Assert.All(Lines(whole), line => Assert.True(line.Length < 80));
        foreach (var word in new[] { "prediction", "reconciliation,", "latency", "remote", "observers." }) Assert.Contains(word, whole);
    }

    [Fact]
    public void NarrowTablesUseHeaderLabelsAndPreserveEachValue()
    {
        var output = Render("| Name | State | Detail |\n|---|---|---|\n| One | Pending | Need a browser check |\n| Two | Done | Verified |", 32);
        Assert.Contains("  │ Name", output);
        Assert.Contains("  │   One", output);
        Assert.Contains("  │ State", output);
        Assert.Contains("Need a browser check", output);
        Assert.Contains("Verified", output);
        Assert.DoesNotContain("┬", output);
        Assert.All(Lines(output), line => Assert.True(line.Length < 32));
    }

    [Fact]
    public void EscapedAndInlineCodePipesAreCellContent()
    {
        var output = Render("Key | Value\n:--- | ---:\nA\\|B | `x|y`\nFinal", 80);
        Assert.Contains("A|B", output); Assert.Contains("x|y", output);
        Assert.DoesNotContain("\\|", output);
        Assert.DoesNotContain("`", output);
        Assert.Contains("│        x|y │", output);
        Assert.Contains("Final", output);
    }

    [Fact]
    public void ProseCodeAndDisabledMarkdownKeepLiteralPipes()
    {
        const string table = "| A | B |\n|---|---|\n| one | two |";
        var prose = Render("A | B\nThis is prose.\nLast | line", 80);
        Assert.Contains("A | B", prose); Assert.Contains("Last | line", prose);
        Assert.DoesNotContain("┬", prose);
        var code = Render("```text\n" + table + "\n```", 80);
        Assert.Contains("|---|---|", code); Assert.DoesNotContain("┬", code);
        var disabled = Render(table, 80, enabled: false);
        Assert.Contains("|---|---|", disabled); Assert.DoesNotContain("┬", disabled);
    }

    [Fact]
    public void EmptyCellsHeaderOnlyAndConsecutiveTablesFinishCleanly()
    {
        var empty = Render("| A | B |\n|---|---|\n| | data |", 80);
        Assert.Contains("data", empty); Assert.Contains("┘", empty);
        var output = Render("| A | B |\n|---|---|\n\n| C | D |\n|---|---|", 80);
        Assert.Equal(2, output.Count(c => c == '┌'));
        Assert.Equal(2, output.Count(c => c == '┘'));
        Assert.Contains("C", output); Assert.Contains("D", output);
    }

    [Fact]
    public void MalformedRowEndsTableWithoutLosingText()
    {
        var output = Render("| A | B |\n|---|---|\n| x | y |\n| extra | column | retained |\nAfter", 80);
        Assert.Contains("| extra | column | retained |", output);
        Assert.Contains("After", output);
        Assert.Equal(1, output.Count(c => c == '┘'));
        var invalid = Render("| A | B |\n|--|no delimiter|", 80);
        Assert.Contains("|--|no delimiter|", invalid);
        Assert.DoesNotContain("┬", invalid);
    }

    [Fact]
    public void TableContentCannotInjectEscapesAndRetainsInlineStyling()
    {
        var plain = Render("| A | B |\n|---|---|\n| **bold** | safe\u001b[2J |", 80);
        Assert.DoesNotContain("\u001b", plain, StringComparison.Ordinal);
        Assert.Contains("safe", plain);
        var styled = Render("| A | B |\n|---|---|\n| **bold** | white |", 80, color: true);
        Assert.Contains("\u001b[1;92mbold\u001b[0m", styled);
        Assert.Contains("\u001b[97mwhite\u001b[0m", styled);
    }

    [Fact]
    public void WideCharactersAndLongWordsPreserveContentWithinColumns()
    {
        var source = "| Key | Value |\n|---|---|\n| 界界界 | " + new string('x', 200) + " |";
        var output = Render(source, 50);
        Assert.Equal(3, output.Count(c => c == '界'));
        Assert.Equal(200, output.Count(c => c == 'x'));
        Assert.All(Lines(output), line => Assert.True(line.Length + line.Count(c => c == '界') < 50));
    }

    [Fact]
    public void RowsAreWrittenBeforeCompletionAndLongLiteralLinesRemainBounded()
    {
        using var output = new StringWriter();
        var writer = new MarkdownWriter(output, false, 80);
        writer.Append("| A | B |\n|---|---|\n| first | streamed |\n");
        Assert.Contains("streamed", output.ToString());
        writer.Append(new string('z', 20000)); writer.Finish();
        Assert.Equal(20000, output.ToString().Count(c => c == 'z'));
        Assert.All(Lines(output.ToString()), line => Assert.True(line.Length < 80));
    }

    private static string[] Lines(string value) => value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static string Render(string source, int width, bool fragmented = false, bool enabled = true, bool color = false)
    {
        using var output = new StringWriter();
        var writer = new MarkdownWriter(output, color, width, enabled);
        if (fragmented) foreach (var c in source) writer.Append(c.ToString());
        else writer.Append(source);
        writer.Finish();
        return output.ToString();
    }
}
