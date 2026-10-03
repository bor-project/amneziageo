using System.Collections.Concurrent;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ipc.Fleet;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Fleet;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The tunnel mode of a routing list is picked when the list is made: the templates on offer follow it, a made list
/// only names it, and a full list keeps its tunnel bucket for the rules that name the connection they ride.
/// </summary>
public sealed class RoutingListModeTests
{
    [Fact]
    public void ANewList_OffersTheTemplatesOfTheSelectedOnlyMode()
    {
        var routing = Routing();

        routing.BeginAddListCommand.Execute(null);

        Assert.Equal(0, routing.RoutingSettings!.VpnModeIndex);
        Assert.Equal(["Closed", "Manual", "External"], Rows(routing));
        Assert.Equal("Closed", routing.SelectedTemplate?.Preset?.Key);
        Assert.False(routing.ShowPresetRegion);
    }

    [Fact]
    public void TheFullMode_OffersItsOwnTemplates()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);

        routing.RoutingSettings!.VpnModeIndex = 1;

        Assert.Equal(["AllButLocal", "Everything", "Manual", "External"], Rows(routing));
        Assert.Equal("AllButLocal", routing.SelectedTemplate?.Preset?.Key);
        Assert.True(routing.IsImportPresets);
        Assert.True(routing.ShowPresetRegion);
    }

    [Fact]
    public void TheRegion_StandsOnlyAtThePresetUnfoldedByIt()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);
        routing.RoutingSettings!.VpnModeIndex = 1;

        routing.SelectedTemplate = routing.Templates.Single(row => row.Preset?.Key == "Everything");

        Assert.True(routing.ShowImportPresets);
        Assert.False(routing.ShowPresetRegion);
    }

    [Fact]
    public void TheManualRow_StaysPickedAcrossTheModes()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);
        routing.SelectedTemplate = routing.Templates.Single(row => row.Method == RoutingImportMethod.Manual);

        routing.RoutingSettings!.VpnModeIndex = 1;

        Assert.Equal(RoutingImportMethod.Manual, routing.SelectedTemplate?.Method);
        Assert.True(routing.IsImportManual);
        Assert.True(routing.ShowImportEditor);
    }

    [Fact]
    public void AListByHandInTheFullMode_HasNoTunnelBucket()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);
        routing.SelectedTemplate = routing.Templates.Single(row => row.Method == RoutingImportMethod.Manual);

        routing.RoutingSettings!.VpnModeIndex = 1;

        Assert.True(routing.RoutingEditor!.GlobalProxyActive);
        Assert.False(routing.RoutingEditor.CanUseProxyBucket);
        Assert.Equal("direct", routing.RoutingEditor.SelectedRole);
    }

    [Fact]
    public void TheModeOfAListByHand_ShowsItsOwnBucket()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);
        routing.SelectedTemplate = routing.Templates.Single(row => row.Method == RoutingImportMethod.Manual);
        routing.RoutingSettings!.VpnModeIndex = 1;

        routing.RoutingSettings.VpnModeIndex = 0;

        Assert.True(routing.RoutingEditor!.CanUseProxyBucket);
        Assert.Equal("proxy", routing.RoutingEditor.SelectedRole);
    }

    [Fact]
    public async Task AFullPreset_FillsADraftOfItsMode()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);
        routing.RoutingSettings!.VpnModeIndex = 1;
        routing.SelectedTemplate = routing.Templates.Single(row => row.Preset?.Key == "Everything");

        await routing.ApplyPresetCommand.ExecuteAsync(routing.SelectedTemplate);

        Assert.True(routing.IsImportDraft);
        Assert.True(routing.RoutingSettings.UseGlobalProxy);
        Assert.Empty(routing.RoutingEditor!.ProxyRules);
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
        Assert.True(routing.ShowListMode);
        Assert.Equal("direct", routing.RoutingEditor!.SelectedRole);
    }

    [Fact]
    public void TheEditorUnderTheModeList_DoesNotNameTheModeAgain()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);

        routing.SelectedTemplate = routing.Templates.Single(row => row.Method == RoutingImportMethod.Manual);

        Assert.True(routing.ShowImportMethods);
        Assert.False(routing.ShowListMode);
    }

    [Fact]
    public async Task ADraft_NamesItsMode()
    {
        var routing = Routing();
        routing.BeginAddListCommand.Execute(null);

        await routing.ApplyPresetCommand.ExecuteAsync(routing.SelectedTemplate);

        Assert.False(routing.ShowImportMethods);
        Assert.True(routing.ShowListMode);
    }

    [Fact]
    public void TheCatalogue_ShowsNoModeAtTheTitle()
    {
        var routing = Routing();

        Assert.False(routing.ShowListMode);
    }

    [Fact]
    public void TheAddPage_ShowsNoModeAtTheTitle()
    {
        var routing = Routing();

        routing.BeginAddListCommand.Execute(null);

        Assert.True(routing.ShowImportPresets);
        Assert.False(routing.ShowListMode);
    }

    [Fact]
    public async Task AnOpenList_NamesItsModeAtTheTitle()
    {
        var routing = Routing();
        var card = new RoutingListSummaryViewModel { Id = 7, Name = "home" };
        routing.RoutingLists.Add(card);

        routing.OpenCardCommand.Execute(card);
        await Settled(routing);

        Assert.True(routing.IsSectionSettings);
        Assert.True(routing.ShowListMode);
    }

    [Fact]
    public async Task TheAdvancedScreenOfAnOpenList_KeepsTheModeAtTheTitle()
    {
        var routing = Routing();
        var card = new RoutingListSummaryViewModel { Id = 7, Name = "home" };
        routing.RoutingLists.Add(card);
        routing.OpenCardCommand.Execute(card);
        await Settled(routing);

        routing.SelectRoutingSectionCommand.Execute("advanced");

        Assert.True(routing.IsSectionAdvanced);
        Assert.True(routing.ShowListMode);
    }

    [Fact]
    public async Task AListStillLoading_ShowsNoModeAtTheTitle()
    {
        var gate = new TaskCompletionSource();
        var routing = new MainWindowViewModel(new Silent(gate.Task), new UiPreferences()).Routing;
        var card = new RoutingListSummaryViewModel { Id = 7, Name = "home" };
        routing.RoutingLists.Add(card);

        routing.OpenCardCommand.Execute(card);

        Assert.True(routing.SectionLoading);
        Assert.False(routing.ShowListMode);

        gate.SetResult();
        await Settled(routing);

        Assert.True(routing.ShowListMode);
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
    public void TheCardOfAFullList_CarriesNoUdpTag()
    {
        var card = new RoutingListSummaryViewModel { UseGlobalProxy = true, AllUdp = true };

        Assert.Empty(card.Tags);
    }

    [Fact]
    public void TheUdpTag_GoesWithTheModeOfTheList()
    {
        var card = new RoutingListSummaryViewModel { AllUdp = true };
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.UseGlobalProxy = true;

        Assert.Contains(nameof(RoutingListSummaryViewModel.Tags), raised);
        Assert.Empty(card.Tags);
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

        routing.RoutingSettings!.VpnModeIndex = 1;

        Assert.False(routing.ShowAllUdp);
        Assert.Contains(nameof(RoutingViewModel.ShowAllUdp), raised);
    }

    [Fact]
    public void TheAdvancedScreenOfAFullList_IsSubtitledWithoutUdp()
    {
        Assert.NotEqual("Main_AdvancedSubtitleFull", Loc.Instance.Get("Main_AdvancedSubtitleFull"));
    }

    [Fact]
    public void TheCardOfASelectedOnlyList_NamesItsMode()
    {
        var card = new RoutingListSummaryViewModel();

        Assert.Equal(Loc.Instance.Get("Main_VpnModeSelected"), card.ModeText);
        Assert.NotEqual("Main_VpnModeSelected", card.ModeText);
    }

    [Fact]
    public void TheCardOfAFullList_NamesItsMode()
    {
        var card = new RoutingListSummaryViewModel();
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.UseGlobalProxy = true;

        Assert.Equal(Loc.Instance.Get("Main_VpnModeFull"), card.ModeText);
        Assert.NotEqual("Main_VpnModeFull", card.ModeText);
        Assert.Contains(nameof(RoutingListSummaryViewModel.ModeText), raised);
    }

    [Fact]
    public void TheModeOnACard_FollowsTheLanguage()
    {
        var card = new RoutingListSummaryViewModel();
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.RefreshLocalizedLabels();

        Assert.Contains(nameof(RoutingListSummaryViewModel.ModeText), raised);
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

    // The rows of the template list by what stands behind each.
    private static string[] Rows(RoutingViewModel routing) =>
        [.. routing.Templates.Select(row => row.Preset?.Key ?? row.Method.ToString())];

    // Waits out the load of an opened list.
    private static async Task Settled(RoutingViewModel routing)
    {
        for (var i = 0; i < 200 && routing.SectionLoading; i++)
        {
            await Task.Delay(10);
        }
    }

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
