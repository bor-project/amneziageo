using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// Seeding the machine root from a legacy per-user root runs on every agent start, so it must never lay another
/// database's transaction files next to a live one and must hand a legacy record over only once.
/// </summary>
public sealed class MachineMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ageo-seed-{Guid.NewGuid():N}");

    private string User => Path.Combine(_root, "user");

    private string Machine => Path.Combine(_root, "machine");

    [Fact]
    public void AMachineLogFolder_TakesNothingFromTheLegacyOne()
    {
        Write(User, "logs", "log.db", "user");
        Write(User, "logs", "log.db-wal", "user wal");
        Write(User, "logs", "log.db-shm", "user shm");
        Write(Machine, "logs", "log.db", "machine");

        MachineMigration.Seed(User, Machine);

        Assert.Equal("machine", File.ReadAllText(Path.Combine(Machine, "logs", "log.db")));
        Assert.False(File.Exists(Path.Combine(Machine, "logs", "log.db-wal")));
        Assert.False(File.Exists(Path.Combine(Machine, "logs", "log.db-shm")));
    }

    [Fact]
    public void AMissingFolder_IsCopiedWithoutTransactionFiles()
    {
        Write(User, "logs", "log.db", "user");
        Write(User, "logs", "log.db-wal", "user wal");
        Write(User, "geo", "ru.dat", "geo");

        MachineMigration.Seed(User, Machine);

        Assert.Equal("user", File.ReadAllText(Path.Combine(Machine, "logs", "log.db")));
        Assert.False(File.Exists(Path.Combine(Machine, "logs", "log.db-wal")));
        Assert.Equal("geo", File.ReadAllText(Path.Combine(Machine, "geo", "ru.dat")));
    }

    [Fact]
    public void ALegacyRecord_IsHandedOverOnce()
    {
        Write(User, null, "dns-state-a.txt", "legacy a");
        Write(User, null, "dns-state-b.txt", "legacy b");
        Write(Machine, null, "dns-state-b.txt", "machine b");

        MachineMigration.Seed(User, Machine);

        Assert.Equal("legacy a", File.ReadAllText(Path.Combine(Machine, "dns-state-a.txt")));
        Assert.Equal("machine b", File.ReadAllText(Path.Combine(Machine, "dns-state-b.txt")));
        Assert.Empty(Directory.GetFiles(User, "dns-state*.txt"));

        File.Delete(Path.Combine(Machine, "dns-state-a.txt"));
        MachineMigration.Seed(User, Machine);

        Assert.False(File.Exists(Path.Combine(Machine, "dns-state-a.txt")));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Write(string root, string? folder, string name, string text)
    {
        var directory = folder is null ? root : Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), text);
    }
}
