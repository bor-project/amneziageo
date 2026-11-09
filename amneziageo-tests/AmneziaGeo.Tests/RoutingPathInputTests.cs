using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class RoutingPathInputTests
{
    [Theory]
    [InlineData("tool.exe", false)]
    [InlineData("data.txt", false)]
    [InlineData("folder", true)]
    public async Task AssignedPath_OffersAnEntry(string name, bool directory)
    {
        var root = Path.Combine(Path.GetTempPath(), $"routing-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        if (directory)
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            await File.WriteAllTextAsync(path, string.Empty).ConfigureAwait(false);
        }

        try
        {
            var editor = new Editor { AddMethod = "app", IsWideLayout = true };
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = AppPathToken.NormalizeAppRule($"app:{(directory ? "dir" : "path")}={path}");
            editor.PropertyChanged += (_, _) =>
            {
                if (editor.MatchedSuggestions.Any(suggestion => suggestion.Token == token))
                {
                    ready.TrySetResult();
                }
            };

            editor.RuleInput = path;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            Assert.True(editor.ShowDropdownSuggestions);
            editor.PickSuggestionCommand.Execute(token);
            Assert.Contains(token, editor.Rules);
            Assert.Empty(editor.RuleInput);
        }
        finally
        {
            if (directory)
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }

            Directory.Delete(root);
        }
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("direct", true)]
    [InlineData("block", false)]
    [InlineData("block", true)]
    public async Task DirectAndBlock_OfferPreparedPaths(string role, bool directory)
    {
        var path = Path.Combine(Path.GetTempPath(), $"prepared-{Guid.NewGuid():N}", directory ? "folder" : "tool.exe");
        var editor = new Editor { SelectedRole = role, AddMethod = "app", IsWideLayout = true };
        Assert.True(editor.CanAddApps);
        var token = AppPathToken.NormalizeAppRule($"app:{(directory ? "dir" : "path")}={path}");
        editor.RuleInput = directory ? token : path;
        for (var attempt = 0; attempt < 100 && !editor.MatchedSuggestions.Any(item => item.Token == token); attempt++)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        var suggestion = Assert.Single(editor.MatchedSuggestions, item => item.Token == token);
        Assert.True(suggestion.IsWarning);
        editor.PickSuggestionCommand.Execute(token);
        Assert.Contains(token, editor.Rules);
    }

    private sealed class Editor() : RoutingListEditorViewModel(new EmptyAgent())
    {
        protected override Task<IReadOnlyList<AppCandidate>> LoadInstalledAppsAsync() => Task.FromResult<IReadOnlyList<AppCandidate>>([]);
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
