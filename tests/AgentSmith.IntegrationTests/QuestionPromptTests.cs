using AgentSmith.Cli;
using AgentSmith.Core;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class QuestionPromptTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key, char c = '\0', bool shift = false, bool control = false) => new(c, key, shift, false, control);
    private static void Type(QuestionInput input, string text)
    {
        foreach (var c in text) input.Handle(Key(ConsoleKey.A, c));
    }

    [Fact]
    public void ChoiceAndNoteRequireExplicitSubmission()
    {
        var input = new QuestionInput(["Japanese", "Spanish", "Arabic"]);
        Assert.Equal(-1, input.Selected);
        input.Handle(Key(ConsoleKey.DownArrow));
        input.Handle(Key(ConsoleKey.DownArrow));
        Assert.Null(input.Answer);
        input.Handle(Key(ConsoleKey.Enter));
        Assert.False(input.Closed);
        Assert.Equal(2, input.Selected);
        Type(input, "For travel");
        input.Handle(Key(ConsoleKey.Tab));
        Assert.Null(input.Answer);
        input.Handle(Key(ConsoleKey.Enter));
        Assert.Equal("Arabic\n\nNote: For travel", input.Answer);
    }

    [Fact]
    public void BlankSubmitDoesNotChooseHighlightedOption()
    {
        var input = new QuestionInput(["one", "two"]);
        input.Handle(Key(ConsoleKey.Tab));
        input.Handle(Key(ConsoleKey.Enter));
        Assert.False(input.Closed);
        Assert.Null(input.Answer);
        Assert.Equal(-1, input.Selected);
        Type(input, "Custom answer");
        input.Handle(Key(ConsoleKey.Enter));
        Assert.Equal("Custom answer", input.Answer);
    }

    [Theory]
    [InlineData(ConsoleKey.Escape, false)]
    [InlineData(ConsoleKey.C, true)]
    [InlineData(ConsoleKey.D, true)]
    [InlineData(ConsoleKey.Z, true)]
    public void LeavingNeverSubmitsPartialInput(ConsoleKey key, bool control)
    {
        var input = new QuestionInput(["one"]);
        input.Handle(Key(ConsoleKey.Enter));
        Type(input, "unfinished");
        input.Handle(Key(key, control: control));
        Assert.True(input.Closed);
        Assert.Null(input.Answer);
    }

    [Fact]
    public void QuestionsWithoutChoicesAcceptEditableFreeText()
    {
        var input = new QuestionInput([]);
        input.Handle(Key(ConsoleKey.Enter));
        Assert.False(input.Closed);
        Type(input, "answerx");
        input.Handle(Key(ConsoleKey.Backspace));
        input.Handle(Key(ConsoleKey.Enter));
        Assert.Equal("answer", input.Answer);
    }

    [Fact]
    public void ViewportScrollsThroughChoicesAndSanitizesControls()
    {
        var question = new Question { Text = "Pick", Choices = Enumerable.Range(0, 30).Select(i => $"Choice {i}\u001b[2J\n" + new string('x', 100)).ToArray() };
        var input = new QuestionInput(question.Choices);
        for (var i = 0; i < 29; i++) input.Handle(Key(ConsoleKey.DownArrow));
        var frame = QuestionPrompt.Frame(question, input, 40, 14);
        Assert.True(frame.Length < 14);
        Assert.All(frame, line => { Assert.True(line.Length <= 38); Assert.False(line.Contains('\u001b')); Assert.False(line.Contains('\n')); });
        Assert.Contains(frame, line => line.Contains("> [ ] Choice 29"));
    }
}
