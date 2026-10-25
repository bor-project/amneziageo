using System.Collections.Concurrent;
using System.Globalization;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Fleet;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// A routing list is made in the mode that tunnels only what it names: the templates of the full mode are not on offer
/// and no mode is picked, while a full list made before keeps its tunnel bucket for the rules that name the connection
/// they ride and is marked unsafe wherever it shows.
/// </summary>
public sealed class RoutingListModeTests
{
    [Fact]
    public void ANewList_OffersTheTemplatesOfTheSelectedOnlyMode()
    {
        var routing = Routing();

        routing.BeginAddListCommand.Execute(null);

        Assert.False(routing.RoutingSettings!.UseGlobalProxy);
        Assert.Equal(["Apps", "Closed", "Manual", "External"], Rows(routing));
        Assert.Equal("Apps", routing.SelectedTemplate?.Preset?.Key);
        Assert.False(routing.ShowPresetRegion);
    }

    [Fact]
    public void ADraftThatTurnsFull_KeepsTheTemplatesOfTheOneMode()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);

        routing.RoutingSettings!.UseGlobalProxy = true;

        Assert.Equal(["Apps", "Closed", "Manual", "External"], Rows(routing));
        Assert.Equal("Apps", routing.SelectedTemplate?.Preset?.Key);
    }

    [Fact]
    public void AnImportedList_KeepsTheModeItWasMadeIn()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);
        routing.SelectedTemplate = routing.Templates.Single(row => row.Method == RoutingImportMethod.External);
        var text = PortableTransfer.EncodeRouting("home", ["direct|10.0.0.0/8"], new PortableTransfer.RoutingOptions(false, true));

        routing.ApplyImportText(text);

        Assert.True(routing.IsImportDraft);
        Assert.True(routing.RoutingSettings!.UseGlobalProxy);
        Assert.True(routing.ShowListUnsafe);
        Assert.Equal("direct", routing.RoutingEditor!.SelectedRole);
    }

    [Fact]
    public void AFullList_HidesTheTunnelBucket()
    {
        var editor = new RoutingListEditorViewModel(new Silent());

        editor.GlobalProxyActive = true;

        Assert.False(editor.CanUseProxyBucket);
        Assert.Equal("direct", editor.SelectedRole);
    }

    [Fact]
    public void AFullListOfTheSet_KeepsTheTunnelBucketForAddressedRules()
    {
        var editor = new FleetRoutingListEditorViewModel(new Silent(), null);
        editor.Describe(["home", "work"], FleetTargets.Unaddressed);

        editor.GlobalProxyActive = true;
        editor.RoleIndex = 0;

        Assert.True(editor.CanUseProxyBucket);
        Assert.True(editor.IsProxyRole);
        Assert.True(editor.CanAddSubnets);
    }

    [Fact]
    public void TheTunnelBucketOfAFullList_TakesNoApplications()
    {
        var editor = new FleetRoutingListEditorViewModel(new Silent(), null);
        editor.Describe(["home", "work"], FleetTargets.Unaddressed);
        editor.AddMethodIndex = 1;

        editor.GlobalProxyActive = true;
        editor.RoleIndex = 0;

        Assert.False(editor.CanAddApps);
        Assert.True(editor.IsAddressMethod);
    }

    [Fact]
    public void TheDirectBucketOfAFullList_TakesApplications()
    {
        var editor = new RoutingListEditorViewModel(new Silent());

        editor.GlobalProxyActive = true;
        editor.AddMethodIndex = 1;

        Assert.True(editor.IsDirectRole);
        Assert.True(editor.CanAddApps);
        Assert.True(editor.IsAppMethod);
    }

    [Fact]
    public void TheBucketsOfAFullListThatTakeNoApplications_NameTheDirectOne()
    {
        var editor = new RoutingListEditorViewModel(new Silent());

        editor.GlobalProxyActive = true;
        editor.RoleIndex = 2;

        Assert.False(editor.CanAddApps);
        Assert.Equal(Loc.Instance.Get("Main_AppOnlyDirectHint"), editor.AppBucketsHint);
    }

    [Fact]
    public void TheDirectBucketOfAListThatTunnelsWhatItNames_TakesApplicationsOnThePhoneAlone()
    {
        var editor = new RoutingListEditorViewModel(new Silent());

        editor.RoleIndex = 1;

        Assert.True(editor.IsDirectRole);
        Assert.Equal(OperatingSystem.IsAndroid(), editor.CanAddApps);
    }

    [Fact]
    public void TheTunnelBucketOfAFullList_SaysItsRulesRideTheirRoutes()
    {
        var editor = new FleetRoutingListEditorViewModel(new Silent(), null);
        editor.Describe(["home", "work"], FleetTargets.Unaddressed);

        editor.GlobalProxyActive = true;
        editor.RoleIndex = 0;

        Assert.Equal(Loc.Instance.Get("Main_RoleProxyRouteHint"), editor.RoleHint);
        Assert.NotEqual("Main_RoleProxyRouteHint", editor.RoleHint);
    }

    [Fact]
    public void TheTunnelBucketOfAFullList_GoesWithTheSet()
    {
        var editor = new FleetRoutingListEditorViewModel(new Silent(), null);
        editor.Describe(["home"], FleetTargets.Unaddressed);
        editor.GlobalProxyActive = true;
        editor.RoleIndex = 0;

        editor.Describe([], FleetTargets.Unaddressed);

        Assert.False(editor.CanUseProxyBucket);
        Assert.Equal("direct", editor.SelectedRole);
    }

    [Fact]
    public void TheCardOfAList_FlipsNoMode()
    {
        var card = new RoutingListSummaryViewModel { AllUdp = true };

        var tag = Assert.Single(card.Tags);

        Assert.Equal(Loc.Instance.Get("Main_AllUdpTitle"), tag.Text);
        Assert.True(tag.Interactive);
    }

    [Fact]
    public void TheUpdateTag_StandsBeforeTheUdpTag()
    {
        var card = new RoutingListSummaryViewModel { HasUpdate = true };

        Assert.Equal([Loc.Instance.Get("Main_CardTagUpdate"), Loc.Instance.Get("Main_CardTagUdp")], card.Tags.Select(tag => tag.Text));
    }

    [Fact]
    public void TheCardOfAFullList_CarriesTheUnsafeTagAlone()
    {
        var card = new RoutingListSummaryViewModel { UseGlobalProxy = true, AllUdp = true };

        var tag = Assert.Single(card.Tags);

        Assert.Equal(Loc.Instance.Get("Main_CardTagUnsafe"), tag.Text);
        Assert.NotEqual("Main_CardTagUnsafe", tag.Text);
        Assert.True(tag.Warn);
        Assert.True(tag.Alert);
        Assert.True(tag.Interactive);
    }

    [Fact]
    public async Task AnOpenFullList_IsMarkedUnsafe()
    {
        var routing = Routing();
        var card = new RoutingListSummaryViewModel { Id = 7, Name = "home", UseGlobalProxy = true };
        routing.RoutingLists.Add(card);

        routing.OpenCardCommand.Execute(card);
        await Settled(routing);

        Assert.True(routing.ShowListUnsafe);
    }

    [Fact]
    public async Task AnOpenListOfTheOneMode_IsNotMarkedUnsafe()
    {
        var routing = Routing();
        var card = new RoutingListSummaryViewModel { Id = 7, Name = "home" };
        routing.RoutingLists.Add(card);

        routing.OpenCardCommand.Execute(card);
        await Settled(routing);

        Assert.False(routing.ShowListUnsafe);
    }

    [Fact]
    public void TheCatalogue_IsNotMarkedUnsafe()
    {
        var routing = Routing();

        routing.RoutingLists.Add(new RoutingListSummaryViewModel { Id = 7, Name = "home", UseGlobalProxy = true });

        Assert.False(routing.ShowListUnsafe);
    }

    [Fact]
    public void TheHomeRowOfAFullListInUse_IsMarkedUnsafe()
    {
        var routing = Routing();
        routing.RoutingLists.Add(new RoutingListSummaryViewModel { Id = 7, Name = "home", UseGlobalProxy = true });
        var raised = new ConcurrentQueue<string?>();
        routing.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

        routing.SelectedRoutingListId = 7;

        Assert.True(routing.HomeRoutingUnsafe);
        Assert.Contains(nameof(RoutingViewModel.HomeRoutingUnsafe), raised);
    }

    [Fact]
    public void TheHomeRowOfAFullListOutOfUse_IsNotMarkedUnsafe()
    {
        var routing = Routing();

        routing.RoutingLists.Add(new RoutingListSummaryViewModel { Id = 7, Name = "home", UseGlobalProxy = true });

        Assert.False(routing.HomeRoutingUnsafe);
    }

    [Fact]
    public void TheHomeRowOfAListOfTheOneMode_IsNotMarkedUnsafe()
    {
        var routing = Routing();
        routing.RoutingLists.Add(new RoutingListSummaryViewModel { Id = 7, Name = "home" });

        routing.SelectedRoutingListId = 7;

        Assert.False(routing.HomeRoutingUnsafe);
    }

    [Fact]
    public void TheLinkOfAnUnsafeList_LeadsToTheRoutingPageOfTheDevice()
    {
        var device = OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsLinux() ? "linux" : "windows";

        Assert.Equal($"https://bor-project.github.io/amneziageo/{device}/menu/routing.ru.html#full-mode", SitePages.FullMode(new CultureInfo("ru")));
        Assert.Equal($"https://bor-project.github.io/amneziageo/{device}/menu/routing.html#full-mode", SitePages.FullMode(new CultureInfo("en")));
    }

    [Fact]
    public void TheUdpTag_GoesWithTheModeOfTheList()
    {
        var card = new RoutingListSummaryViewModel { AllUdp = true };
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.UseGlobalProxy = true;

        Assert.Contains(nameof(RoutingListSummaryViewModel.Tags), raised);
        Assert.Equal([Loc.Instance.Get("Main_CardTagUnsafe")], card.Tags.Select(tag => tag.Text));
    }

    [Fact]
    public void ASelectedOnlyList_OffersTheAllUdpSwitch()
    {
        var routing = Routing();

        routing.BeginAddListCommand.Execute(null);

        Assert.True(routing.ShowAllUdp);
    }

    [Fact]
    public void AFullList_OffersNoAllUdpSwitch()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);
        var raised = new ConcurrentQueue<string?>();
        routing.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

        routing.RoutingSettings!.UseGlobalProxy = true;

        Assert.False(routing.ShowAllUdp);
        Assert.Contains(nameof(RoutingViewModel.ShowAllUdp), raised);
    }

    [Fact]
    public void TheAdvancedScreenOfAFullList_IsSubtitledWithoutUdp()
    {
        Assert.NotEqual("Main_AdvancedSubtitleFull", Loc.Instance.Get("Main_AdvancedSubtitleFull"));
    }

    [Fact]
    public void TheCardOfASelectedOnlyList_CountsItsTunnelRules()
    {
        var card = new RoutingListSummaryViewModel { ProxyRuleCount = 3 };

        Assert.True(card.ShowProxyRules);
        Assert.Equal(Loc.Instance.Get("Main_CardRulesProxy", 3), card.ProxyRulesText);
    }

    [Fact]
    public void TheCardOfASelectedOnlyList_CountsItsTunnelRulesEvenAtNone()
    {
        var card = new RoutingListSummaryViewModel();

        Assert.True(card.ShowProxyRules);
        Assert.Equal(Loc.Instance.Get("Main_CardRulesProxy", 0), card.ProxyRulesText);
    }

    [Fact]
    public void TheCardOfAFullList_CountsNoTunnelRulesAtNone()
    {
        var card = new RoutingListSummaryViewModel();
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.UseGlobalProxy = true;

        Assert.False(card.ShowProxyRules);
        Assert.Contains(nameof(RoutingListSummaryViewModel.ShowProxyRules), raised);
    }

    [Fact]
    public void TheCardOfAFullList_CountsTheRulesOfItsTunnelBucket()
    {
        var card = new RoutingListSummaryViewModel { UseGlobalProxy = true };
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.ProxyRuleCount = 3;

        Assert.True(card.ShowProxyRules);
        Assert.Equal(Loc.Instance.Get("Main_CardRulesProxy", 3), card.ProxyRulesText);
        Assert.Contains(nameof(RoutingListSummaryViewModel.ShowProxyRules), raised);
    }

    [Fact]
    public void TheTemplateList_IsTitled()
    {
        Assert.NotEqual("Main_TemplateTitle", Loc.Instance.Get("Main_TemplateTitle"));
    }

    private static RoutingViewModel Routing() => new MainWindowViewModel(new Silent(), new UiPreferences()).Routing;

    // Waits out the load of an opened list.
    private static async Task Settled(RoutingViewModel routing)
    {
        for (var i = 0; i < 200 && routing.SectionLoading; i++)
        {
            await Task.Delay(10);
        }
    }

    // The rows of the template list by what stands behind each.
    private static string[] Rows(RoutingViewModel routing) =>
        [.. routing.Templates.Select(row => row.Preset?.Key ?? row.Method.ToString())];

    // Answers every command with nothing, past the gate where one is given.
    private sealed class Silent(Task? gate = null) : IAgentConnection
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

        public async Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            if (gate is not null)
            {
                await gate.ConfigureAwait(false);
            }

            return new IpcAck(true, string.Empty);
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
