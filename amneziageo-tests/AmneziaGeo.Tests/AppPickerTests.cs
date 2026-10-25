using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The app picker shows every program the device lets it see at once, and saving it touches only the rules of the
/// programs it offered.
/// </summary>
public sealed class AppPickerTests
{
    [Fact]
    public void EveryProgram_IsShownAtOnce()
    {
        var picker = Picker();

        Assert.Equal(["org.auto", "org.browser", "org.downloads", "org.mail"], Shown(picker));
    }

    [Fact]
    public void ASearch_FindsAProgramByItsName()
    {
        var picker = Picker();

        picker.Query = " Auto ";

        Assert.Equal(["org.auto"], Shown(picker));
    }

    [Fact]
    public void ASearch_FindsAProgramByItsPackage()
    {
        var picker = Picker();

        picker.Query = "org.down";

        Assert.Equal(["org.downloads"], Shown(picker));
    }

    [Fact]
    public void ThePicker_AnswersWithEveryRowAndTheMarkedOnes()
    {
        var picker = Picker("org.mail");
        picker.Query = "auto";
        picker.Shown[0].Picked = true;

        Assert.Equal(["org.auto", "org.browser", "org.downloads", "org.mail"], picker.Offered);
        Assert.Equal(["org.auto", "org.mail"], picker.Picked);
    }

    [Fact]
    public void ARuleOnAProgramThePickerDidNotOffer_StaysAfterSave()
    {
        var editor = Editor("app:pkg=org.browser", "app:pkg=org.gone", "geoip:ru");

        editor.ApplyPickedApps("proxy", ["org.browser", "org.mail"], ["org.browser", "org.mail"]);

        Assert.Equal(["app:pkg=org.browser", "app:pkg=org.gone", "geoip:ru", "app:pkg=org.mail"], editor.ProxyRules);
    }

    [Fact]
    public void AnUnmarkedProgram_LosesItsRule()
    {
        var editor = Editor("app:pkg=org.browser", "app:pkg=org.gone", "app:pkg=org.mail");

        editor.ApplyPickedApps("proxy", ["org.browser", "org.mail"], ["org.mail"]);

        Assert.Equal(["app:pkg=org.gone", "app:pkg=org.mail"], editor.ProxyRules);
    }

    [Fact]
    public void KeptRules_StayInTheirPlaces()
    {
        var editor = Editor("app:pkg=org.mail", "geoip:ru", "app:pkg=org.browser");

        editor.ApplyPickedApps("proxy", ["org.auto", "org.browser", "org.mail"], ["org.auto", "org.browser", "org.mail"]);

        Assert.Equal(["app:pkg=org.mail", "geoip:ru", "app:pkg=org.browser", "app:pkg=org.auto"], editor.ProxyRules);
    }

    [Fact]
    public void ThePick_GoesToTheGroupItWasOpenedFor()
    {
        var editor = Editor("app:pkg=org.browser");

        editor.ApplyPickedApps("direct", ["org.browser", "org.mail"], ["org.mail"]);

        Assert.Equal(["app:pkg=org.browser"], editor.ProxyRules);
        Assert.Equal(["app:pkg=org.mail"], editor.DirectRules);
    }

    // A picker over four programs.
    private static AppPickerViewModel Picker(params string[] picked)
    {
        var picker = new AppPickerViewModel();
        picker.Load(
        [
            new AppPickerRow("Auto", "org.auto", picked.Contains("org.auto")),
            new AppPickerRow("Browser", "org.browser", picked.Contains("org.browser")),
            new AppPickerRow("Download manager", "org.downloads", picked.Contains("org.downloads")),
            new AppPickerRow("Mail", "org.mail", picked.Contains("org.mail")),
        ]);
        return picker;
    }

    private static string[] Shown(AppPickerViewModel picker) => [.. picker.Shown.Select(row => row.Package)];

    // An editor whose tunnel group carries the rules.
    private static RoutingListEditorViewModel Editor(params string[] rules)
    {
        var editor = new RoutingListEditorViewModel(new Silent());
        foreach (var rule in rules)
        {
            editor.ProxyRules.Add(rule);
        }

        return editor;
    }

    // Answers every command with nothing.
    private sealed class Silent : IAgentConnection
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

        public Task<IpcAck> SendCommandAsync(IpcCommand command) => Task.FromResult(new IpcAck(true, string.Empty));

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }
}
