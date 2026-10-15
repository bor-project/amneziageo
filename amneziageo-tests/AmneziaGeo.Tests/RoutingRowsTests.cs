using System.Collections.Specialized;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Ui.Fleet;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The rows of a routing list follow its rules one by one: a long bucket opens with its first rows and gets the
/// rest in portions, and an edit of one rule touches the row of that rule alone.
/// </summary>
public sealed class RoutingRowsTests
{
    private const int First = RoutingListEditorViewModel.FirstRows;

    [Fact]
    public async Task AShortBucket_OpensWhole()
    {
        var editor = await OpenAsync(3);

        Assert.Equal(editor.Rules, Tokens(editor));
        Assert.False(editor.RowsPending);
        Assert.Equal(0, editor.ShowMoreRows(10));
    }

    [Fact]
    public async Task ALongBucket_OpensWithItsFirstRows()
    {
        var editor = await OpenAsync(100);

        Assert.Equal(editor.Rules.Take(First), Tokens(editor));
        Assert.True(editor.RowsPending);
    }

    [Fact]
    public async Task TheRestOfALongBucket_FollowsInPortions()
    {
        var editor = await OpenAsync(100);
        var first = editor.RuleItems.ToList();

        var built = editor.ShowMoreRows(25);

        Assert.Equal(25, built);
        Assert.Equal(editor.Rules.Take(First + 25), Tokens(editor));
        Assert.True(editor.RowsPending);
        Kept(first, editor.RuleItems.Take(First));

        built = editor.ShowMoreRows(500);

        Assert.Equal(100 - First - 25, built);
        Assert.Equal(editor.Rules, Tokens(editor));
        Assert.False(editor.RowsPending);
    }

    [Fact]
    public async Task ARemovedRule_TakesItsRowAlone()
    {
        var editor = await OpenWholeAsync(100);
        var rows = editor.RuleItems.ToList();
        var changes = Changes(editor);

        editor.RemoveRuleCommand.Execute(rows[40].Token);

        rows.RemoveAt(40);
        Kept(rows, editor.RuleItems);
        Assert.Equal([NotifyCollectionChangedAction.Remove], changes);
        Assert.False(editor.RowsPending);
    }

    [Fact]
    public async Task AnAddedRule_GetsItsRowAlone()
    {
        var editor = await OpenWholeAsync(100);
        var rows = editor.RuleItems.ToList();
        var changes = Changes(editor);

        editor.RuleInput = "added.example";
        editor.PickSuggestionCommand.Execute(editor.MatchedSuggestions[0].Token);

        Assert.Equal(101, editor.RuleItems.Count);
        Kept(rows, editor.RuleItems.Take(100));
        Assert.Equal("domain:added.example", editor.RuleItems[100].Token);
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
        Assert.False(editor.RowsPending);
    }

    [Fact]
    public async Task ARuleRemovedBelowTheRowsShown_LeavesThemBe()
    {
        var editor = await OpenAsync(100);
        var rows = editor.RuleItems.ToList();
        var changes = Changes(editor);

        editor.RemoveRuleCommand.Execute(editor.Rules[70]);

        Kept(rows, editor.RuleItems);
        Assert.Empty(changes);
        Assert.True(editor.RowsPending);

        ShowAll(editor);

        Assert.Equal(99, editor.RuleItems.Count);
        Assert.Equal(editor.Rules, Tokens(editor));
    }

    [Fact]
    public async Task ARuleRemovedAmongTheRowsShown_KeepsTheBucketOpening()
    {
        var editor = await OpenAsync(100);
        var rows = editor.RuleItems.ToList();

        editor.RemoveRuleCommand.Execute(rows[3].Token);

        rows.RemoveAt(3);
        Assert.Equal(editor.Rules.Take(First), Tokens(editor));
        Kept(rows, editor.RuleItems.Take(First - 1));
        Assert.True(editor.RowsPending);
    }

    [Fact]
    public async Task ARuleAddedToABucketStillOpening_WaitsForItsPortion()
    {
        var editor = await OpenAsync(100);
        var rows = editor.RuleItems.ToList();
        var changes = Changes(editor);

        editor.RuleInput = "added.example";
        editor.PickSuggestionCommand.Execute(editor.MatchedSuggestions[0].Token);

        Kept(rows, editor.RuleItems);
        Assert.Empty(changes);

        ShowAll(editor);

        Assert.Equal(101, editor.RuleItems.Count);
        Assert.Equal("domain:added.example", editor.RuleItems[100].Token);
    }

    [Fact]
    public async Task AnotherBucket_OpensWithItsFirstRows()
    {
        var editor = await OpenWholeAsync(100, 50);

        editor.RoleIndex = 1;

        Assert.Equal(editor.DirectRules.Take(First), Tokens(editor));
        Assert.True(editor.RowsPending);

        editor.RoleIndex = 0;

        Assert.Equal(editor.ProxyRules.Take(First), Tokens(editor));
        Assert.True(editor.RowsPending);
    }

    [Fact]
    public async Task ASortedBucket_OpensAnew()
    {
        var editor = await OpenWholeAsync(100);

        editor.SortRulesCommand.Execute(null);
        editor.SortRulesCommand.Execute(null);

        Assert.Equal("domain:host0100.example", editor.Rules[0]);
        Assert.Equal(editor.Rules.Take(First), Tokens(editor));
        Assert.True(editor.RowsPending);
    }

    [Fact]
    public async Task Cancel_PutsTheRemovedRowsBack_AndKeepsTheRest()
    {
        var editor = await OpenWholeAsync(100);
        var rows = editor.RuleItems.ToList();
        editor.RemoveRuleCommand.Execute(rows[5].Token);
        editor.RemoveRuleCommand.Execute(rows[60].Token);

        editor.Revert();

        Assert.Equal(rows.Select(row => row.Token), Tokens(editor));
        Assert.False(editor.RowsPending);
        for (var at = 0; at < rows.Count; at++)
        {
            if (at is not (5 or 60))
            {
                Assert.Same(rows[at], editor.RuleItems[at]);
            }
        }
    }

    [Fact]
    public async Task TheClearedBucket_HasNoRows()
    {
        var editor = await OpenAsync(100);

        editor.RequestClearRulesCommand.Execute(null);
        editor.ClearRulesCommand.Execute(null);

        Assert.Empty(editor.RuleItems);
        Assert.False(editor.RowsPending);
        Assert.False(editor.HasRules);
    }

    [Fact]
    public async Task AnOpenRow_StaysOpen_WhenAnotherRuleGoes()
    {
        var editor = new RoutingListEditorViewModel(new Lists("proxy|geosite:youtube\nproxy|domain:one.example\nproxy|domain:two.example"), 6, "main");
        await editor.LoadAsync();
        var row = editor.RuleItems[0];
        await editor.ToggleRuleDetailsCommand.ExecuteAsync(row);

        editor.RemoveRuleCommand.Execute("domain:one.example");

        Assert.Same(row, editor.RuleItems[0]);
        Assert.True(row.IsExpanded);
        Assert.Equal(["geosite:youtube", "domain:two.example"], Tokens(editor));
    }

    [Fact]
    public async Task AnImportedList_OpensWithItsFirstRows()
    {
        var editor = await OpenAsync(3);
        var rules = Enumerable.Range(1, 80).Select(number => $"proxy|domain:new{number:D4}.example").ToList();

        Assert.True(editor.ApplyImport(PortableTransfer.EncodeRouting("big", rules, new PortableTransfer.RoutingOptions(false, false))));

        Assert.Equal(80, editor.Rules.Count);
        Assert.Equal(editor.Rules.Take(First), Tokens(editor));
        Assert.True(editor.RowsPending);
    }

    [Fact]
    public async Task ARuleAddressedInTheSet_KeepsEveryRow()
    {
        var editor = await OpenInTheSetAsync(40);
        ShowAll(editor);
        var rows = editor.RuleItems.ToList();
        var targets = new Dictionary<string, string> { [FleetTargets.Key(6, rows[3].Token)] = "work,direct" };

        editor.Describe(["home", "work"], targets);

        Kept(rows, editor.RuleItems);
        var row = Assert.IsType<FleetRoutingRuleItemViewModel>(editor.RuleItems[3]);
        Assert.Equal("work", row.Route?.Word);
        Assert.Equal(RuleTarget.Direct, row.Fallback?.Word);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task AnotherSetOfServers_OpensTheBucketAnew()
    {
        var editor = await OpenInTheSetAsync(40);
        ShowAll(editor);

        editor.Describe([], FleetTargets.Unaddressed);

        Assert.Equal(editor.Rules.Take(First), Tokens(editor));
        Assert.All(editor.RuleItems, row => Assert.IsNotType<FleetRoutingRuleItemViewModel>(row));
        Assert.True(editor.RowsPending);
    }

    [Fact]
    public async Task Cancel_ShowsWhereARuleRodeBefore_InTheSameRow()
    {
        var editor = await OpenInTheSetAsync(5);
        var row = Assert.IsType<FleetRoutingRuleItemViewModel>(editor.RuleItems[2]);
        row.Route = row.RouteChoices.Single(choice => choice.Word == "work");
        Assert.True(editor.IsDirty);

        editor.Revert();

        Assert.Same(row, editor.RuleItems[2]);
        Assert.Equal(RuleTarget.Auto, row.Route?.Word);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public async Task AListOpenedInTheSet_NamesTheServersFromItsFirstRow()
    {
        var link = new Lists(Listed(5, 0));
        var prefs = new UiPreferences();
        var routing = new FleetRoutingViewModel(new MainWindowViewModel(link, prefs), link, prefs);
        routing.Apply(new StatusSnapshot("1", null, [], MultiServer: true, Fleet: new FleetSnapshot(
        [
            new FleetEntry("home", TunnelRoles.Primary, true, Slot: 1),
            new FleetEntry("work", TunnelRoles.Reserve, true, Slot: 2),
        ])));
        var card = new RoutingListSummaryViewModel { Id = 6, Name = "main" };
        routing.RoutingLists.Add(card);

        routing.OpenCardCommand.Execute(card);
        for (var i = 0; i < 200 && routing.SectionLoading; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(5, routing.RoutingEditor!.RuleItems.Count);
        Assert.All(routing.RoutingEditor.RuleItems, row => Assert.IsType<FleetRoutingRuleItemViewModel>(row));
    }

    private static async Task<RoutingListEditorViewModel> OpenAsync(int tunnel, int direct = 0)
    {
        var editor = new RoutingListEditorViewModel(new Lists(Listed(tunnel, direct)), 6, "main");
        await editor.LoadAsync();
        return editor;
    }

    private static async Task<RoutingListEditorViewModel> OpenWholeAsync(int tunnel, int direct = 0)
    {
        var editor = await OpenAsync(tunnel, direct);
        ShowAll(editor);
        return editor;
    }

    private static async Task<FleetRoutingListEditorViewModel> OpenInTheSetAsync(int tunnel)
    {
        var editor = new FleetRoutingListEditorViewModel(new Lists(Listed(tunnel, 0)), 6, "main", null);
        editor.Describe(["home", "work"], FleetTargets.Unaddressed);
        await editor.LoadAsync();
        return editor;
    }

    // The list as the agent hands it over: rules through the tunnel, then direct ones.
    private static string Listed(int tunnel, int direct) => string.Join('\n', Enumerable.Range(1, tunnel)
        .Select(number => $"proxy|domain:host{number:D4}.example")
        .Concat(Enumerable.Range(1, direct).Select(number => $"direct|domain:near{number:D4}.example")));

    // Asks for portions until the shown bucket has every row.
    private static void ShowAll(RoutingListEditorViewModel editor)
    {
        for (var i = 0; i < 1000 && editor.RowsPending; i++)
        {
            editor.ShowMoreRows(40);
        }
    }

    private static string[] Tokens(RoutingListEditorViewModel editor) => [.. editor.RuleItems.Select(row => row.Token)];

    // The changes the rows go through from here on.
    private static List<NotifyCollectionChangedAction> Changes(RoutingListEditorViewModel editor)
    {
        var changes = new List<NotifyCollectionChangedAction>();
        editor.RuleItems.CollectionChanged += (_, e) => changes.Add(e.Action);
        return changes;
    }

    // The rows are the very ones that stood there before.
    private static void Kept(IEnumerable<RoutingRuleItemViewModel> before, IEnumerable<RoutingRuleItemViewModel> now)
    {
        var was = before.ToList();
        var stands = now.ToList();
        Assert.Equal(was.Count, stands.Count);
        for (var at = 0; at < was.Count; at++)
        {
            Assert.Same(was[at], stands[at]);
        }
    }

    // Answers the list with the rules it was given, and every other command with nothing.
    private sealed class Lists(string rules) : IAgentConnection
    {
        public event Action? Connected
        {
            add { }
            remove { }
        }

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command) => Task.FromResult(command.Op == IpcContract.OpGetRoutingList
            ? new IpcAck(true, rules)
            : new IpcAck(true, string.Empty));

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
