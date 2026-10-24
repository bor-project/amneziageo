using System.Buffers.Binary;
using System.Text;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.ViewModels;
using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The form app:&lt;name&gt;: a program named the way people know it, which the device finds itself. What it found is
/// kept beside the lists, a connect routes by it at once, a name nothing stands for is skipped, and the window and the
/// check read it.
/// </summary>
public sealed class AppNamesTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-apps-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    private static readonly IReadOnlyList<NamedApp> Programs =
    [
        new("Telegram Desktop", @"C:\Users\anna\AppData\Roaming\Telegram Desktop\Telegram.exe", string.Empty, @"path=C:\Users\anna\AppData\Roaming\Telegram Desktop\Telegram.exe"),
        new("Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", string.Empty, @"path=C:\Program Files\Mozilla Firefox\firefox.exe"),
        new("Telegram", string.Empty, "org.telegram.messenger", "pkg=org.telegram.messenger"),
        new("Visual Studio Code", "/usr/share/code/code", string.Empty, "path=/usr/share/code/code"),
        new("Visual Studio 2022", string.Empty, string.Empty, @"dir=C:\Program Files\Microsoft Visual Studio\2022"),
    ];

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("Telegram", true)]
    [InlineData(" Visual Studio Code ", true)]
    [InlineData(@"path=C:\Apps\Tool.exe", false)]
    [InlineData(@"C:\Apps\Tool.exe", false)]
    [InlineData("/usr/bin/tool", false)]
    [InlineData("pkg=org.telegram.messenger", false)]
    [InlineData("", false)]
    public void AValue_NamesAProgramWithoutAKindOrAFolder(string value, bool name)
    {
        Assert.Equal(name, AppNames.IsName(value));
    }

    [Fact]
    public void AName_TakesEveryProgramItIsTheLabelFileOrPackageOf()
    {
        Assert.Equal([@"path=C:\Users\anna\AppData\Roaming\Telegram Desktop\Telegram.exe", "pkg=org.telegram.messenger"], AppNames.Pick("telegram", Programs));
        Assert.Equal([@"path=C:\Program Files\Mozilla Firefox\firefox.exe"], AppNames.Pick("FIREFOX", Programs));
        Assert.Equal(["pkg=org.telegram.messenger"], AppNames.Pick("org.telegram.messenger", Programs));
    }

    [Fact]
    public void AName_TakesTheOnlyLabelItStartsAsAWord()
    {
        IReadOnlyList<NamedApp> desktop = [Programs[0], Programs[1]];

        Assert.Equal([@"path=C:\Users\anna\AppData\Roaming\Telegram Desktop\Telegram.exe"], AppNames.Pick("Telegram", desktop));
        Assert.Empty(AppNames.Pick("Tele", desktop));
    }

    [Fact]
    public void AName_TwoLabelsStartWith_TakesNothing()
    {
        Assert.Empty(AppNames.Pick("Visual Studio", Programs));
    }

    [Fact]
    public void AName_NeverTakesThisApplication()
    {
        IReadOnlyList<NamedApp> own = [new("AmneziaGeo", @"C:\Program Files\AmneziaGeo\AmneziaGeo.exe", string.Empty, @"path=C:\Program Files\AmneziaGeo\AmneziaGeo.exe")];

        Assert.Empty(AppNames.Pick("AmneziaGeo", own));
    }

    [Fact]
    public void AName_DropsAProgramTheFolderItFoundHolds()
    {
        IReadOnlyList<NamedApp> docker =
        [
            new("Docker Desktop", string.Empty, string.Empty, @"dir=C:\Program Files\Docker\Docker"),
            new("Docker Desktop", @"C:\Program Files\Docker\Docker\Docker Desktop.exe", string.Empty, @"path=C:\Program Files\Docker\Docker\Docker Desktop.exe"),
        ];

        Assert.Equal([@"dir=C:\Program Files\Docker\Docker"], AppNames.Pick("Docker Desktop", docker));
    }

    [Fact]
    public void AName_KeepsAPackageAndAProgramOfIt()
    {
        const string exe = @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_11.2407.9.0_x64__8wekyb3d8bbwe\Notepad\Notepad.exe";
        IReadOnlyList<NamedApp> notepad =
        [
            new("Notepad", string.Empty, "Microsoft.WindowsNotepad_8wekyb3d8bbwe", "pkg=Microsoft.WindowsNotepad_8wekyb3d8bbwe"),
            new("Notepad", exe, string.Empty, "path=" + exe),
        ];

        Assert.Equal(["pkg=Microsoft.WindowsNotepad_8wekyb3d8bbwe", "path=" + exe], AppNames.Pick("Notepad", notepad));
    }

    [Fact]
    public void TheValues_PutWhatWasFoundInPlaceOfANameAndSkipANameNothingStandsFor()
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Telegram"] = ["pkg=org.telegram.messenger"],
            ["Discord"] = [],
        };

        var values = AppNames.Expand(["telegram", "Discord", "Zoom", "pkg=org.example"], found);

        Assert.Equal(["pkg=org.telegram.messenger", "pkg=org.example"], values);
    }

    [Fact]
    public async Task ASearch_KeepsWhatEachNameStandsForAndTellsWhatChanged()
    {
        var first = await AppFinds.SearchAsync(_store, ["Firefox", "Discord"], Programs);
        var again = await AppFinds.SearchAsync(_store, ["Firefox", "Discord"], Programs);
        var gone = await AppFinds.SearchAsync(_store, ["Firefox"], []);
        var kept = await AppFinds.ReadAsync(_store);

        Assert.Equal(["Firefox"], first.Changed);
        Assert.Equal(["Discord"], first.Missing);
        Assert.Empty(again.Changed);
        Assert.Equal(["Discord"], again.Missing);
        Assert.Equal(["Firefox"], gone.Changed);
        Assert.Empty(kept["firefox"]);
        Assert.Empty(kept["Discord"]);
    }

    [Fact]
    public async Task AList_RoutesByWhatWasFoundLastInPlaceOfItsNames()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        var id = await geo.ApplyToRoutingListAsync(0, "Apps", ["proxy|app:Firefox", "direct|app:Discord", "proxy|geosite:youtube", @"proxy|app:path=C:\Tools\tool.exe"]);
        await AppFinds.SearchAsync(_store, ["Firefox", "Discord"], Programs);
        var list = (await _store.GetRoutingListAsync(id))!;

        var routed = await AppFinds.ExpandListAsync(_store, list);

        Assert.Equal(
            [
                new GeoRule(GeoRuleKind.App, @"path=C:\Program Files\Mozilla Firefox\firefox.exe", RouteRole.Proxy),
                new GeoRule(GeoRuleKind.GeoSite, "youtube", RouteRole.Proxy),
                new GeoRule(GeoRuleKind.App, @"path=C:\Tools\tool.exe", RouteRole.Proxy),
            ],
            routed.Rules);
        Assert.Equal([@"path=C:\Program Files\Mozilla Firefox\firefox.exe", @"path=C:\Tools\tool.exe"], routed.Apps);
        Assert.Empty(routed.DirectApps);
    }

    [Fact]
    public async Task AListThatNamesNoProgram_StandsAsItIs()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        var id = await geo.ApplyToRoutingListAsync(0, "Sites", ["proxy|geosite:youtube"]);
        var list = (await _store.GetRoutingListAsync(id))!;
        var stamp = await _store.GetRoutingListStampAsync(id);

        Assert.Same(list, await AppFinds.ExpandListAsync(_store, list));
        Assert.Same(stamp, AppFinds.ExpandStamp(stamp, new Dictionary<string, IReadOnlyList<string>>()));
    }

    [Fact]
    public async Task TheStampOfAList_ChangesWithWhatWasFound()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        var id = await geo.ApplyToRoutingListAsync(0, "Apps", ["proxy|app:Telegram"]);
        var stamp = await _store.GetRoutingListStampAsync(id);
        var none = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["Telegram"] = [] };
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["Telegram"] = ["pkg=org.telegram.messenger"] };

        Assert.Empty(AppFinds.ExpandStamp(stamp, none)!.Rules);
        Assert.Equal("pkg=org.telegram.messenger", Assert.Single(AppFinds.ExpandStamp(stamp, found)!.Rules).Value);
    }

    [Fact]
    public async Task ACheckOfAName_AsksAboutWhatWasFound()
    {
        await AppFinds.SearchAsync(_store, ["Firefox", "Discord"], Programs);

        Assert.Equal(@"app:path=C:\Program Files\Mozilla Firefox\firefox.exe", await AppFinds.TargetAsync(_store, "app:firefox"));
        Assert.Equal("app:Discord", await AppFinds.TargetAsync(_store, "app:Discord"));
        Assert.Equal("example.com", await AppFinds.TargetAsync(_store, "example.com"));
    }

    [Fact]
    public async Task TheSearchOfTheLists_ListsNoProgramsWhereNoListNamesOne()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        await geo.ApplyToRoutingListAsync(0, "Sites", ["proxy|geosite:youtube"]);
        var listed = 0;

        var search = await AppFinds.SearchListsAsync(_store, () =>
        {
            listed++;
            return Programs;
        }, null);

        Assert.Null(search);
        Assert.Equal(0, listed);
    }

    [Fact]
    public async Task TheSearchOfTheLists_NotesEveryNameNothingStandsFor()
    {
        var geo = new GeoConfigurator(_store, new NoFiles());
        await geo.ApplyToRoutingListAsync(0, "A", ["proxy|app:Firefox", "proxy|app:Discord"]);
        await geo.ApplyToRoutingListAsync(0, "B", ["direct|app:discord", "proxy|app:Zoom"]);
        var notes = new List<string>();

        var search = await AppFinds.SearchListsAsync(_store, () => Programs, notes.Add);

        Assert.Equal(["Firefox"], search!.Changed);
        Assert.Equal(["Discord", "Zoom"], search.Missing);
        Assert.Equal(2, notes.Count);
        Assert.Contains("app:Zoom", notes[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowOfAName_SaysWhatTheDeviceFound()
    {
        var row = new RoutingRuleItemViewModel("app:Telegram");
        var searching = row.Caption;

        row.Found = [@"path=C:\Users\anna\AppData\Roaming\Telegram Desktop\Telegram.exe"];
        var one = (row.Caption, row.CaptionWarn, row.CaptionTip);
        row.Found = ["pkg=a", "pkg=b"];
        var two = (row.Caption, row.CaptionTip);
        row.Found = [];
        var none = (row.Caption, row.CaptionWarn);

        var kind = Loc.Instance.Get("Main_KindApp");
        Assert.Equal(Loc.Instance.Get("Main_RuleAppSearching", kind), searching);
        Assert.Equal((Loc.Instance.Get("Main_RuleAppFound", kind, @"C:\Users\anna\AppData\Roaming\Telegram Desktop\Telegram.exe"), false, (string?)null), one);
        Assert.Equal((Loc.Instance.Get("Main_RuleAppFound", kind, "2"), "a, b"), two);
        Assert.Equal((Loc.Instance.Get("Main_RuleAppMissing", kind), true), none);
    }

    [Fact]
    public void TheRowOfAPath_KeepsItsCaption()
    {
        var row = new RoutingRuleItemViewModel(@"app:path=C:\Tools\tool.exe");

        Assert.Equal(string.Empty, row.AppName);
        Assert.Equal(RuleToken.Caption(@"app:path=C:\Tools\tool.exe"), row.Caption);
        Assert.False(row.CaptionWarn);
    }

    [Fact]
    public void AShortcut_NamesTheProgramItPointsAt()
    {
        Assert.Equal(@"C:\Apps\Tool.exe", ShellLink.Target(Shortcut(@"C:\Apps\Tool.exe")));
        Assert.Null(ShellLink.Target(Shortcut(@"\\server\share\tool.exe")));
        Assert.Null(ShellLink.Target(new byte[0x20]));
    }

    [Fact]
    public void ThePrograms_OfThisMachineAreListedWithoutFailing()
    {
        var programs = InstalledPrograms.List(null);

        Assert.All(programs, program => Assert.True(program.Value.StartsWith("path=", StringComparison.Ordinal) || program.Value.StartsWith("dir=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Docker\Docker\Docker Desktop Installer.exe", @"""C:\Program Files\Docker\Docker\Docker Desktop Installer.exe"" uninstall", true)]
    [InlineData(@"C:\Program Files\Tool\unins000.exe", null, true)]
    [InlineData(@"C:\Users\anna\AppData\Local\Discord\Update.exe", null, true)]
    [InlineData(@"C:\Program Files\Tool\setup.exe", null, true)]
    [InlineData(@"C:\Program Files\Tool\helper.exe", @"C:\Program Files\Tool\helper.exe /remove", true)]
    [InlineData(@"C:\Program Files\Docker\Docker\Docker Desktop.exe", @"""C:\Program Files\Docker\Docker\Docker Desktop Installer.exe"" uninstall", false)]
    [InlineData(@"C:\Program Files\Mozilla Firefox\firefox.exe", @"""C:\Program Files\Mozilla Firefox\uninstall\helper.exe""", false)]
    public void AnUninstallEntry_DoesNotTakeItsInstallerForTheProgram(string exe, string? uninstall, bool installs)
    {
        Assert.Equal(installs, InstalledPrograms.Installs(exe, uninstall));
    }

    // A shortcut whose link info names a local base path and no suffix.
    private static byte[] Shortcut(string target)
    {
        var path = Encoding.ASCII.GetBytes(target + "\0");
        const int infoHeader = 0x1C;
        const int volume = 4;
        var size = infoHeader + volume + path.Length + 1;
        var data = new byte[0x4C + size];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x4C);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x14), 0x2);
        var info = data.AsSpan(0x4C);
        BinaryPrimitives.WriteUInt32LittleEndian(info, (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(info[4..], infoHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(info[8..], 0x1);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0xC..], infoHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x10..], infoHeader + volume);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x18..], (uint)(infoHeader + volume + path.Length));
        path.CopyTo(info[(infoHeader + volume)..]);
        return data;
    }

    // No geo files.
    private sealed class NoFiles : IGeoFileStore
    {
        public byte[]? Read(string name) => null;

        public Stream? OpenRead(string name) => null;

        public Task WriteAsync(string name, byte[] data, CancellationToken ct = default) => Task.CompletedTask;
    }
}
