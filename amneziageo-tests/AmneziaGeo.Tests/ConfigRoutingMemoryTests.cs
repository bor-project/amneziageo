using System.Text.Json;
using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class ConfigRoutingMemoryTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ageo-routing-{Guid.NewGuid():N}.db");
    private SqliteStateStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _store = new SqliteStateStore(_path);
        await _store.InitializeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _store.ClearPool();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(path);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SwitchingConfigurationsRestoresTheirOwnSelections()
    {
        await _store.SaveConfigAsync("A", "a").ConfigureAwait(false);
        await _store.SaveConfigAsync("B", "b").ConfigureAwait(false);
        await _store.SetSettingAsync(StateKeys.SelectedTarget, "A").ConfigureAwait(false);
        await ConfigRouting.SelectCurrentListAsync(_store, 11).ConfigureAwait(false);
        await _store.SetSettingAsync(StateKeys.SelectedTarget, "B").ConfigureAwait(false);
        await ConfigRouting.SelectCurrentListAsync(_store, 22).ConfigureAwait(false);

        await _store.SetSettingAsync(StateKeys.SelectedTarget, "A").ConfigureAwait(false);
        Assert.Equal(11, await ConfigRouting.CurrentListAsync(_store).ConfigureAwait(false));
        await _store.SetSettingAsync(StateKeys.SelectedTarget, "B").ConfigureAwait(false);
        Assert.Equal(22, await ConfigRouting.CurrentListAsync(_store).ConfigureAwait(false));
    }

    [Fact]
    public async Task LegacyMigrationCopiesTheOldSelectionBeforeOneConfigurationChanges()
    {
        await _store.SaveConfigAsync("A", "a").ConfigureAwait(false);
        await _store.SaveConfigAsync("B", "b").ConfigureAwait(false);
        await SeedLegacyAsync("17").ConfigureAwait(false);
        await _store.InitializeAsync().ConfigureAwait(false);

        await ConfigRouting.SelectListAsync(_store, "A", 23).ConfigureAwait(false);

        Assert.Equal(17, await ConfigRouting.SelectedListAsync(_store, "B").ConfigureAwait(false));
        Assert.Equal("17", await _store.GetSettingAsync(ConfigRouting.SelectionKey("B")).ConfigureAwait(false));
        Assert.Equal(23, await ConfigRouting.SelectedListAsync(_store, "A").ConfigureAwait(false));
    }

    [Fact]
    public async Task ANewConfigurationInheritsTheFixedDefault()
    {
        await _store.SetSelectedRoutingListAsync(7).ConfigureAwait(false);
        await ConfigRouting.SelectListAsync(_store, "A", 9).ConfigureAwait(false);

        Assert.Equal(7, await ConfigRouting.SelectedListAsync(_store, "not-yet-opened").ConfigureAwait(false));
        Assert.Equal(9, await _store.GetSelectedRoutingListAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task ExplicitOffSurvivesRestartAndAnotherConfigurationsSelection()
    {
        await _store.SetSelectedRoutingListAsync(7).ConfigureAwait(false);
        await _store.SaveConfigAsync("A", "a").ConfigureAwait(false);
        await ConfigRouting.SelectListAsync(_store, "A", null).ConfigureAwait(false);
        await ConfigRouting.SelectListAsync(_store, "B", 9).ConfigureAwait(false);
        await _store.InitializeAsync().ConfigureAwait(false);

        Assert.Null(await ConfigRouting.SelectedListAsync(_store, "A").ConfigureAwait(false));
        Assert.Equal(9, await ConfigRouting.SelectedListAsync(_store, "B").ConfigureAwait(false));
    }

    [Fact]
    public async Task RenameMovesTheChoiceAndReusingTheOldNameInheritsTheDefault()
    {
        await _store.SetSelectedRoutingListAsync(7).ConfigureAwait(false);
        await _store.SaveConfigAsync("old", "a").ConfigureAwait(false);
        await ConfigRouting.SelectListAsync(_store, "old", 11).ConfigureAwait(false);
        await _store.RenameConfigAsync("old", "new").ConfigureAwait(false);
        await ConfigRename.CarryAsync(_store, "old", "new").ConfigureAwait(false);
        await ConfigRename.CarryAsync(_store, "old", "new").ConfigureAwait(false);

        Assert.Equal(11, await ConfigRouting.SelectedListAsync(_store, "new").ConfigureAwait(false));
        Assert.Equal(7, await ConfigRouting.SelectedListAsync(_store, "old").ConfigureAwait(false));
    }

    [Fact]
    public async Task RemovalDoesNotLeaveAChoiceForTheReimportedConfiguration()
    {
        await _store.SetSelectedRoutingListAsync(7).ConfigureAwait(false);
        await _store.SaveConfigAsync("A", "a").ConfigureAwait(false);
        await ConfigRouting.SelectListAsync(_store, "A", 11).ConfigureAwait(false);
        await _store.RemoveConfigAsync("A").ConfigureAwait(false);
        await ConfigForget.CarryAsync(_store, "A").ConfigureAwait(false);
        await _store.SaveConfigAsync("A", "replacement").ConfigureAwait(false);

        Assert.Equal(7, await ConfigRouting.SelectedListAsync(_store, "A").ConfigureAwait(false));
    }

    [Fact]
    public async Task RemovingAListClearsEveryReferenceAndKeepsOtherChoices()
    {
        var first = await _store.SaveRoutingListAsync(new RoutingList(0, "first", [], [], [], [], [], [], [], [], [])).ConfigureAwait(false);
        var second = await _store.SaveRoutingListAsync(new RoutingList(0, "second", [], [], [], [], [], [], [], [], [])).ConfigureAwait(false);
        await ConfigRouting.SelectListAsync(_store, "A", first).ConfigureAwait(false);
        await ConfigRouting.SelectListAsync(_store, "B", first).ConfigureAwait(false);
        await ConfigRouting.SelectListAsync(_store, "C", second).ConfigureAwait(false);

        await _store.RemoveRoutingListAsync(first).ConfigureAwait(false);

        Assert.Null(await ConfigRouting.SelectedListAsync(_store, "A").ConfigureAwait(false));
        Assert.Null(await ConfigRouting.SelectedListAsync(_store, "B").ConfigureAwait(false));
        Assert.Equal(second, await ConfigRouting.SelectedListAsync(_store, "C").ConfigureAwait(false));
    }

    [Fact]
    public async Task DisablingRoutingKeepsTheRememberedSelection()
    {
        await ConfigRouting.SelectListAsync(_store, "A", 17).ConfigureAwait(false);
        await _store.SetConfigTransportAsync(new ConfigTransport("A", false, UseRouting: false)).ConfigureAwait(false);
        Assert.False(await ConfigRouting.AllowedAsync(_store, "A").ConfigureAwait(false));
        Assert.Equal(17, await ConfigRouting.SelectedListAsync(_store, "A").ConfigureAwait(false));
    }

    [Fact]
    public async Task NamedReadsDoNotFollowTheUiSelectionOfAnotherTunnel()
    {
        await ConfigRouting.SelectListAsync(_store, "A", 11).ConfigureAwait(false);
        await ConfigRouting.SelectListAsync(_store, "B", 22).ConfigureAwait(false);
        await _store.SetSettingAsync(StateKeys.SelectedTarget, "B").ConfigureAwait(false);

        Assert.Equal(11, await ConfigRouting.SelectedListAsync(_store, "A").ConfigureAwait(false));
        Assert.Equal(22, await ConfigRouting.CurrentListAsync(_store).ConfigureAwait(false));
    }

    [Fact]
    public void AndroidMemoryRoundTripKeepsOffAndTheInheritedDefault()
    {
        var choices = new ConfigRoutingMemory { Default = 7 };
        choices.Seed(["A", "B"]);
        choices.Remember("A", null);
        choices.Remember("B", 11);

        var restored = JsonSerializer.Deserialize<ConfigRoutingMemory>(JsonSerializer.Serialize(choices))!;

        Assert.Null(restored.For("A"));
        Assert.Equal(11, restored.For("B"));
        Assert.Equal(7, restored.For("new"));
    }

    [Fact]
    public void AndroidRenameAndListRemovalPreserveTheOtherConfiguration()
    {
        var choices = new ConfigRoutingMemory { Default = 7 };
        choices.Seed(["A", "B"]);
        choices.Remember("A", 11);
        choices.Rename("A", "renamed");
        choices.RemoveList(11);

        Assert.Null(choices.For("renamed"));
        Assert.Equal(7, choices.For("B"));
        Assert.Equal(7, choices.For("A"));
    }

    [Fact]
    public void SelectingAnotherConfigurationKeepsTheConnectedRoutingList()
    {
        var choices = new ConfigRoutingMemory { Choices = new() { ["A"] = 11, ["B"] = 22 } };
        Assert.Equal(11, choices.ForConnection("B", "A", true, new Dictionary<string, ConfigTransport>()));
        Assert.Equal(22, choices.For("B"));
        Assert.Equal("A", ConfigRoutingMemory.ConnectionTarget("B", "A", true));
    }

    [Fact]
    public void AnIdleConfigurationUsesItsOwnRoutingList()
    {
        var choices = new ConfigRoutingMemory { Choices = new() { ["A"] = 11, ["B"] = 22 } };
        Assert.Equal(22, choices.ForConnection("B", "A", false, new Dictionary<string, ConfigTransport>()));
    }

    [Fact]
    public void DisablingRoutingOnTheSelectedConfigurationDoesNotDisableTheConnectedOne()
    {
        var choices = new ConfigRoutingMemory { Choices = new() { ["A"] = 11, ["B"] = 22 } };
        var transports = new Dictionary<string, ConfigTransport> { ["B"] = new("B", false, UseRouting: false) };
        Assert.Equal(11, choices.ForConnection("B", "A", true, transports));
        Assert.Null(choices.ForConnection("B", "A", false, transports));
    }

    [Fact]
    public void TheConnectedConfigurationsRoutingSwitchIsAppliedWithoutLosingItsChoice()
    {
        var choices = new ConfigRoutingMemory { Choices = new() { ["A"] = 11, ["B"] = 22 } };
        var transports = new Dictionary<string, ConfigTransport> { ["A"] = new("A", false, UseRouting: false) };
        Assert.Null(choices.ForConnection("B", "A", true, transports));
        Assert.Equal(11, choices.For("A"));
        transports["A"] = transports["A"] with { UseRouting = true };
        Assert.Equal(11, choices.ForConnection("B", "A", true, transports));
    }

    [Fact]
    public void AConnectionWithoutABoundTargetUsesTheSelectedConfiguration()
    {
        var choices = new ConfigRoutingMemory { Choices = new() { ["A"] = 11 } };
        Assert.Equal(11, choices.ForConnection("A", null, true, new Dictionary<string, ConfigTransport>()));
    }

    [Fact]
    public void ANewBoundConfigurationUsesTheInheritedDefault()
    {
        var choices = new ConfigRoutingMemory { Default = 7, Choices = new() { ["B"] = 22 } };
        Assert.Equal(7, choices.ForConnection("B", "new", true, new Dictionary<string, ConfigTransport>()));
    }

    private async Task SeedLegacyAsync(string list)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key = $default OR substr(key, 1, length($prefix)) = $prefix; INSERT OR REPLACE INTO settings (key, value, updated_at) VALUES ($key, $list, '2026-11-05');";
        command.Parameters.AddWithValue("$default", StateKeys.ConfigRoutingDefault);
        command.Parameters.AddWithValue("$prefix", StateKeys.ConfigRoutingPrefix);
        command.Parameters.AddWithValue("$key", StateKeys.SelectedRoutingList);
        command.Parameters.AddWithValue("$list", list);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
