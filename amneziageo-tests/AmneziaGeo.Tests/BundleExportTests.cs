using System.Runtime.Versioning;
using AmneziaGeo.Cli;
using AmneziaGeo.Ipc;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// An exported bundle carries the private keys in the clear, so the file it lands in is never one another account
/// can read, not even for the moment the keys take to be written.
/// </summary>
[Collection("Output")]
public sealed class BundleExportTests : IDisposable
{
    private const string Bundle = "{\"configs\":[{\"name\":\"work\",\"configText\":\"[Interface]\"}]}";
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly BufferConsoleSink _console = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("amneziageo-bundle-").FullName;

    /// <summary>
    /// ctor
    /// </summary>
    public BundleExportTests()
    {
        Output.Sink = _console;
        Output.Json = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Output.Sink = new SystemConsoleSink();
        Directory.Delete(_folder, recursive: true);
    }

    [UnixFact("the mode of a file is a Unix permission")]
    [UnsupportedOSPlatform("windows")]
    public async Task TheBundle_LandsInAFileOnlyItsOwnerReads()
    {
        var path = Path.Combine(_folder, "bundle.json");

        var exit = await BundleCommands.RunAsync(new Link(), ["export", "--config", "work", "--out", path]);

        Assert.Equal(Exit.Ok, exit);
        Assert.Equal(Bundle, await File.ReadAllTextAsync(path));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_folder));
    }

    [UnixFact("the mode of a file is a Unix permission")]
    [UnsupportedOSPlatform("windows")]
    public async Task AReaderHoldingTheOldFileOpen_NeverSeesTheKeys()
    {
        var path = Path.Combine(_folder, "bundle.json");
        await File.WriteAllTextAsync(path, "old");
        File.SetUnixFileMode(path, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        using var held = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

        var exit = await BundleCommands.RunAsync(new Link(), ["export", "--config", "work", "--out", path]);

        Assert.Equal(Exit.Ok, exit);
        Assert.Equal("old", await held.ReadToEndAsync());
        Assert.Equal(Bundle, await File.ReadAllTextAsync(path));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
    }

    // Answers the export with a bundle.
    private sealed class Link : IAgentLink
    {
        /// <inheritdoc/>
        public event Action<StatusSnapshot>? SnapshotReceived
        {
            add { }
            remove { }
        }

        /// <inheritdoc/>
        public StatusSnapshot Snapshot { get; } = new("1.0.0", null, []);

        /// <inheritdoc/>
        public Task<IpcAck> SendAsync(string op, params string[] args) =>
            Task.FromResult(op == IpcContract.OpExportBundle ? new IpcAck(true, Bundle) : new IpcAck(false, op));
    }
}
