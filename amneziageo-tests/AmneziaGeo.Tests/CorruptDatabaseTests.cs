using AmneziaGeo.Dal;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Self-heal of a corrupt SQLite database on store initialization: the broken file is quarantined and a
/// clean one is created, so the agent starts with defaults instead of dying (installer Error 1920).
/// </summary>
public sealed class CorruptDatabaseTests
{
    [Fact]
    public async Task StateStore_CorruptFile_QuarantinedAndRecreated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-corrupt-{Guid.NewGuid():N}.db");
        await File.WriteAllTextAsync(path, "this is not a sqlite database at all, just enough bytes to trip the header check");
        try
        {
            var store = new SqliteStateStore(path);
            await store.InitializeAsync();

            await store.SetSettingAsync("probe", "value");
            Assert.Equal("value", await store.GetSettingAsync("probe"));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*"));
            store.ClearPool();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task LogStore_CorruptFile_QuarantinedAndRecreated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-corrupt-log-{Guid.NewGuid():N}.db");
        await File.WriteAllTextAsync(path, "garbage bytes standing in for a database whose disk image is malformed");
        try
        {
            using var store = new SqliteLogStore(path);
            await store.InitializeAsync();

            var page = await store.QueryAsync(SqliteLogStore.AgentTable, null, 10, null, null);
            Assert.Empty(page.Rows);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task StateStore_ForeignWalPair_DataSurvives()
    {
        // The field shape: an intact main file next to a -wal/-shm pair from another database generation.
        // Whether SQLite silently discards the pair or the sidecar-quarantine step kicks in, initialization
        // must not throw, the main file must stay put, and its data must survive.
        var path = Path.Combine(Path.GetTempPath(), $"ageo-corrupt-wal-{Guid.NewGuid():N}.db");
        try
        {
            var seed = new SqliteStateStore(path);
            await seed.InitializeAsync();
            await seed.SetSettingAsync("probe", "original");
            seed.ClearPool();
            // Checkpoint on pool close leaves no live sidecars; plant a foreign pair in their place.
            await File.WriteAllBytesAsync(path + "-wal", MakeFakeWal());
            await File.WriteAllBytesAsync(path + "-shm", new byte[32768]);

            var store = new SqliteStateStore(path);
            await store.InitializeAsync();
            Assert.Equal("original", await store.GetSettingAsync("probe"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*"));
            store.ClearPool();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Quarantine_SidecarStep_KeepsMainFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ageo-quarantine-{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(path, "main");
            File.WriteAllText(path + "-wal", "wal");
            File.WriteAllText(path + "-shm", "shm");

            CorruptQuarantine.MoveAsideSidecars(path);

            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + "-wal"));
            Assert.False(File.Exists(path + "-shm"));
            Assert.Empty(Directory.GetFiles(dir, name + ".corrupt-*"));
            Assert.Single(Directory.GetFiles(dir, name + "-wal.corrupt-*"));
            Assert.Single(Directory.GetFiles(dir, name + "-shm.corrupt-*"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // A syntactically plausible WAL header (magic + version) followed by garbage frames.
    private static byte[] MakeFakeWal()
    {
        var wal = new byte[32 + 4096];
        // 0x377f0682 big-endian WAL magic.
        wal[0] = 0x37;
        wal[1] = 0x7f;
        wal[2] = 0x06;
        wal[3] = 0x82;
        // File format 3007000.
        wal[4] = 0x00;
        wal[5] = 0x2d;
        wal[6] = 0xe2;
        wal[7] = 0x18;
        var random = new Random(42);
        random.NextBytes(wal.AsSpan(32));
        return wal;
    }

    private static void Cleanup(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        foreach (var file in Directory.GetFiles(dir, name + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
