using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The background block of the general page lists the limits the platform names, the notifications with their
/// state in a word, every row with a button that opens its system screen, and follows the platform when the window
/// comes back.
/// </summary>
public sealed class BackgroundLimitsTests
{
    [Fact]
    public void TheBlock_ListsTheLimitsThePlatformNames()
    {
        var platform = new Platform(
            new BackgroundLimit(BackgroundLimitKind.Notifications, BackgroundLimitState.Limited),
            new BackgroundLimit(BackgroundLimitKind.Battery, BackgroundLimitState.Free));

        var block = platform.Block();

        Assert.True(block.HasRows);
        Assert.Equal([BackgroundLimitKind.Notifications, BackgroundLimitKind.Battery], block.Rows.Select(row => row.Kind));
        Assert.Equal([true, false], block.Rows.Select(row => row.IsLimited));
    }

    [Fact]
    public void APlatformThatNamesNoLimits_HasNoBlock()
    {
        var block = new Platform().Block();

        Assert.False(block.HasRows);
        Assert.Empty(block.Rows);
    }

    [Fact]
    public void AReload_FollowsTheStateAndKeepsTheRow()
    {
        var platform = new Platform(new BackgroundLimit(BackgroundLimitKind.Notifications, BackgroundLimitState.Limited));
        var block = platform.Block();
        var row = Assert.Single(block.Rows);
        var told = new List<string?>();
        row.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        platform.Limits = [new BackgroundLimit(BackgroundLimitKind.Notifications, BackgroundLimitState.Free)];
        block.Reload();

        Assert.Same(row, Assert.Single(block.Rows));
        Assert.False(row.IsLimited);
        Assert.Contains(nameof(BackgroundLimitRow.StateText), told);
        Assert.Contains(nameof(BackgroundLimitRow.IsLimited), told);
    }

    [Fact]
    public void AReload_TakesTheLimitsInTheOrderOfThePlatform()
    {
        var platform = new Platform(new BackgroundLimit(BackgroundLimitKind.Autostart, BackgroundLimitState.Unknown));
        var block = platform.Block();

        platform.Limits =
        [
            new BackgroundLimit(BackgroundLimitKind.Notifications, BackgroundLimitState.Free),
            new BackgroundLimit(BackgroundLimitKind.Battery, BackgroundLimitState.Limited),
            new BackgroundLimit(BackgroundLimitKind.Autostart, BackgroundLimitState.Limited),
        ];
        block.Reload();

        Assert.Equal(
            [BackgroundLimitKind.Notifications, BackgroundLimitKind.Battery, BackgroundLimitKind.Autostart],
            block.Rows.Select(row => row.Kind));
    }

    [Fact]
    public void ALimitThePlatformNamesNoMore_LeavesTheBlock()
    {
        var platform = new Platform(
            new BackgroundLimit(BackgroundLimitKind.Notifications, BackgroundLimitState.Free),
            new BackgroundLimit(BackgroundLimitKind.Autostart, BackgroundLimitState.Limited));
        var block = platform.Block();
        var told = new List<string?>();
        block.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        platform.Limits = [];
        block.Reload();

        Assert.Empty(block.Rows);
        Assert.False(block.HasRows);
        Assert.Contains(nameof(BackgroundLimitsViewModel.HasRows), told);
    }

    [Theory]
    [InlineData("Notifications", "Limited")]
    [InlineData("Notifications", "Free")]
    [InlineData("Battery", "Limited")]
    [InlineData("Battery", "Unknown")]
    [InlineData("Autostart", "Limited")]
    [InlineData("Autostart", "Unknown")]
    public void EveryRow_OffersToConfigure(string kind, string state)
    {
        var row = new BackgroundLimitRow(Enum.Parse<BackgroundLimitKind>(kind), Enum.Parse<BackgroundLimitState>(state), _ => { });

        Assert.Equal(Loc.Instance.Get("Background_Configure"), row.ActionText);
    }

    [Theory]
    [InlineData("Battery", "Limited")]
    [InlineData("Battery", "Free")]
    [InlineData("Autostart", "Limited")]
    [InlineData("Autostart", "Free")]
    public void TheBatteryAndAutostartRows_TellNoState(string kind, string state)
    {
        var row = new BackgroundLimitRow(Enum.Parse<BackgroundLimitKind>(kind), Enum.Parse<BackgroundLimitState>(state), _ => { });

        Assert.False(row.HasState);
        Assert.Equal(string.Empty, row.StateText);
    }

    [Theory]
    [InlineData("Limited", "Background_NotificationsOff")]
    [InlineData("Free", "Background_NotificationsOn")]
    public void TheNotificationsRow_SaysItsState(string state, string stateKey)
    {
        var row = new BackgroundLimitRow(BackgroundLimitKind.Notifications, Enum.Parse<BackgroundLimitState>(state), _ => { });

        Assert.True(row.HasState);
        Assert.Equal(Loc.Instance.Get(stateKey), row.StateText);
    }

    [Theory]
    [InlineData("Notifications", "Background_Notifications")]
    [InlineData("Battery", "Background_Battery")]
    [InlineData("Autostart", "Background_Autostart")]
    public void ARowWhoseStateIsNotRead_KeepsItsNameAndItsButton(string kind, string nameKey)
    {
        var row = new BackgroundLimitRow(Enum.Parse<BackgroundLimitKind>(kind), BackgroundLimitState.Unknown, _ => { });

        Assert.False(row.HasState);
        Assert.False(row.IsLimited);
        Assert.Equal(string.Empty, row.StateText);
        Assert.Equal(Loc.Instance.Get(nameKey), row.Name);
        Assert.Equal(Loc.Instance.Get("Background_Configure"), row.ActionText);
    }

    [Fact]
    public void TheButtonOfARow_OpensTheScreenOfItsLimit()
    {
        var platform = new Platform(
            new BackgroundLimit(BackgroundLimitKind.Notifications, BackgroundLimitState.Limited),
            new BackgroundLimit(BackgroundLimitKind.Autostart, BackgroundLimitState.Limited));
        var block = platform.Block();

        block.Rows[1].OpenCommand.Execute(null);

        Assert.Equal([BackgroundLimitKind.Autostart], platform.Opened);
    }

    [Theory]
    [InlineData("Background_Section")]
    [InlineData("Background_Notifications")]
    [InlineData("Background_NotificationsOn")]
    [InlineData("Background_NotificationsOff")]
    [InlineData("Background_Battery")]
    [InlineData("Background_Autostart")]
    [InlineData("Background_Configure")]
    public void EveryTextOfTheBlock_IsTranslated(string key)
    {
        var english = Loc.GetIn("en", key);
        var russian = Loc.GetIn("ru", key);

        Assert.NotEqual(key, english);
        Assert.NotEqual(key, russian);
        Assert.NotEqual(english, russian);
    }

    [Fact]
    public void TheGeneralPage_CarriesTheBlockOfThePlatform()
    {
        var platform = new Platform(new BackgroundLimit(BackgroundLimitKind.Battery, BackgroundLimitState.Limited));
        BackgroundLimitsBridge.Register(platform.Read, platform.Open);
        try
        {
            var window = new MainWindowViewModel(new NullAgentConnection(), new UiPreferences());

            var row = Assert.Single(window.General.Background.Rows);
            row.OpenCommand.Execute(null);

            Assert.Equal(BackgroundLimitKind.Battery, row.Kind);
            Assert.Equal([BackgroundLimitKind.Battery], platform.Opened);
        }
        finally
        {
            BackgroundLimitsBridge.Register(() => [], _ => { });
        }
    }

    // The platform under the block: names the limits it is told to and remembers which screens were opened.
    private sealed class Platform(params BackgroundLimit[] limits)
    {
        public IReadOnlyList<BackgroundLimit> Limits { get; set; } = limits;

        public List<BackgroundLimitKind> Opened { get; } = [];

        public IReadOnlyList<BackgroundLimit> Read() => Limits;

        public void Open(BackgroundLimitKind kind) => Opened.Add(kind);

        public BackgroundLimitsViewModel Block() => new(Read, Open);
    }
}
