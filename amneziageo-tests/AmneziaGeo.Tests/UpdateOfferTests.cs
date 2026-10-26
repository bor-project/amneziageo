using System.Collections.Concurrent;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// One download of the setup takes its address, its files and its digest from one offer of the agent, also when
/// another offer arrives while the download starts.
/// </summary>
public sealed class UpdateOfferTests
{
    private const string Version = "1.9.18.0";

    private static readonly StatusSnapshot _first = Offer("https://releases.test/latest/download/", 'a', 1000, "1.9.16.0");
    private static readonly StatusSnapshot _second = Offer("https://releases.test/download/v1.9.18.0/", 'b', 2000, "1.9.17.0");

    [Fact]
    public async Task ADownload_TakesItsAddressAndItsFilesFromOneOffer()
    {
        var (general, agent, fetched) = Window();
        general.Apply(_first);
        agent.WhileDownloadStarts = () => general.Apply(_second);

        await general.RunBackgroundDownloadAsync();

        var offer = Assert.Single(fetched);
        Assert.Equal(_second.UpdateSetupUrl, offer.Url);
        Assert.Same(_second.UpdateSetupAsset, offer.Asset);
        Assert.Equal(_second.UpdateSetupSha256, offer.Sha256);
        Assert.Equal(["downloading", "downloaded"], agent.Phases);
    }

    [Fact]
    public async Task AnOfferTakenBackWhileTheDownloadStarts_IsNotFetched()
    {
        var (general, agent, fetched) = Window();
        general.Apply(_first);
        agent.WhileDownloadStarts = () => general.Apply(_first with { UpdateAvailable = false });

        await general.RunBackgroundDownloadAsync();

        Assert.Empty(fetched);
        Assert.False(general.UpdateDownloading);
        Assert.False(general.UpdateDownloaded);
        Assert.Equal(["downloading", "idle"], agent.Phases);
    }

    [Fact]
    public async Task AnOfferThatStays_IsFetchedAsItStands()
    {
        var (general, agent, fetched) = Window();
        general.Apply(_first);

        await general.RunBackgroundDownloadAsync();

        var offer = Assert.Single(fetched);
        Assert.Equal(_first.UpdateSetupUrl, offer.Url);
        Assert.Same(_first.UpdateSetupAsset, offer.Asset);
        Assert.Equal(_first.UpdateSetupSha256, offer.Sha256);
        Assert.Equal(Version, offer.Version);
        Assert.Equal([Version, Version], agent.Versions);
    }

    [Fact]
    public void TheProgressOfADownload_WaitsForTheAgentToTakeTheLastReport()
    {
        var (general, agent, _) = Window();
        general.Apply(_first);
        general.Fetch = Reporting;
        agent.Busy = true;

        Pump.Run(general.RunBackgroundDownloadAsync);

        Assert.Equal(["downloading", "downloading", "downloaded"], agent.Phases);
        Assert.Equal(["0", "1", "100"], agent.Percents);
    }

    [Fact]
    public void TheProgressOfADownload_ReachesTheAgentOncePerPercent()
    {
        var (general, agent, _) = Window();
        general.Apply(_first);
        general.Fetch = Reporting;

        Pump.Run(general.RunBackgroundDownloadAsync);

        Assert.Equal([.. Enumerable.Range(0, 100).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)), "100"], agent.Percents);
        Assert.Equal("downloaded", agent.Phases[^1]);
    }

    [Fact]
    public void TheOfferOfASnapshot_IsOneValue()
    {
        var offer = UpdateOffer.Of(_second);

        Assert.True(offer.Downloadable);
        Assert.Equal(new UpdateOffer(true, Version, _second.UpdateSetupUrl, _second.UpdateSetupSha256, _second.UpdateSetupAsset), offer);
        Assert.False(UpdateOffer.None.Downloadable);
        Assert.False(UpdateOffer.Of(_second with { UpdateSetupUrl = string.Empty }).Downloadable);
        Assert.False(UpdateOffer.Of(_second with { UpdateAvailable = false }).Downloadable);
    }

    // A window whose downloads are only recorded; its preferences have shown the version already, so nothing is saved.
    private static (GeneralViewModel General, Agent Agent, List<UpdateOffer> Fetched) Window()
    {
        var agent = new Agent();
        var general = new MainWindowViewModel(agent, new UiPreferences { ShownUpdateVersion = Version }).General;
        var fetched = new List<UpdateOffer>();
        general.Fetch = (offer, _, _, _) =>
        {
            fetched.Add(offer);
            return Task.FromResult("/nowhere/" + offer.Version);
        };
        return (general, agent, fetched);
    }

    // A download off the window thread that reports each percent many times over, as the reads of the network do.
    private static Task<string> Reporting(UpdateOffer offer, IProgress<int> progress, Func<string, Task> note, CancellationToken ct) =>
        Task.Run(() =>
        {
            for (var percent = 1; percent <= 100; percent++)
            {
                for (var read = 0; read < 30; read++)
                {
                    progress.Report(percent);
                }
            }

            return "/nowhere/" + offer.Version;
        });

    // What the agent says of one release reached at one address: the setup, its digest and its patch.
    private static StatusSnapshot Offer(string folder, char digest, long size, string from)
    {
        var setup = $"AmneziaGeo-{Version}-win-x64-fdd.exe";
        var patch = new UpdatePatchFile($"AmneziaGeo-{Version}-win-x64-fdd.patch", size, new string(digest, 64), from);
        return new StatusSnapshot("1.9.15.0", null, [])
        {
            UpdateAvailable = true,
            UpdateVersion = Version,
            UpdateSetupUrl = folder + setup,
            UpdateSetupSha256 = new string(digest, 64),
            UpdateSetupAsset = new UpdateAsset(setup, "windows", "x64", "fdd", new string(digest, 64), size * 40, patch),
        };
    }

    // Records the phases the window reports of its download and lets a test act while the first one is on its way.
    private sealed class Agent : IAgentConnection
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

        public List<string> Phases { get; } = [];

        public List<string> Versions { get; } = [];

        public List<string> Percents { get; } = [];

        public Action? WhileDownloadStarts { get; set; }

        // Leaves the reports of the download progress unanswered.
        public bool Busy { get; set; }

        public void Start()
        {
        }

        public Task<IpcAck> SendCommandAsync(IpcCommand command)
        {
            if (command.Op == IpcContract.OpReportUpdateDownload)
            {
                Phases.Add(command.Args[0]);
                Versions.Add(command.Args[3]);
                Percents.Add(command.Args[1]);
                if (WhileDownloadStarts is { } act)
                {
                    WhileDownloadStarts = null;
                    act();
                }

                if (Busy && command.Args[0] == "downloading" && command.Args[1] != "0")
                {
                    return new TaskCompletionSource<IpcAck>().Task;
                }
            }

            return Task.FromResult(new IpcAck(true, string.Empty));
        }

        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);

        public void Dispose()
        {
        }
    }

    // Runs the posted work on one thread, as the dispatcher of the window does.
    private sealed class Pump : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public static void Run(Func<Task> work)
        {
            var pump = new Pump();
            var previous = Current;
            SetSynchronizationContext(pump);
            try
            {
                var task = work();
                task.ContinueWith(_ => pump._queue.CompleteAdding(), TaskScheduler.Default);
                foreach (var (callback, state) in pump._queue.GetConsumingEnumerable())
                {
                    callback(state);
                }

                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
