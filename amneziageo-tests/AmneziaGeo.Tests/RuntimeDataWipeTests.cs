using AmneziaGeo.Dal;
using AmneziaGeo.Windows.App;

using Microsoft.Data.Sqlite;

using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// What the setup wipes. The settings reset clears the user library, the agent settings kept for the machine and the
/// retired copies of the databases, and keeps the geo cache, the logs and the tunnel state; the removal with the
/// configuration deleted leaves neither tree.
/// </summary>
public sealed class RuntimeDataWipeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ageo-wipe-{Guid.NewGuid():N}");

    private string User => Path.Combine(_root, "user");

    private string Machine => Path.Combine(_root, "machine");

    private string MachineDb => Path.Combine(Machine, "machine.db");

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Reset_ClearsTheLibraryTheMachineSettingsAndTheRetiredCopies()
    {
        await SeedAsync();

        InstallerMaintenance.WipeRuntimeData(User, Machine);

        Assert.False(Directory.Exists(User));
        Assert.Null(Setting("log-level"));
        Assert.Null(Setting("geo-last-refresh"));
        Assert.Equal("kept", Setting("not-a-machine-key"));
        Assert.False(File.Exists(Path.Combine(Machine, "state.db.legacy")));
        Assert.False(File.Exists(Path.Combine(Machine, "machine.db.pre-profiles.bak")));
        Assert.False(File.Exists(Path.Combine(Machine, "machine.db.corrupt-20260801-134031")));
    }

    [Fact]
    public async Task Reset_KeepsTheGeoCacheTheLogsAndTheTunnelState()
    {
        await SeedAsync();

        InstallerMaintenance.WipeRuntimeData(User, Machine);

        Assert.True(File.Exists(MachineDb));
        Assert.True(File.Exists(Path.Combine(Machine, "geo", "geoip.dat")));
        Assert.True(File.Exists(Path.Combine(Machine, "logs", "log.db")));
        Assert.True(File.Exists(Path.Combine(Machine, "dns-state-t.txt")));
        Assert.True(File.Exists(Path.Combine(Machine, "data-root")));
    }

    [Fact]
    public void Reset_WithoutAMachineDatabase_ClearsTheLibrary()
    {
        Directory.CreateDirectory(User);
        File.WriteAllText(Path.Combine(User, "state.db"), "library");

        InstallerMaintenance.WipeRuntimeData(User, Machine);

        Assert.False(Directory.Exists(User));
    }

    [Fact]
    public async Task Removal_LeavesNeitherTree()
    {
        await SeedAsync();

        InstallerMaintenance.WipeAllRuntimeData(User, Machine);

        Assert.False(Directory.Exists(User));
        Assert.False(Directory.Exists(Machine));
    }

    // Lays a user library and a machine root the way an installed agent leaves them.
    private async Task SeedAsync()
    {
        Directory.CreateDirectory(Path.Combine(User, "config"));
        File.WriteAllText(Path.Combine(User, "state.db"), "library");
        File.WriteAllText(Path.Combine(User, "config", "t.conf"), "config");

        Directory.CreateDirectory(Path.Combine(Machine, "geo"));
        Directory.CreateDirectory(Path.Combine(Machine, "logs"));
        var store = new SqliteStateStore(MachineDb);
        await store.InitializeAsync();
        await store.SetSettingAsync("log-level", "info");
        await store.SetSettingAsync("geo-last-refresh", "1");
        await store.SetSettingAsync("not-a-machine-key", "kept");
        store.ClearPool();

        foreach (var name in new[] { "state.db.legacy", "machine.db.pre-profiles.bak", "machine.db.corrupt-20260801-134031", "dns-state-t.txt", "data-root" })
        {
            File.WriteAllText(Path.Combine(Machine, name), name);
        }

        File.WriteAllText(Path.Combine(Machine, "geo", "geoip.dat"), "geo");
        File.WriteAllText(Path.Combine(Machine, "logs", "log.db"), "log");
    }

    // Reads one settings row of the machine database.
    private string? Setting(string key)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = MachineDb, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }
}
