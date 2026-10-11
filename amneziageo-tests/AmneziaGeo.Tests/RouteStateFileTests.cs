using System.Diagnostics;

using AmneziaGeo.Windows.App;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The file a tunnel keeps its added routes in is read and removed by the tunnel and by the agent at once: a file
/// the other one holds or is already removing is waited for and never fails the caller.
/// </summary>
public sealed class RouteStateFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ag-route-state-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// ctor
    /// </summary>
    public RouteStateFileTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    private string File(string name = "lan-state-a.txt") => Path.Combine(_folder, name);

    [Fact]
    public void Read_KeepsEachEntryOnceInTheOrderWritten()
    {
        var path = File();
        System.IO.File.WriteAllLines(path, ["10.0.0.0/8", " 192.168.0.0/16 ", "", "10.0.0.0/8", "172.16.0.0/12"]);

        Assert.Equal(["10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12"], RouteStateFile.Read(path));
    }

    [Fact]
    public void Read_AFileThatIsNotThere_GivesNothing()
    {
        Assert.Empty(RouteStateFile.Read(File()));
    }

    [Fact]
    public void Read_AFileTheOtherProcessLetsGoOf_IsReadOnALaterTry()
    {
        var reads = 0;
        var pauses = 0;

        var entries = RouteStateFile.Read(
            "held",
            _ => true,
            _ => ++reads < 3 ? throw new UnauthorizedAccessException("Access to the path 'held' is denied.") : ["10.0.0.0/8"],
            () => pauses++);

        Assert.Equal(["10.0.0.0/8"], entries);
        Assert.Equal(3, reads);
        Assert.Equal(2, pauses);
    }

    [Fact]
    public void Read_AFileThatStaysHeld_GivesNothingAndFailsNobody()
    {
        var reads = 0;

        var entries = RouteStateFile.Read("held", _ => true, _ => { reads++; throw new IOException("in use"); }, () => { });

        Assert.Empty(entries);
        Assert.Equal(RouteStateFile.Tries, reads);
    }

    [Fact]
    public void Read_AFileThatGoesWhileItIsWaitedFor_GivesNothing()
    {
        var asked = 0;
        var reads = 0;

        var entries = RouteStateFile.Read("going", _ => ++asked < 3, _ => { reads++; throw new UnauthorizedAccessException(); }, () => { });

        Assert.Empty(entries);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Delete_AFileThatIsThere_RemovesIt()
    {
        var path = File();
        System.IO.File.WriteAllText(path, "10.0.0.0/8");

        Assert.True(RouteStateFile.Delete(path));
        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public void Delete_AFileThatIsNotThere_IsDone()
    {
        Assert.True(RouteStateFile.Delete(File()));
    }

    [Fact]
    public void Delete_AFileAlreadyOnItsWayOut_IsNotAnError()
    {
        // What Windows answers for a file the other process removed while a third one still reads it: the file
        // is seen, removing it is denied, and it goes once the reader lets go.
        var asked = 0;
        var deletes = 0;

        var gone = RouteStateFile.Delete(
            "pending",
            _ => ++asked < 4,
            _ => { deletes++; throw new UnauthorizedAccessException("Access to the path 'pending' is denied."); },
            () => { });

        Assert.True(gone);
        Assert.Equal(3, deletes);
    }

    [Fact]
    public void Delete_AFileHeldForAWhile_GoesOnALaterTry()
    {
        var deletes = 0;
        var pauses = 0;

        var gone = RouteStateFile.Delete("held", _ => true, _ => { if (++deletes < 3) { throw new IOException("in use"); } }, () => pauses++);

        Assert.True(gone);
        Assert.Equal(3, deletes);
        Assert.Equal(2, pauses);
    }

    [Fact]
    public void Delete_AFileThatStaysHeld_SaysSoAndFailsNobody()
    {
        var deletes = 0;

        var gone = RouteStateFile.Delete("held", _ => true, _ => { deletes++; throw new UnauthorizedAccessException(); }, () => { });

        Assert.False(gone);
        Assert.Equal(RouteStateFile.Tries, deletes);
    }

    [Fact]
    public void Delete_AnErrorThatIsNotAboutAHeldFile_ComesOut()
    {
        Assert.Throws<NotSupportedException>(() => RouteStateFile.Delete("odd", _ => true, _ => throw new NotSupportedException(), () => { }));
    }

    [Fact]
    public void Add_PutsInOnlyWhatTheFileDoesNotHold()
    {
        var path = File();
        System.IO.File.WriteAllLines(path, ["10.0.0.0/8", "192.168.0.0/16"]);

        RouteStateFile.Add(path, ["192.168.0.0/16", "172.16.0.0/12", "172.16.0.0/12"], 2000, _ => { });

        Assert.Equal(["10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12"], System.IO.File.ReadAllLines(path));
    }

    [Fact]
    public void Add_NothingNew_LeavesTheFileAlone()
    {
        var path = File();
        System.IO.File.WriteAllLines(path, ["10.0.0.0/8"]);
        var written = System.IO.File.GetLastWriteTimeUtc(path).AddHours(-1);
        System.IO.File.SetLastWriteTimeUtc(path, written);

        RouteStateFile.Add(path, ["10.0.0.0/8"], 2000, _ => { });
        RouteStateFile.Add(path, [], 2000, _ => { });

        Assert.Equal(written, System.IO.File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Add_OfTensOfThousandsOfEntries_TakesNoTimeToSpeakOf()
    {
        var path = File();
        var first = Enumerable.Range(0, 100_000).Select(Address).ToList();
        var second = Enumerable.Range(50_000, 100_000).Select(Address).ToList();
        System.IO.File.WriteAllLines(path, first);
        var clock = Stopwatch.StartNew();

        RouteStateFile.Add(path, second, 60_000, _ => { });
        var held = RouteStateFile.Read(path);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(150_000, held.Count);
        Assert.Equal(Address(0), held[0]);
        Assert.Equal(Address(149_999), held[^1]);
    }

    [Fact]
    public void Add_ThatOutlastsItsBudget_IsWaitedForByTheReadAfterIt()
    {
        var path = File();
        using var release = new ManualResetEventSlim();

        RouteStateFile.Add(path, ["10.0.0.0/8"], 20, _ => { }, (file, entries) =>
        {
            release.Wait();
            System.IO.File.WriteAllLines(file, entries);
        });
        var read = Task.Run(() => RouteStateFile.Read(path));

        Assert.False(read.Wait(300));
        release.Set();
        Assert.Equal(["10.0.0.0/8"], read.GetAwaiter().GetResult());
    }

    [Fact]
    public void Add_ThatOutlastsItsBudget_IsNotLeftToWriteTheFileBackAfterItWasRemoved()
    {
        var path = File();
        using var release = new ManualResetEventSlim();

        RouteStateFile.Add(path, ["10.0.0.0/8"], 20, _ => { }, (file, entries) =>
        {
            release.Wait();
            System.IO.File.WriteAllLines(file, entries);
        });
        var delete = Task.Run(() => RouteStateFile.Delete(path));

        Assert.False(delete.Wait(300));
        release.Set();
        Assert.True(delete.GetAwaiter().GetResult());
        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public void Add_ThatFailsAfterItsBudget_TellsWhoeverAskedToBeTold()
    {
        var path = File();
        using var release = new ManualResetEventSlim();
        using var told = new ManualResetEventSlim();
        var said = string.Empty;

        RouteStateFile.Add(path, ["10.0.0.0/8"], 20, late => { said = late.Message; told.Set(); }, (_, _) =>
        {
            release.Wait();
            throw new IOException("the disk is full");
        });
        release.Set();

        Assert.True(told.Wait(5000));
        Assert.Equal("the disk is full", said);
        Assert.Empty(RouteStateFile.Read(path));
    }

    [Fact]
    public void Update_AddsAnEntryOnceAndTakesItBack()
    {
        var path = File("route-state-a.txt");

        RouteStateFile.Update(path, "203.0.113.7", add: true);
        RouteStateFile.Update(path, "203.0.113.7", add: true);
        RouteStateFile.Update(path, "203.0.113.8", add: true);
        Assert.Equal(["203.0.113.7", "203.0.113.8"], System.IO.File.ReadAllLines(path));

        RouteStateFile.Update(path, "203.0.113.7", add: false);
        Assert.Equal(["203.0.113.8"], System.IO.File.ReadAllLines(path));
    }

    [Fact]
    public void Update_TakingTheLastEntryBack_RemovesTheFile()
    {
        var path = File("route-state-a.txt");
        RouteStateFile.Update(path, "203.0.113.7", add: true);

        RouteStateFile.Update(path, "203.0.113.7", add: false);

        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public void Update_InAFolderThatIsNotThereYet_MakesIt()
    {
        var path = Path.Combine(_folder, "later", "route-state-a.txt");

        RouteStateFile.Update(path, "203.0.113.7", add: true);

        Assert.Equal(["203.0.113.7"], System.IO.File.ReadAllLines(path));
    }

    [Fact]
    public void Through_WritesTheRouteWithTheAdapterAndTheHopItWasPutThrough()
    {
        var record = RouteStateFile.Through("203.0.113.64/28", 19, "192.168.1.1");

        Assert.Equal("203.0.113.64/28 19 192.168.1.1", record);
        Assert.Equal("203.0.113.64/28", RouteStateFile.Entry(record));
        Assert.Equal((19u, "192.168.1.1"), RouteStateFile.Hop(record));
    }

    [Fact]
    public void Hop_OfAnIPv6Record_KeepsTheScopeOfTheHop()
    {
        var record = RouteStateFile.Through("fd00:1::/64", 12, "fe80::1%12");

        Assert.Equal("fd00:1::/64", RouteStateFile.Entry(record));
        Assert.Equal((12u, "fe80::1%12"), RouteStateFile.Hop(record));
    }

    [Fact]
    public void Hop_OfARecordWrittenWithoutIt_IsNotThere()
    {
        Assert.Null(RouteStateFile.Hop("203.0.113.64/28"));
        Assert.Null(RouteStateFile.Hop("203.0.113.7"));
        Assert.Equal("203.0.113.7", RouteStateFile.Entry("203.0.113.7"));
    }

    [Fact]
    public void Hop_OfARecordThatDoesNotRead_IsNotThere()
    {
        Assert.Null(RouteStateFile.Hop("203.0.113.7 nineteen 192.168.1.1"));
        Assert.Null(RouteStateFile.Hop("203.0.113.7 19"));
    }

    [Fact]
    public void HeldElsewhere_HoldsTheRoutesTheOtherFilesRecord()
    {
        System.IO.File.WriteAllLines(File("lan-state-a.txt"), [Route("10.0.0.0/8"), Route("198.18.0.1/32")]);
        System.IO.File.WriteAllLines(File("lan-state-b.txt"), [Route("198.18.0.1/32"), Route("198.18.0.3/32")]);
        System.IO.File.WriteAllLines(File("lan-state-c.txt"), [Route("172.16.0.0/12")]);

        var held = RouteStateFile.HeldElsewhere(File("lan-state-a.txt"), Directory.GetFiles(_folder));

        Assert.True(held.Holds(Route("198.18.0.1/32")));
        Assert.True(held.Holds(Route("198.18.0.3/32")));
        Assert.True(held.Holds(Route("172.16.0.0/12")));
        Assert.False(held.Holds(Route("10.0.0.0/8")));
    }

    [Fact]
    public void HeldElsewhere_TheSameDestinationThroughAnotherHop_IsNotHeld()
    {
        System.IO.File.WriteAllLines(File("lan-state-b.txt"), [Route("198.18.0.1/32")]);

        var held = RouteStateFile.HeldElsewhere(File("lan-state-a.txt"), Directory.GetFiles(_folder));

        Assert.False(held.Holds(RouteStateFile.Through("198.18.0.1/32", 19, "192.168.1.254")));
        Assert.False(held.Holds(RouteStateFile.Through("198.18.0.1/32", 21, "192.168.1.1")));
    }

    [Fact]
    public void HeldElsewhere_ARecordWrittenWithoutItsHop_HoldsTheDestinationWhateverTheHop()
    {
        System.IO.File.WriteAllLines(File("lan-state-b.txt"), ["198.18.0.1/32"]);

        var held = RouteStateFile.HeldElsewhere(File("lan-state-a.txt"), Directory.GetFiles(_folder));

        Assert.True(held.Holds(Route("198.18.0.1/32")));
        Assert.True(held.Holds("198.18.0.1/32"));
        Assert.False(held.Holds(Route("198.18.0.3/32")));
    }

    [Fact]
    public void HeldElsewhere_ForARecordWrittenWithoutItsHop_AnyRecordOfTheDestinationHoldsIt()
    {
        System.IO.File.WriteAllLines(File("lan-state-b.txt"), [Route("198.18.0.1/32")]);

        var held = RouteStateFile.HeldElsewhere(File("lan-state-a.txt"), Directory.GetFiles(_folder));

        Assert.True(held.Holds("198.18.0.1/32"));
        Assert.False(held.Holds("198.18.0.3/32"));
    }

    [Fact]
    public void HeldElsewhere_LeavesTheFileItselfOut_HoweverItsPathIsSpelt()
    {
        System.IO.File.WriteAllLines(File("lan-state-a.txt"), [Route("10.0.0.0/8")]);

        var held = RouteStateFile.HeldElsewhere(File("lan-state-a.txt"), [File("lan-state-a.txt"), Path.Combine(_folder, ".", "lan-state-a.txt")]);

        Assert.False(held.Holds(Route("10.0.0.0/8")));
    }

    [Fact]
    public void HeldElsewhere_AFileThatIsGone_HoldsNothing()
    {
        System.IO.File.WriteAllLines(File("lan-state-a.txt"), [Route("10.0.0.0/8")]);

        var held = RouteStateFile.HeldElsewhere(File("lan-state-a.txt"), [File("lan-state-a.txt"), File("lan-state-gone.txt")]);

        Assert.False(held.Holds(Route("10.0.0.0/8")));
    }

    [Fact]
    public void HeldElsewhere_ForAFileNotWrittenYet_HoldsWhatTheOthersRecord()
    {
        System.IO.File.WriteAllLines(File("lan-state-b.txt"), [Route("198.18.0.1/32")]);

        Assert.True(RouteStateFile.HeldElsewhere(File("lan-state-a.txt"), Directory.GetFiles(_folder)).Holds(Route("198.18.0.1/32")));
    }

    [Fact]
    public void Recorded_TakesInTheFileOfTheTunnelItself()
    {
        System.IO.File.WriteAllLines(File("lan-state-a.txt"), [Route("10.0.0.0/8")]);

        var recorded = RouteStateFile.Recorded(Directory.GetFiles(_folder));

        Assert.True(recorded.Holds(Route("10.0.0.0/8")));
        Assert.False(recorded.Holds(Route("172.16.0.0/12")));
    }

    private static string Route(string entry) => RouteStateFile.Through(entry, 19, "192.168.1.1");

    private static string Address(int n) => $"198.{18 + n / 65536}.{n / 256 % 256}.{n % 256}/32";
}
