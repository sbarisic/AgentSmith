using AgentSmith.Cli;
using Xunit;

namespace AgentSmith.IntegrationTests;

public sealed class InputAttentionTests
{
    [Fact]
    public void RepaintsAndInvalidAnswersDoNotRepeatTheNotification()
    {
        var title = "Smith - project"; var rings = 0;
        using var attention = new InputAttention(true, () => title, value => title = value, () => rings++);
        attention.WaitFor("question:one");
        attention.WaitFor("question:one");
        Assert.Equal(1, rings); Assert.Equal("[!] Smith - project", title);
        attention.WaitFor("question:two");
        Assert.Equal(2, rings); Assert.Equal("[!] Smith - project", title);
        attention.Working(); Assert.Equal("Smith - project", title);
        attention.WaitFor("composer"); Assert.Equal(3, rings);
    }

    [Fact]
    public void EachReviewStageAndReturnToComposerGetsOneNotification()
    {
        var rings = 0;
        using var attention = new InputAttention(true, () => "AgentSmith", _ => { }, () => rings++);
        attention.WaitFor("plan-review:one:Decision");
        attention.WaitFor("plan-review:one:Decision");
        attention.WaitFor("plan-review:one:Mode");
        attention.Working();
        attention.WaitFor("composer");
        attention.WaitFor("composer");
        attention.Working();
        attention.WaitFor("composer");
        Assert.Equal(4, rings);
    }

    [Fact]
    public void DisposalRestoresTheOriginalTitleAndPreventsFurtherNotifications()
    {
        var title = "PowerShell"; var rings = 0;
        var attention = new InputAttention(true, () => title, value => title = value, () => rings++);
        attention.WaitFor("resume:run"); attention.Dispose(); attention.Dispose();
        attention.WaitFor("composer");
        Assert.Equal("PowerShell", title); Assert.Equal(1, rings);
    }

    [Fact]
    public void DisabledNotificationsNeverTouchTheConsole()
    {
        using var attention = new InputAttention(false,
            () => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), () => throw new InvalidOperationException());
        attention.WaitFor("composer"); attention.Working();
    }

    [Fact]
    public void TitleFailureDoesNotSuppressSoundAndSoundFailureDoesNotAbortInput()
    {
        var rings = 0;
        using var attention = new InputAttention(true,
            () => throw new IOException(), _ => throw new IOException(), () => { rings++; throw new InvalidOperationException(); });
        attention.WaitFor("question:one"); attention.Working();
        Assert.Equal(1, rings);
    }

    [Fact]
    public void EmptyTitleHasAUsefulWaitingLabelAndIsRestoredExactly()
    {
        var title = "";
        using var attention = new InputAttention(true, () => title, value => title = value, () => { });
        attention.WaitFor("composer"); Assert.Equal("[!] AgentSmith", title);
        attention.Working(); Assert.Equal("", title);
        title = "New title";
        attention.WaitFor("composer"); Assert.Equal("[!] New title", title);
        attention.Working(); Assert.Equal("New title", title);
    }
}
