using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What the support archive says about the session: when it came up, how often it was raised again and how often
/// the link under it was repaired.
/// </summary>
public sealed class SessionMarksTests
{
    private const long Start = 1_700_000_000_000;

    [Fact]
    public void WithoutASession_TheSectionSaysSo()
    {
        Assert.Equal(["[session]", "up since:        - (no session is up)"], new SessionMarks().Lines(Start));
    }

    [Fact]
    public void ASession_NamesItsRaisesAndItsRepairs()
    {
        var marks = new SessionMarks();
        marks.Raised(Start, "srv");
        marks.Repaired(RecoveryStep.Rebind);
        marks.Repaired(RecoveryStep.Rebind);
        marks.Repaired(RecoveryStep.Resolve);
        marks.Dropped(Start + 60_000);
        marks.Raised(Start + 65_000, "srv");

        var lines = marks.Lines(Start + 95_000);

        Assert.Equal(4, lines.Count);
        Assert.StartsWith("up since:        ", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("(95 s ago)", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("raised again:    1 (last at ", lines[2], StringComparison.Ordinal);
        Assert.Equal("link repairs:    3 (another source port 2, the address of the server resolved again 1)", lines[3]);
    }

    [Fact]
    public void ASessionThatWentDown_NamesSinceWhen()
    {
        var marks = new SessionMarks();
        marks.Raised(Start, "srv");
        marks.Dropped(Start + 10_000);

        Assert.Contains(marks.Lines(Start + 20_000), line => line.StartsWith("down since:      ", StringComparison.Ordinal));

        marks.Raised(Start + 30_000, "srv");

        Assert.DoesNotContain(marks.Lines(Start + 40_000), line => line.StartsWith("down since:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherConfiguration_OpensASessionOfItsOwn()
    {
        var marks = new SessionMarks();
        marks.Raised(Start, "srv");
        marks.Repaired(RecoveryStep.Rebind);
        marks.Raised(Start + 5_000, "other");

        var lines = marks.Lines(Start + 6_000);

        Assert.Equal("raised again:    0", lines[2]);
        Assert.Equal("link repairs:    0", lines[3]);
    }

    [Fact]
    public void AClosedSession_LeavesNoMarks()
    {
        var marks = new SessionMarks();
        marks.Raised(Start, "srv");
        marks.Repaired(RecoveryStep.Rebind);
        marks.Closed();

        Assert.Equal(new SessionMarks().Lines(Start), marks.Lines(Start));
    }

    [Fact]
    public void TheMarks_ReadBackFromTheirLine()
    {
        var marks = new SessionMarks();
        marks.Raised(Start, "srv");
        marks.Repaired(RecoveryStep.Rebind);
        marks.Dropped(Start + 60_000);
        marks.Raised(Start + 65_000, "srv");
        marks.Dropped(Start + 70_000);

        Assert.Equal(marks.Lines(Start + 95_000), SessionMarks.Parse(marks.ToPayload()).Lines(Start + 95_000));
        Assert.Equal(new SessionMarks().Lines(Start), SessionMarks.Parse("not the marks").Lines(Start));
        Assert.Equal(new SessionMarks().Lines(Start), SessionMarks.Parse(null).Lines(Start));
    }

    [Fact]
    public void TheReport_AddsHowManyAddressesCarryAName()
    {
        var held = new SessionReport(
            Start,
            [
                new LiveSession("1.2.3.4", "proxy", IdleSeconds: 3, Name: "a.example"),
                new LiveSession("5.6.7.8", LiveSession.Undecided, IdleSeconds: 9),
                new LiveSession("10.8.0.0/24", "proxy", Name: "not an address the session met"),
            ]);

        Assert.Equal("names in cache:  1 of 2 address(es) the session met", new SessionMarks().Lines(Start, held)[^1]);
    }
}
