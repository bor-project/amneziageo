using AmneziaGeo.Dal;
using AmneziaGeo.Decl;
using AmneziaGeo.Geo;
using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class RoutingApplicationPersistenceTests
{
    [Fact]
    public async Task DirectAndBlockApplications_PersistAcrossRenameAndExport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ag727-persistence-{Guid.NewGuid():N}.db");
        var store = new SqliteStateStore(path);
        try
        {
            await store.InitializeAsync().ConfigureAwait(false);
            var rules = new GeoRule[]
            {
                new(GeoRuleKind.App, @"path=C:\Prepared\direct.exe", RouteRole.Direct),
                new(GeoRuleKind.App, @"dir=C:\Prepared\blocked", RouteRole.Block),
                new(GeoRuleKind.App, "tool", RouteRole.Block),
            };
            var id = await store.SaveRoutingListAsync(new RoutingList(0, "applications", rules, [], [], [], [], [], [], [], [])).ConfigureAwait(false);
            await store.SaveConfigAsync("before", "[Interface]\nAddress = 10.77.0.2/32\n").ConfigureAwait(false);
            await ConfigRouting.SelectListAsync(store, "before", id).ConfigureAwait(false);
            await store.RenameConfigAsync("before", "after").ConfigureAwait(false);
            await ConfigRouting.RenameListAsync(store, "before", "after").ConfigureAwait(false);
            Assert.Equal(id, await ConfigRouting.SelectedListAsync(store, "after").ConfigureAwait(false));
            var list = await store.GetRoutingListAsync(id).ConfigureAwait(false);
            Assert.NotNull(list);
            Assert.Equal(rules, list.Rules);
            Assert.Equal([rules[0].Value], list.DirectApps);

            var expanded = AppFinds.Expand(list, new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["tool"] = [@"path=C:\Prepared\resolved.exe"],
            });
            Assert.Contains(expanded.Rules, rule => rule.Role == RouteRole.Block && rule.Value == @"path=C:\Prepared\resolved.exe");

            var editor = new RoutingListEditorViewModel(new EmptyAgent());
            editor.Name = list.Name;
            editor.DirectRules.Add("app:" + rules[0].Value);
            editor.BlockRules.Add("app:" + rules[1].Value);
            editor.BlockRules.Add("app:" + rules[2].Value);
            Assert.True(PortableTransfer.TryDecodeRouting(editor.BuildTransferPayload(), out var name, out var tokens));
            Assert.Equal("applications", name);
            Assert.Equal(new[] { "direct|app:" + rules[0].Value, "block|app:" + rules[1].Value, "block|app:" + rules[2].Value }, tokens);
        }
        finally
        {
            store.ClearPool();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }

    private sealed class EmptyAgent : IAgentConnection
    {
        public event Action? Connected { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public event Action<StatusSnapshot>? SnapshotReceived { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
        public Task<IpcAck> SendCommandAsync(IpcCommand command) => Task.FromResult(new IpcAck(true, string.Empty));
        public Task<IpcAck> SendCommandRawAsync(IpcCommand command) => SendCommandAsync(command);
    }
}
