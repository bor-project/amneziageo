using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;
using AmneziaGeo.Routing;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Tests;

/// <summary>
/// A machine to try the distributor on: the two stores an agent keeps, the tunnels it holds up and the journal it
/// writes. Everything lives under one temporary folder, so a restart is the same stores read again.
/// </summary>
internal sealed class MachineHarness : IAsyncDisposable
{
    private readonly string _root;
    private SqliteStateStore _machine = null!;
    private UserStoreRegistry _registry = null!;

    private MachineHarness(string root)
    {
        _root = root;
    }

    /// <summary>
    /// The library and the settings the agent reads.
    /// </summary>
    public IStateStore Store { get; private set; } = null!;

    /// <summary>
    /// The tunnels the agent holds up.
    /// </summary>
    public AgentControl Control { get; private set; } = null!;

    /// <summary>
    /// The distributor under test.
    /// </summary>
    public RoutingDistributor Distributor { get; private set; } = null!;

    /// <summary>
    /// What the distributor wrote.
    /// </summary>
    public JournalLogger Journal { get; private set; } = null!;

    /// <summary>
    /// The library the tunnels belong to.
    /// </summary>
    public string UserRoot => Path.Combine(_root, "user");

    /// <summary>
    /// Opens an empty machine.
    /// </summary>
    public static async Task<MachineHarness> StartAsync()
    {
        var harness = new MachineHarness(Path.Combine(Path.GetTempPath(), $"ageo-machine-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(harness._root);
        await harness.OpenAsync();
        return harness;
    }

    /// <summary>
    /// Closes everything and opens it again over the same files: what the machine knows after a restart is what
    /// the stores hold, and nothing besides.
    /// </summary>
    public async Task RestartAsync()
    {
        Close();
        await OpenAsync();
    }

    /// <summary>
    /// Turns on working several servers at once.
    /// </summary>
    public Task ModeOnAsync()
    {
        return Store.SetSettingAsync(SettingKeys.MultiServer, "on");
    }

    /// <summary>
    /// Turns off working several servers at once.
    /// </summary>
    public Task ModeOffAsync()
    {
        return Store.SetSettingAsync(SettingKeys.MultiServer, "off");
    }

    /// <summary>
    /// Fills the library, priority top down.
    /// </summary>
    public async Task LibraryAsync(params string[] names)
    {
        foreach (var name in names)
        {
            await Store.SaveConfigAsync(name, "[Interface]");
        }

        await Store.SetConfigOrderAsync(names);
    }

    /// <summary>
    /// Saves the list the whole machine routes through.
    /// </summary>
    public async Task<long> ListAsync(params GeoRule[] rules)
    {
        var id = await Store.SaveRoutingListAsync(new RoutingList(0, "main", rules, [], [], [], [], [], [], []));
        await Store.SetSelectedRoutingListAsync(id);
        return id;
    }

    /// <summary>
    /// Switches cards off.
    /// </summary>
    public Task SwitchOffAsync(params string[] names)
    {
        return Store.SetSettingAsync(SettingKeys.FailoverSkipped, NameList.Join(names));
    }

    /// <summary>
    /// Holds tunnels up.
    /// </summary>
    public void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            Control.For(name, UserRoot).SetRunning(true);
        }
    }

    /// <summary>
    /// Takes a tunnel down.
    /// </summary>
    public void Drop(string name)
    {
        Control.For(name, UserRoot).SetRunning(false);
    }

    /// <summary>
    /// Dials a tunnel that the peer does not answer.
    /// </summary>
    public void Dial(string name, int times)
    {
        var tunnel = Control.For(name, UserRoot);
        for (var attempt = 0; attempt < times; attempt++)
        {
            tunnel.NextRetry();
        }
    }

    /// <summary>
    /// Answers a dial: the handshake stands and the count starts over.
    /// </summary>
    public void Answer(string name)
    {
        var tunnel = Control.For(name, UserRoot);
        tunnel.ClearRetry();
        tunnel.SetHandshakeAge(0);
    }

    /// <summary>
    /// Recounts every share.
    /// </summary>
    public Task<TunnelRole> DistributeAsync(string? raising = null)
    {
        return Distributor.DistributeAsync(UserRoot, raising);
    }

    /// <summary>
    /// The configurations the machine keeps up.
    /// </summary>
    public Task<IReadOnlyList<string>> RosterAsync()
    {
        return Distributor.RosterAsync(UserRoot);
    }

    /// <summary>
    /// How the list came out across the servers up.
    /// </summary>
    public Task<RoutingLayout> LayoutAsync()
    {
        return Distributor.LayoutAsync(UserRoot);
    }

    /// <summary>
    /// Settles the machine on the mode it was just put in.
    /// </summary>
    public Task<ModeSwitch> SwitchModeAsync()
    {
        return Distributor.SwitchModeAsync(UserRoot);
    }

    /// <summary>
    /// The configurations, priority top down.
    /// </summary>
    public Task<IReadOnlyList<string>> OrderAsync()
    {
        return new ConfigRepository(Store, new ServiceManager()).ListAsync();
    }

    /// <summary>
    /// The cards switched off.
    /// </summary>
    public async Task<IReadOnlyList<string>> SwitchedOffAsync()
    {
        return NameList.Split(await Store.GetSettingAsync(SettingKeys.FailoverSkipped));
    }

    /// <summary>
    /// The address ranges a tunnel was handed.
    /// </summary>
    public async Task<IReadOnlyList<string>> CarriedAsync(string server)
    {
        return (await Store.GetActiveRoutingListMaterializationAsync(server))?.Routes ?? [];
    }

    /// <summary>
    /// The address ranges a tunnel was told to block.
    /// </summary>
    public async Task<IReadOnlyList<string>> BlockedAsync(string server)
    {
        return (await Store.GetActiveRoutingListMaterializationAsync(server))?.BlockRoutes ?? [];
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Close();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private async Task OpenAsync()
    {
        _machine = new SqliteStateStore(Path.Combine(_root, "machine.db"));
        await _machine.InitializeAsync();
        _registry = new UserStoreRegistry();
        var factory = new ScopedStoreFactory(_machine, _registry);
        Store = factory.For(UserRoot);
        Control = new AgentControl();
        Journal = new JournalLogger();
        Distributor = new RoutingDistributor(Control, factory, new SettingsStore(Store), new ServiceManager(), new NoGeoFiles(), Journal);
    }

    private void Close()
    {
        _machine.ClearPool();
        foreach (var opened in _registry.OpenedStores().OfType<SqliteStateStore>())
        {
            opened.ClearPool();
        }
    }
}

/// <summary>
/// Keeps what the distributor wrote. It announces a new share to the tunnels themselves, which run in another
/// process and stand behind no test, so what they say about that arrives from other threads.
/// </summary>
internal sealed class JournalLogger : ILogger<RoutingDistributor>
{
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// The lines about the rules, level first.
    /// </summary>
    public IReadOnlyList<string> Verdicts
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines.Where(line => line.Contains("|rule ", StringComparison.Ordinal))];
            }
        }
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _lines.Add($"{logLevel}|{formatter(state, exception)}");
        }
    }
}

/// <summary>
/// No geo databases at hand: a rule naming an address range needs none.
/// </summary>
internal sealed class NoGeoFiles : IGeoFileStore
{
    /// <inheritdoc />
    public byte[]? Read(string name)
    {
        return null;
    }

    /// <inheritdoc />
    public Stream? OpenRead(string name)
    {
        return null;
    }

    /// <inheritdoc />
    public Task WriteAsync(string name, byte[] data, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
