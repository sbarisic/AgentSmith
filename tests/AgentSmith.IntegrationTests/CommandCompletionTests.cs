using System.Text.Json;
using AgentSmith.Cli;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class CommandCompletionTests
{
    [Fact]
    public void CatalogContainsExactlyAdvertisedCommandsWithoutDuplicatesOrHelpSyntax()
    {
        var help = JsonSerializer.SerializeToElement(CommandHelp.Build("all"));
        var advertised = help.GetProperty("groups").EnumerateArray()
            .SelectMany(group => group.GetProperty("Commands").EnumerateArray())
            .Select(entry => entry.GetProperty("Command").GetString()!)
            .Where(command => command.StartsWith('/'))
            .Select(command => command.Split(' ')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(command => command).ToArray();
        var names = CommandCompletion.Catalog.Select(command => command.Name).ToArray();
        Assert.Equal(advertised, names.OrderBy(command => command).ToArray());
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("/end", names); // Paste terminator, not a standalone command.
        Assert.All(CommandCompletion.Catalog, command =>
        {
            Assert.StartsWith("/", command.Name);
            Assert.DoesNotContain(command.Name, c => char.IsWhiteSpace(c) || "<>[]|·".Contains(c));
            Assert.False(string.IsNullOrWhiteSpace(command.Description));
        });
        Assert.Equal(names, Values("/"));
    }

    [Theory]
    [InlineData("/Mo", "/mode,/model,/models")]
    [InlineData("/worker-", "/worker-limits,/worker-budget")]
    [InlineData("/PLANS", "/plans")]
    [InlineData("/not-a-command", "")]
    public void CommandPrefixesAreCaseInsensitive(string text, string expected)
        => Assert.Equal(Split(expected), Values(text));

    [Theory]
    [InlineData("/help ", "workflows,session,tools,queue,settings,all")]
    [InlineData("/mode ", "work,plan,planning,document,documentation,review,inspect,edit,execute")]
    [InlineData("/workflow ", "work")]
    [InlineData("/plan ", "new,show,read,revise,implement,leave")]
    [InlineData("/review ", "--base,--commit,--path")]
    [InlineData("/report ", "export")]
    [InlineData("/MODE REVIEW ", "--base,--commit,--path")]
    [InlineData("/diagnostics report-id ", "errors,warnings,failed,skipped,passed,all")]
    [InlineData("/artifact process:#15 ", "read,search")]
    [InlineData("/agent #1 ", "cancel,message")]
    [InlineData("/recover ", "abandon")]
    [InlineData("/steering ", "remove")]
    [InlineData("/queue ", "remove,clear,continue")]
    [InlineData("/display ", "compact,verbose")]
    [InlineData("/markdown ", "on,off")]
    [InlineData("/context ", "budget")]
    [InlineData("/context budget ", "balanced,max")]
    [InlineData("/delegation ", "proactive,manual")]
    [InlineData("/web-search ", "native,basic,off")]
    [InlineData("/memory ", "list,search,show,add,edit,forget,on,off")]
    [InlineData("/MODE DOC", "document,documentation")]
    [InlineData("/CONTEXT BUDGET M", "max")]
    public void FiniteArgumentsRespectPositionAndPrefix(string text, string expected)
    {
        var suggestions = CommandCompletion.GetSuggestions(text, text.Length);
        Assert.Equal(Split(expected), suggestions.Select(s => s.Value).ToArray());
        Assert.All(suggestions, suggestion =>
        {
            Assert.False(string.IsNullOrWhiteSpace(suggestion.Description));
            Assert.DoesNotContain(suggestion.Value, c => char.IsWhiteSpace(c) || "<>[]|·".Contains(c));
        });
    }

    [Theory]
    [InlineData("/model ")]
    [InlineData("/models ")]
    [InlineData("/attach ")]
    [InlineData("/answer question-id ")]
    [InlineData("/limits ")]
    [InlineData("/worker-budget ")]
    [InlineData("/worker-limits ")]
    [InlineData("/plans ")]
    [InlineData("/diagnostics ")]
    [InlineData("/diagnostics report-id failed ")]
    [InlineData("/artifact ")]
    [InlineData("/artifact id search ")]
    [InlineData("/agent ")]
    [InlineData("/agent #1 message ")]
    [InlineData("/queue remove ")]
    [InlineData("/report export ")]
    [InlineData("/plan new ")]
    [InlineData("/plan revise ")]
    [InlineData("/plan read ")]
    [InlineData("/review --path ")]
    [InlineData("/memory search ")]
    [InlineData("/memory list ")]
    [InlineData("/context other ")]
    [InlineData("/markdown maybe")]
    [InlineData("/unknown ")]
    public void FreeTextIdsPathsModelsNumbersAndInvalidArgumentsAreNotDiscovered(string text)
        => Assert.Empty(CommandCompletion.GetSuggestions(text, text.Length));

    [Theory]
    [InlineData("", 0)]
    [InlineData("/", 0)]
    [InlineData("/mode", -1)]
    [InlineData("/mode", 6)]
    [InlineData("ordinary prompt /mo", 19)]
    [InlineData("@file", 5)]
    [InlineData("/mo\n", 3)]
    [InlineData("/mo\r", 3)]
    [InlineData("  /mode", 2)]
    public void RequiresSingleLineLeadingSlashAndValidCursor(string text, int position)
        => Assert.Empty(CommandCompletion.GetSuggestions(text, position));

    [Theory]
    [InlineData("  /MoXYZ keep suffix", 5, "/mode", 2, 6, "  /mode keep suffix")]
    [InlineData("/mode DOCxyz trailing text", 9, "document", 6, 6, "/mode document trailing text")]
    [InlineData("/context budget maXYZ suffix", 18, "max", 16, 5, "/context budget max suffix")]
    [InlineData("/mode  suffix", 6, "work", 6, 0, "/mode work suffix")]
    [InlineData("\t/MO\t suffix", 4, "/model", 1, 3, "\t/model\t suffix")]
    public void ReplacesWholeCurrentTokenNotTextToItsRight(string text, int position, string value,
        int start, int length, string expected)
    {
        var suggestion = Assert.Single(CommandCompletion.GetSuggestions(text, position), s => s.Value == value);
        Assert.Equal(start, suggestion.Start);
        Assert.Equal(length, suggestion.Length);
        Assert.Equal(expected, text.Remove(suggestion.Start, suggestion.Length).Insert(suggestion.Start, suggestion.Value));
        Assert.Equal(value, suggestion.Value.Trim()); // Spacing belongs to the composer.
    }

    [Fact]
    public void AliasChoicesAreUniqueAndCatalogIsNotMutatedByFiltering()
    {
        var before = CommandCompletion.Catalog.ToArray();
        Assert.Equal(new[] { "plan", "planning" }, Values("/mode pl"));
        Assert.Equal(new[] { "document", "documentation" }, Values("/mode doc"));
        Assert.Equal(before, CommandCompletion.Catalog.ToArray());
        Assert.Equal(Values("/MODE PL"), Values("/mode pl"));
    }

    private static string[] Values(string text)
        => CommandCompletion.GetSuggestions(text, text.Length).Select(s => s.Value).ToArray();
    private static string[] Split(string text)
        => text.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
