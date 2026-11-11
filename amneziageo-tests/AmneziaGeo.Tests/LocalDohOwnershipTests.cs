using System.Net;
using System.Text.Json;
using AmneziaGeo.Windows.App;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmneziaGeo.Tests;

public sealed class LocalDohOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ageo-doh-" + Guid.NewGuid().ToString("N"));
    private readonly Resources _resources = new();
    private static readonly IPAddress First = IPAddress.Parse("127.0.0.1");
    private static readonly IPAddress Second = IPAddress.Parse("127.0.0.2");

    [Fact]
    public void ASecondaryCleanupLeavesTheLivePrimaryRegistrationAndBinding()
    {
        using var primary = Hold("primary", First);
        Reconcile("secondary");
        Reconcile();
        Assert.Equal(Template(First, primary.AppId), _resources.Template(First));
        Assert.Equal(primary.AppId, _resources.Binding(First));
        Assert.Empty(_resources.Deleted);
        Assert.Null(LocalDohOwnership.Take(_root, "secondary", First, _resources, NullLogger.Instance));
    }

    [Fact]
    public void NormalReleaseRemovesOnlyItsOwnResources()
    {
        using var primary = Hold("primary", First);
        var secondary = Hold("secondary", Second);
        secondary.Dispose();
        Assert.Equal(Template(First, primary.AppId), _resources.Template(First));
        Assert.Equal(primary.AppId, _resources.Binding(First));
        Assert.Null(_resources.Template(Second));
        Assert.Null(_resources.Binding(Second));
        Assert.Equal(["dns:127.0.0.2", "ssl:127.0.0.2"], _resources.Deleted);
    }

    [Fact]
    public void RecoveryUsesTheSavedAddressAndRunsOnce()
    {
        using var primary = Hold("primary", First);
        LeaveStale("secondary", Second);
        Reconcile("secondary");
        Reconcile("secondary");
        Assert.Equal(primary.AppId, _resources.Binding(First));
        Assert.Null(_resources.Template(Second));
        Assert.Null(_resources.Binding(Second));
        Assert.Equal(["dns:127.0.0.2", "ssl:127.0.0.2"], _resources.Deleted);
    }

    [Fact]
    public void GeneralRecoveryKeepsTheNamedTunnel()
    {
        var primaryId = LeaveStale("primary", First);
        LeaveStale("secondary", Second);
        LocalDohOwnership.Reconcile(_root, _resources, NullLogger.Instance, keep: ["PRIMARY"]);
        Assert.Equal(Template(First, primaryId), _resources.Template(First));
        Assert.NotNull(_resources.Binding(First));
        Assert.Null(_resources.Template(Second));
        Assert.Null(_resources.Binding(Second));
    }

    [Fact]
    public void AnAbortedRecoveryDoesNotDeleteAfterReadingThePlatform()
    {
        var primaryId = LeaveStale("primary", First);
        var abort = false;
        _resources.TemplateRead = () => abort = true;
        LocalDohOwnership.Reconcile(_root, _resources, NullLogger.Instance, abortIf: () => abort);
        Assert.Empty(_resources.Deleted);
        _resources.TemplateRead = null;
        Assert.Equal(Template(First, primaryId), _resources.Template(First));
        Assert.Single(Directory.GetFiles(_root, "*.json"));
    }

    [Fact]
    public void AbortingBetweenResourcesPreservesThePendingBindingForRetry()
    {
        LeaveStale("primary", First);
        var abort = false;
        _resources.TemplateRemoved = () => abort = true;
        LocalDohOwnership.Reconcile(_root, _resources, NullLogger.Instance, abortIf: () => abort);
        Assert.Equal(["dns:127.0.0.1"], _resources.Deleted);
        Assert.NotNull(_resources.Binding(First));
        Assert.Single(Directory.GetFiles(_root, "*.json"));
        _resources.TemplateRemoved = null;
        Reconcile();
        Assert.Null(_resources.Binding(First));
        Assert.Empty(Directory.GetFiles(_root, "*.json"));
    }

    [Fact]
    public void ADelayedOldReleaseCannotRemoveTheNextSession()
    {
        var previous = Hold("primary", First);
        previous.Dispose();
        _resources.Deleted.Clear();
        using var current = Hold("primary", First);
        previous.Dispose();
        Reconcile("primary");
        Assert.Equal(current.AppId, _resources.Binding(First));
        Assert.Equal(Template(First, current.AppId), _resources.Template(First));
        Assert.Empty(_resources.Deleted);
    }

    [Fact]
    public void AReplacementSessionTakesOverOnlyStaleOwnedResources()
    {
        var previousId = LeaveStale("primary", First);
        using var current = Hold("primary", First);
        Assert.NotEqual(previousId, current.AppId);
        Assert.Equal(current.AppId, _resources.Binding(First));
        Assert.Equal(["dns:127.0.0.1", "ssl:127.0.0.1"], _resources.Deleted);
    }

    [Fact]
    public void AForeignReplacementIsPreservedDuringRelease()
    {
        var owner = Hold("primary", First);
        _resources.Templates[First] = "https://foreign.example/dns-query";
        var foreign = Guid.NewGuid().ToString("B");
        _resources.Bindings[First] = foreign;
        owner.Dispose();
        Assert.Empty(_resources.Deleted);
        Assert.Equal("https://foreign.example/dns-query", _resources.Template(First));
        Assert.Equal(foreign, _resources.Binding(First));
        Assert.Empty(Directory.GetFiles(_root, "*.json"));
    }

    [Fact]
    public void AnUnownedRegistrationPreventsTakeover()
    {
        _resources.Templates[First] = Template(First);
        Assert.Null(LocalDohOwnership.Take(_root, "primary", First, _resources, NullLogger.Instance));
        Assert.Empty(_resources.Deleted);
        Assert.Empty(Directory.GetFiles(_root, "*.json"));
    }

    [Fact]
    public void AnUnownedBindingPreventsTakeover()
    {
        _resources.Bindings[First] = Guid.NewGuid().ToString("B");
        Assert.Null(LocalDohOwnership.Take(_root, "primary", First, _resources, NullLogger.Instance));
        Assert.Empty(_resources.Deleted);
    }

    [Fact]
    public void AFailedPartialRegistrationCanBeReleased()
    {
        var owner = LocalDohOwnership.Take(_root, "primary", First, _resources, NullLogger.Instance)!;
        _resources.Bindings[First] = owner.AppId;
        owner.Dispose();
        Assert.Equal(["ssl:127.0.0.1"], _resources.Deleted);
        Assert.Empty(Directory.GetFiles(_root, "*.json"));
    }

    [Fact]
    public void AFailedCleanupRetainsOwnershipForRecovery()
    {
        LeaveStale("primary", First);
        _resources.ThrowOnRemove = true;
        Reconcile();
        Assert.Single(Directory.GetFiles(_root, "*.json"));
        _resources.ThrowOnRemove = false;
        Reconcile();
        Assert.Empty(Directory.GetFiles(_root, "*.json"));
        Assert.Null(_resources.Template(First));
    }

    [Theory]
    [InlineData("dns")]
    [InlineData("ssl")]
    public void AFailedQueryKeepsTheRecoveryRecord(string kind)
    {
        LeaveStale("primary", First);
        _resources.ReadFailure = kind;
        Reconcile();
        Assert.Single(Directory.GetFiles(_root, "*.json"));
        Assert.DoesNotContain("ssl:127.0.0.1", _resources.Deleted);
        _resources.ReadFailure = null;
        Reconcile();
        Assert.Empty(Directory.GetFiles(_root, "*.json"));
        Assert.Null(_resources.Template(First));
        Assert.Null(_resources.Binding(First));
    }

    [Theory]
    [InlineData("dns")]
    [InlineData("ssl")]
    public void AnUncertainQueryDoesNotClaimTheAddress(string kind)
    {
        _resources.ReadFailure = kind;
        Assert.Throws<IOException>(() => LocalDohOwnership.Take(_root, "primary", First, _resources, NullLogger.Instance));
        Assert.Empty(_resources.Deleted);
        _resources.ReadFailure = null;
        using var owner = Hold("primary", First);
    }

    [Fact]
    public void ACorruptRecordDoesNotAffectAnotherAddress()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "7F000001.json"), "broken");
        _resources.Templates[First] = Template(First);
        LeaveStale("secondary", Second);
        Reconcile();
        Assert.Equal(Template(First), _resources.Template(First));
        Assert.Null(_resources.Template(Second));
    }

    [Fact]
    public void ARecordCannotClaimAnAddressDifferentFromItsFilename()
    {
        Directory.CreateDirectory(_root);
        var appId = Guid.NewGuid().ToString("B");
        File.WriteAllText(Path.Combine(_root, "7F000002.json"), JsonSerializer.Serialize(new
        {
            Tunnel = "primary", Address = First.ToString(), AppId = appId, Template = Template(First, appId),
        }));
        _resources.Templates[First] = Template(First);
        Reconcile();
        Assert.Empty(_resources.Deleted);
    }

    [Fact]
    public void AResolverLeaseRejectsExternalAddresses()
    {
        Assert.Throws<ArgumentException>(() => LocalDohOwnership.Take(_root, "primary", IPAddress.Parse("192.0.2.1"), _resources, NullLogger.Instance));
    }

    private LocalDohOwnership Hold(string name, IPAddress address)
    {
        var owner = LocalDohOwnership.Take(_root, name, address, _resources, NullLogger.Instance);
        Assert.NotNull(owner);
        _resources.Templates[address] = Template(address, owner.AppId);
        _resources.Bindings[address] = owner.AppId;
        return owner;
    }

    private string LeaveStale(string name, IPAddress address)
    {
        var owner = Hold(name, address);
        _resources.ThrowOnRemove = true;
        owner.Dispose();
        _resources.ThrowOnRemove = false;
        return owner.AppId;
    }

    private void Reconcile(string? tunnel = null)
    {
        LocalDohOwnership.Reconcile(_root, _resources, NullLogger.Instance, tunnel: tunnel);
    }

    private static string Template(IPAddress address, string? appId = null) => appId is null
        ? $"https://{address}/dns-query" : LocalDohOwnership.TemplateFor(address, appId);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class Resources : ILocalDohResources
    {
        public Dictionary<IPAddress, string> Templates { get; } = [];
        public Dictionary<IPAddress, string> Bindings { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool ThrowOnRemove { get; set; }
        public string? ReadFailure { get; set; }
        public Action? TemplateRead { get; set; }
        public Action? TemplateRemoved { get; set; }

        public string? Template(IPAddress address)
        {
            if (ReadFailure == "dns")
            {
                throw new IOException("Injected DNS query failure");
            }

            TemplateRead?.Invoke();
            return Templates.GetValueOrDefault(address);
        }

        public string? Binding(IPAddress address)
        {
            if (ReadFailure == "ssl")
            {
                throw new IOException("Injected SSL query failure");
            }

            return Bindings.GetValueOrDefault(address);
        }

        public void RemoveTemplate(IPAddress address)
        {
            if (ThrowOnRemove)
            {
                throw new IOException("Injected failure");
            }

            Deleted.Add("dns:" + address);
            Templates.Remove(address);
            TemplateRemoved?.Invoke();
        }

        public void RemoveBinding(IPAddress address)
        {
            if (ThrowOnRemove)
            {
                throw new IOException("Injected failure");
            }

            Deleted.Add("ssl:" + address);
            Bindings.Remove(address);
        }
    }
}
