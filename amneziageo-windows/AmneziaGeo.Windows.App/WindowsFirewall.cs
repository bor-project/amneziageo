using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

/// <summary>
/// WFP kill-switch: block-all plus permits for this process, tunnel, loopback, DHCP, LAN.
/// </summary>
internal sealed partial class WindowsFirewall(ILogger<WindowsFirewall> logger) : IDisposable
{
    // Filter weights (FWP_UINT8, 0..15); highest-weight matching filter wins.
    private const byte WeightBlock = 0;
    private const byte WeightLan = 2;
    private const byte WeightDhcp = 4;
    private const byte WeightLoopback = 8;
    private const byte WeightTun = 10;
    private const byte WeightApp = 14;
    private const byte WeightHyperV = 14;
    // Block-list filters outrank every permit so a blocked destination is dropped regardless of LAN/tunnel/DHCP.
    private const byte WeightBlockList = 15;

    // Infrastructure ranges (not user-controllable); LAN bypass comes from extraCidrs.
    private static readonly string[] LanInfraCidrsV4 =
    [
        "169.254.0.0/16",
        "224.0.0.0/4",
        "255.255.255.255/32",
    ];

    // v6 LAN bypass: ULA, link-local, link-local multicast.
    private static readonly string[] LanCidrsV6 =
    [
        "fc00::/7",
        "fe80::/10",
        "ff02::/16",
    ];

    private readonly object _gate = new();

    // Where an outbound v4 decision is installed. On the ALE layer a block answers the program with an error the
    // moment it calls connect; on the transport layer the packet is dropped instead, so the stack repeats it and
    // the repeat gets through once the verdict lands. IPv6 stays on ALE - no verdict is ever learned for it.
    private Guid _outboundV4 = LayerAleAuthConnectV4;
    private IntPtr _engine = IntPtr.Zero;
    // Second, non-dynamic handle: only the event options and the subscription live on it.
    private IntPtr _eventEngine = IntPtr.Zero;
    private IntPtr _eventSubscription = IntPtr.Zero;
    private ulong? _collectPrevious;
    private ulong? _keywordsPrevious;
    // Held so the thunk the engine calls back into outlives the subscription.
    private NetEventCallback? _dropCallback;
    private Action<IPAddress, string?>? _onDrop;
    private int _dropEvents;
    // A dynamic session drops its filters with the engine handle, and arming rebuilds the set from scratch, so
    // on-demand permits carry the generation they were installed under and are reinstalled when it moves.
    private int _generation;

    /// <summary>
    /// True while the kill-switch filters are installed.
    /// </summary>
    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _engine != IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Filter-set generation; moves on every arm. Read without the gate: the data plane checks it per connection,
    /// and the gate is held across kernel calls.
    /// </summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Arms the kill-switch; permits before block. Block-list destinations are dropped per address on contact,
    /// not materialized here. With <paramref name="softBlock"/> the outbound v4 block drops packets instead of
    /// refusing connect, so a program retries into the verdict rather than failing on it. Returns false on failure.
    /// </summary>
    public bool Enable(uint tunnelInterfaceIndex, bool killSwitch, bool dualStack, string? underlayAppPath = null, IReadOnlyList<string>? extraLanCidrs = null, IReadOnlyList<uint>? alsoPermit = null, bool softBlock = false, IPAddress? underlayEndpoint = null)
    {
        lock (_gate)
        {
            DisableLocked();
            _outboundV4 = softBlock ? LayerOutboundTransportV4 : LayerAleAuthConnectV4;

            if (ConvertInterfaceIndexToLuid(tunnelInterfaceIndex, out var luid) != 0)
            {
                logger.LogError("the tunnel adapter (index {Index}) could not be identified, so no leak protection is installed and traffic may leave past the tunnel", tunnelInterfaceIndex);
                return false;
            }

            var session = new FWPM_SESSION0 { flags = SessionFlagDynamic };
            var open = FwpmEngineOpen0(IntPtr.Zero, RpcCAuthnWinnt, IntPtr.Zero, ref session, out var engine);
            if (open != 0)
            {
                logger.LogError("the Windows filtering engine refused to open (0x{Code:X8}); no leak protection is installed — check that the Base Filtering Engine service is running", open);
                return false;
            }

            // One transaction for the whole set: a bypass list of thousands of CIDRs costs as many engine
            // rebuilds otherwise, and the stack is unusable while they run.
            var batched = FwpmTransactionBegin0(engine, 0) == 0;

            try
            {
                if (killSwitch)
                {
                    CreateSublayer(engine);
                    PermitApp(engine);

                    // Permit wstunnel.exe (carries the encrypted underlay in a child process).
                    if (!string.IsNullOrEmpty(underlayAppPath) && File.Exists(underlayAppPath))
                    {
                        PermitExe(engine, underlayAppPath, "Permit wstunnel underlay");
                    }

                    PermitTunInterface(engine, luid);

                    // The adapters of the tunnels standing alongside this one: what a rule sends to them leaves
                    // through their adapter, and this block would otherwise be the end of it.
                    foreach (var peer in alsoPermit ?? [])
                    {
                        if (ConvertInterfaceIndexToLuid(peer, out var peerLuid) == 0)
                        {
                            PermitTunInterface(engine, peerLuid);
                        }
                    }

                    PermitLoopback(engine);
                    PermitDhcpV4(engine);
                    PermitLan(engine, extraLanCidrs ?? []);

                    // Permits the underlay by address: the transport layer carries no ALE_APP_ID to permit it by.
                    if (underlayEndpoint is not null && underlayEndpoint.AddressFamily == AddressFamily.InterNetwork)
                    {
                        PermitV4Cidr(engine, $"{underlayEndpoint}/32");
                    }

                    // Stand-in addresses of the shared access point. Nothing leaves the machine to them: the
                    // gateway adapter terminates them and this process opens the name again, which the leak
                    // protection already permits. Without this the first packet of every client is dropped here.
                    PermitV4Cidr(engine, HotspotNames.Prefix);
                    if (dualStack)
                    {
                        PermitLanV6(engine);
                    }

                    TryPermitHyperV(engine); // best-effort.

                    BlockAll(engine);
                }

                if (batched)
                {
                    var commit = FwpmTransactionCommit0(engine);
                    if (commit != 0)
                    {
                        throw new InvalidOperationException($"FwpmTransactionCommit0 failed 0x{commit:X8}");
                    }
                }

                _engine = engine;
                Interlocked.Increment(ref _generation);
                if (killSwitch)
                {
                    logger.LogInformation("leak protection is on for adapter {Index}: anything not going through the tunnel, your own network or the allowed programs is now blocked (IPv6 covered too: {DualStack})", tunnelInterfaceIndex, dualStack);
                }
                else
                {
                    logger.LogInformation("adapter {Index} holds no blocking rules of its own; what leaves this machine is decided by the tunnel that carries it", tunnelInterfaceIndex);
                }

                if (softBlock)
                {
                    logger.LogDebug("a destination with no verdict yet is dropped without answering the program, so the network stack repeats the attempt and the repeat goes through once the verdict is there");
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "the leak-protection rules could not be installed; they are rolled back whole, so the tunnel runs without protection rather than half-blocked");
                if (batched)
                {
                    FwpmTransactionAbort0(engine);
                }

                FwpmEngineClose0(engine);
                return false;
            }
        }
    }

    /// <summary>
    /// Reports the destination of every packet the filters drop. A destination blocked before its verdict exists
    /// announces itself here and nowhere else: the send never happens, so no ETW send or connect event follows it.
    /// Returns false when the platform has no subscription - the tunnel still runs, on-demand just loses this source.
    /// </summary>
    public bool WatchDrops(Action<IPAddress, string?> onDrop)
    {
        lock (_gate)
        {
            if (_engine == IntPtr.Zero || _eventSubscription != IntPtr.Zero)
            {
                return false;
            }

            _onDrop = onDrop;
            try
            {
                if (!OpenEventEngineLocked() || !CollectDropsLocked())
                {
                    CloseEventEngineLocked();
                    return false;
                }

                _dropCallback = OnNetEvent;
                var subscription = new FWPM_NET_EVENT_SUBSCRIPTION0();
                var rc = FwpmNetEventSubscribe4(_eventEngine, ref subscription, Marshal.GetFunctionPointerForDelegate(_dropCallback), IntPtr.Zero, out var handle);
                if (rc != 0)
                {
                    logger.LogWarning("the firewall would not report what it blocks (0x{Code:X8}); addresses are learned from outgoing connections only, so an app with a hard-coded address may stay blocked", rc);
                    _dropCallback = null;
                    CloseEventEngineLocked();
                    return false;
                }

                _eventSubscription = handle;
                logger.LogInformation("blocked destinations are now reported back, so an address an app reaches without a name lookup still gets routed on the next try");
                return true;
            }
            catch (EntryPointNotFoundException ex)
            {
                logger.LogWarning(ex, "this version of Windows cannot report blocked destinations; addresses are learned from outgoing connections only, so an app with a hard-coded address may stay blocked");
                CloseEventEngineLocked();
                return false;
            }
        }
    }

    // Event collection is an engine-wide option, and a dynamic session may not set one (FWP_E_DYNAMIC_SESSION_IN_PROGRESS):
    // events get a plain session of their own while the filters keep the dynamic one that drops them with the process.
    private bool OpenEventEngineLocked()
    {
        if (_eventEngine != IntPtr.Zero)
        {
            return true;
        }

        var session = new FWPM_SESSION0();
        var rc = FwpmEngineOpen0(IntPtr.Zero, RpcCAuthnWinnt, IntPtr.Zero, ref session, out var engine);
        if (rc != 0)
        {
            logger.LogWarning("a second connection to the filtering engine, the one that reads blocked destinations, failed to open (0x{Code:X8}); blocked addresses will not be learned", rc);
            return false;
        }

        _eventEngine = engine;
        return true;
    }

    // Turns on event collection and narrows it to classify drops, so an allow-heavy host does not pay for events
    // nobody reads. Prior values are kept and restored on teardown - the options belong to the machine, not to us.
    private bool CollectDropsLocked()
    {
        _collectPrevious = ReadOptionLocked(EngineOptionCollectNetEvents);
        if (!SetOptionLocked(EngineOptionCollectNetEvents, 1, "enabling net events"))
        {
            return false;
        }

        _keywordsPrevious = ReadOptionLocked(EngineOptionNetEventMatchAnyKeywords);
        SetOptionLocked(EngineOptionNetEventMatchAnyKeywords, NetEventKeywordClassifyDrop, "narrowing net events");
        return true;
    }

    private bool SetOptionLocked(uint option, ulong value, string what)
    {
        var wanted = new FWP_VALUE0 { type = FwpUint32, value = value };
        var rc = FwpmEngineSetOption0(_eventEngine, option, ref wanted);
        if (rc != 0)
        {
            logger.LogWarning("{What} failed (0x{Code:X8}); blocked destinations may not be reported, and an app with a hard-coded address can stay blocked", what, rc);
            return false;
        }

        return true;
    }

    private ulong? ReadOptionLocked(uint option)
    {
        if (FwpmEngineGetOption0(_eventEngine, option, out var value) != 0 || value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<FWP_VALUE0>(value).value;
        }
        finally
        {
            FwpmFreeMemory0(ref value);
        }
    }

    private void CloseEventEngineLocked()
    {
        if (_eventEngine == IntPtr.Zero)
        {
            return;
        }

        if (_eventSubscription != IntPtr.Zero)
        {
            FwpmNetEventUnsubscribe0(_eventEngine, _eventSubscription);
            _eventSubscription = IntPtr.Zero;
        }

        if (_keywordsPrevious is { } keywords)
        {
            SetOptionLocked(EngineOptionNetEventMatchAnyKeywords, keywords, "restoring net event keywords");
            _keywordsPrevious = null;
        }

        if (_collectPrevious is { } collect)
        {
            SetOptionLocked(EngineOptionCollectNetEvents, collect, "restoring net event collection");
            _collectPrevious = null;
        }

        FwpmEngineClose0(_eventEngine);
        _eventEngine = IntPtr.Zero;
        _dropCallback = null;
        _onDrop = null;
    }

    // Engine callback: reads the destination out of the event header and hands it over. Anything unexpected is
    // dropped silently - this runs on an engine thread, and an exception crossing back into it takes the process.
    private void OnNetEvent(IntPtr context, IntPtr netEvent)
    {
        try
        {
            var sink = _onDrop;
            if (sink is null || netEvent == IntPtr.Zero)
            {
                return;
            }

            Interlocked.Increment(ref _dropEvents);
            var version = Marshal.ReadInt32(netEvent, HeaderIpVersionOffset);
            var app = ReadAppId(netEvent);
            if (version == IpVersionV4)
            {
                var value = (uint)Marshal.ReadInt32(netEvent, HeaderRemoteAddressOffset);
                if (value != 0)
                {
                    sink(new IPAddress(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }), app);
                }

                return;
            }

            if (version == IpVersionV6)
            {
                var raw = new byte[16];
                Marshal.Copy(netEvent + HeaderRemoteAddressOffset, raw, 0, raw.Length);
                sink(new IPAddress(raw), app);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "one blocked-destination report could not be read and was skipped; that address is decided on its next attempt");
        }
    }

    // Image path the dropped packet belonged to, as the NT device path the engine reports. Absent on events the
    // engine could not attribute.
    private static string? ReadAppId(IntPtr netEvent)
    {
        var flags = (uint)Marshal.ReadInt32(netEvent, HeaderFlagsOffset);
        if ((flags & HeaderFlagAppIdSet) == 0)
        {
            return null;
        }

        var size = (uint)Marshal.ReadInt32(netEvent, HeaderAppIdSizeOffset);
        var data = Marshal.ReadIntPtr(netEvent, HeaderAppIdDataOffset);
        if (data == IntPtr.Zero || size < sizeof(char) || size > MaxAppIdBytes)
        {
            return null;
        }

        return Marshal.PtrToStringUni(data, (int)(size / sizeof(char)))?.TrimEnd('\0');
    }

    /// <summary>
    /// Whether classify drops are being reported, and how many arrived.
    /// </summary>
    public (bool Watching, int Events) DropWatch
    {
        get
        {
            lock (_gate)
            {
                return (_eventSubscription != IntPtr.Zero, Volatile.Read(ref _dropEvents));
            }
        }
    }

    /// <summary>
    /// Removes all kill-switch filters.
    /// </summary>
    public void Disable()
    {
        lock (_gate)
        {
            DisableLocked();
        }
    }

    private void DisableLocked()
    {
        if (_engine == IntPtr.Zero)
        {
            return;
        }

        CloseEventEngineLocked();
        FwpmEngineClose0(_engine);
        _engine = IntPtr.Zero;
        logger.LogInformation("leak protection is off; traffic is no longer restricted to the tunnel");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Disable();
    }

    private void CreateSublayer(IntPtr engine)
    {
        var namePtr = Marshal.StringToHGlobalUni("AmneziaGeo kill-switch");
        try
        {
            var sublayer = new FWPM_SUBLAYER0
            {
                subLayerKey = SublayerKey,
                displayData = new FWPM_DISPLAY_DATA0 { name = namePtr },
                weight = 0xFFFF,
            };
            var rc = FwpmSubLayerAdd0(engine, ref sublayer, IntPtr.Zero);
            if (rc == FwpAlreadyExists)
            {
                // Leftover sublayer from a prior or overlapping session (reconnect churn, in-place upgrade). Drop it
                // when it is ours to drop, then re-add; if a live session still owns it, reuse it so the permits install.
                var key = SublayerKey;
                var del = FwpmSubLayerDeleteByKey0(engine, ref key);
                if (del == 0 || del == FwpSublayerNotFound)
                {
                    rc = FwpmSubLayerAdd0(engine, ref sublayer, IntPtr.Zero);
                }

                if (rc == FwpAlreadyExists)
                {
                    logger.LogWarning("the leak-protection group left by a previous session is still held by it, so this session reuses it; its blocking rules stay in force until that session lets go");
                    return;
                }
            }

            if (rc != 0)
            {
                throw new InvalidOperationException($"FwpmSubLayerAdd0 failed 0x{rc:X8}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }
    }

    // ---- rule builders ------------------------------------------------------------------------

    private void PermitApp(IntPtr engine)
    {
        var path = Environment.ProcessPath
            ?? throw new InvalidOperationException("kill-switch: cannot determine this process's executable path");

        if (!PermitExe(engine, path, "Permit AmneziaGeo app"))
        {
            throw new InvalidOperationException("kill-switch: could not permit the AmneziaGeo app");
        }
    }

    // Hard permit (CLEAR_ACTION_RIGHT) so the underlay survives other sublayers.
    private bool PermitExe(IntPtr engine, string path, string label)
    {
        var rc = FwpmGetAppIdFromFileName0(path, out var appId);
        if (rc != 0 || appId == IntPtr.Zero)
        {
            logger.LogWarning("the program {Path} could not be identified to the firewall (0x{Code:X8}), so it gets no exemption and the leak protection will block it", path, rc);
            return false;
        }

        try
        {
            var cond = new[]
            {
                Condition(CondAleAppId, MatchEqual, FwpByteBlobType, (ulong)appId),
            };

            foreach (var layer in AleLayers)
            {
                Add(engine, layer, WeightApp, ActionPermit, FilterFlagClearActionRight, cond, label);
            }

            return true;
        }
        finally
        {
            FwpmFreeMemory0(ref appId);
        }
    }

    private void PermitTunInterface(IntPtr engine, ulong luid)
    {
        var luidPtr = Marshal.AllocHGlobal(sizeof(ulong));
        try
        {
            Marshal.WriteInt64(luidPtr, (long)luid);
            var cond = new[]
            {
                Condition(CondIpLocalInterface, MatchEqual, FwpUint64, (ulong)luidPtr),
            };

            foreach (var layer in DecisionLayers)
            {
                Add(engine, layer, WeightTun, ActionPermit, 0, cond, "Permit tunnel interface");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(luidPtr);
        }
    }

    private void PermitLoopback(IntPtr engine)
    {
        var cond = new[]
        {
            Condition(CondFlags, MatchFlagsAllSet, FwpUint32, ConditionFlagIsLoopback),
        };

        foreach (var layer in DecisionLayers)
        {
            Add(engine, layer, WeightLoopback, ActionPermit, 0, cond, "Permit loopback");
        }
    }

    private void PermitDhcpV4(IntPtr engine)
    {
        // Client role: this machine takes a lease.
        PermitDhcpV4Ports(engine, 68, 67, "DHCP client");

        // Server role: shared-connection leases for hotspot clients; the first DISCOVER comes from 0.0.0.0 and matches no address bypass.
        PermitDhcpV4Ports(engine, 67, 68, "DHCP server");
    }

    private void PermitDhcpV4Ports(IntPtr engine, ushort localPort, ushort remotePort, string role)
    {
        var cond = new[]
        {
            Condition(CondIpProtocol, MatchEqual, FwpUint8, ProtocolUdp),
            Condition(CondIpLocalPort, MatchEqual, FwpUint16, localPort),
            Condition(CondIpRemotePort, MatchEqual, FwpUint16, remotePort),
        };

        Add(engine, _outboundV4, WeightDhcp, ActionPermit, 0, cond, $"Permit outbound {role}");
        Add(engine, LayerAleAuthRecvAcceptV4, WeightDhcp, ActionPermit, 0, cond, $"Permit inbound {role}");
    }

    private void PermitLan(IntPtr engine, IReadOnlyList<string> extraCidrs)
    {
        // Infrastructure ranges always permitted.
        foreach (var cidr in LanInfraCidrsV4)
        {
            PermitV4Cidr(engine, cidr);
        }

        // A bypass list carries the v6 half of a geo database; counting it beats a warning per entry.
        var permitted = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var cidr in extraCidrs)
        {
            if (!TryParseV4Cidr(cidr, out var addr, out var mask))
            {
                skipped++;
                continue;
            }

            if (PermitV4Range(engine, addr, mask))
            {
                permitted++;
            }
            else
            {
                failed++;
            }
        }

        if (skipped > 0 || failed > 0)
        {
            logger.LogInformation("{Permitted} address range(s) are allowed past the leak protection; {Skipped} were IPv6 and this protection covers IPv4 only, {Failed} were refused by the firewall and stay blocked", permitted, skipped, failed);
        }
    }

    private void PermitV4Cidr(IntPtr engine, string cidr)
    {
        if (!TryParseV4Cidr(cidr, out var addr, out var mask))
        {
            logger.LogWarning("the local range {Cidr} is not a valid IPv4 range and was skipped; traffic to it will be blocked while the tunnel is up", cidr);
            return;
        }

        if (!PermitV4Range(engine, addr, mask))
        {
            throw new InvalidOperationException($"kill-switch: could not permit LAN {cidr}");
        }
    }

    // Shared display names keep a bypass list of thousands off the per-filter string marshalling.
    private bool PermitV4Range(IntPtr engine, uint addr, uint mask)
    {
        var maskPtr = Marshal.AllocHGlobal(2 * sizeof(uint)); // FWP_V4_ADDR_AND_MASK { UINT32 addr; UINT32 mask; }
        try
        {
            Marshal.WriteInt32(maskPtr, 0, (int)addr);
            Marshal.WriteInt32(maskPtr, sizeof(uint), (int)mask);
            var cond = new[]
            {
                Condition(CondIpRemoteAddress, MatchEqual, FwpV4AddrMask, (ulong)maskPtr),
            };

            var rcOut = AddRaw(engine, _outboundV4, WeightLan, ActionPermit, 0, cond, "Permit bypass (out)");
            var rcIn = AddRaw(engine, LayerAleAuthRecvAcceptV4, WeightLan, ActionPermit, 0, cond, "Permit bypass (in)");
            return rcOut == 0 && rcIn == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(maskPtr);
        }
    }

    /// <summary>
    /// Permits one host address through the physical path, reporting the filter ids and the generation they belong to.
    /// </summary>
    public bool TryPermitHost(uint address, out ulong outId, out ulong inId, out int generation)
    {
        return TryHostFilter(address, ActionPermit, WeightLan, "Permit direct host", out outId, out inId, out generation);
    }

    /// <summary>
    /// Drops one host address at block-list weight, so it loses to no permit.
    /// </summary>
    public bool TryDropHost(uint address, out ulong outId, out ulong inId, out int generation)
    {
        return TryHostFilter(address, ActionBlock, WeightBlockList, "Block host", out outId, out inId, out generation);
    }

    private bool TryHostFilter(uint address, uint action, byte weight, string label, out ulong outId, out ulong inId, out int generation)
    {
        outId = 0;
        inId = 0;
        lock (_gate)
        {
            generation = _generation;
            if (_engine == IntPtr.Zero)
            {
                return false;
            }

            var maskPtr = Marshal.AllocHGlobal(2 * sizeof(uint)); // FWP_V4_ADDR_AND_MASK { UINT32 addr; UINT32 mask; }
            try
            {
                Marshal.WriteInt32(maskPtr, 0, (int)address);
                Marshal.WriteInt32(maskPtr, sizeof(uint), unchecked((int)uint.MaxValue));
                var cond = new[]
                {
                    Condition(CondIpRemoteAddress, MatchEqual, FwpV4AddrMask, (ulong)maskPtr),
                };

                // A block belongs on ALE, where the program is told at once; a permit belongs where the block-all is.
                var outLayer = action == ActionBlock ? LayerAleAuthConnectV4 : _outboundV4;
                if (AddRaw(_engine, outLayer, weight, action, 0, cond, $"{label} (out)", out outId) != 0)
                {
                    outId = 0;
                    return false;
                }

                if (AddRaw(_engine, LayerAleAuthRecvAcceptV4, weight, action, 0, cond, $"{label} (in)", out inId) != 0)
                {
                    DeleteByIdLocked(outId);
                    outId = 0;
                    inId = 0;
                    return false;
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(maskPtr);
            }
        }
    }

    /// <summary>
    /// Deletes host filters installed under <paramref name="generation"/> in one transaction; returns the pairs removed.
    /// A generation mismatch means an arm already dropped them.
    /// </summary>
    public int DeleteHostFilters(IReadOnlyList<(ulong Out, ulong In)> filters, int generation)
    {
        if (filters.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_engine == IntPtr.Zero || generation != _generation)
            {
                return 0;
            }

            var batched = FwpmTransactionBegin0(_engine, 0) == 0;
            var removed = 0;
            foreach (var (outId, inId) in filters)
            {
                if (DeleteByIdLocked(outId) && DeleteByIdLocked(inId))
                {
                    removed++;
                }
            }

            if (batched && FwpmTransactionCommit0(_engine) != 0)
            {
                FwpmTransactionAbort0(_engine);
                return 0;
            }

            return removed;
        }
    }

    private bool DeleteByIdLocked(ulong id)
    {
        return id != 0 && FwpmFilterDeleteById0(_engine, id) == 0;
    }

    private void PermitLanV6(IntPtr engine)
    {
        foreach (var cidr in LanCidrsV6)
        {
            var slash = cidr.IndexOf('/');
            var address = IPAddress.Parse(cidr[..slash]);
            var prefix = byte.Parse(cidr[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            var bytes = address.GetAddressBytes(); // 16 bytes, network order
            var maskPtr = Marshal.AllocHGlobal(17); // FWP_V6_ADDR_AND_MASK { UINT8 addr[16]; UINT8 prefixLength; }
            try
            {
                Marshal.Copy(bytes, 0, maskPtr, 16);
                Marshal.WriteByte(maskPtr, 16, prefix);
                var cond = new[]
                {
                    Condition(CondIpRemoteAddress, MatchEqual, FwpV6AddrMask, (ulong)maskPtr),
                };

                // Best-effort: v6 permit failure must not abort the v4 kill-switch.
                var rcOut = AddRaw(engine, LayerAleAuthConnectV6, WeightLan, ActionPermit, 0, cond, $"Permit LAN v6 {cidr} (out)");
                var rcIn = AddRaw(engine, LayerAleAuthRecvAcceptV6, WeightLan, ActionPermit, 0, cond, $"Permit LAN v6 {cidr} (in)");
                if (rcOut != 0 || rcIn != 0)
                {
                    logger.LogWarning("the IPv6 range {Cidr} of your own network was only partly allowed (outgoing 0x{Out:X8}, incoming 0x{In:X8}); IPv6 devices on the LAN may be unreachable while the tunnel is up", cidr, rcOut, rcIn);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(maskPtr);
            }
        }
    }

    private void TryPermitHyperV(IntPtr engine)
    {
        var cond = new[]
        {
            Condition(CondL2Flags, MatchEqual, FwpUint32, ConditionL2IsVm2Vm),
        };

        foreach (var layer in MacFrameLayers)
        {
            var rc = AddRaw(engine, layer, WeightHyperV, ActionPermit, 0, cond, "Permit Hyper-V");
            if (rc != 0)
            {
                logger.LogWarning("traffic between virtual machines could not be exempted (0x{Code:X8}); if you run Hyper-V, its guests may lose the network while the tunnel is up", rc);
                return;
            }
        }
    }

    private void BlockAll(IntPtr engine)
    {
        // Block-all at lowest weight; also blocks all v6 intentionally (v4-only tunnel).
        foreach (var layer in DecisionLayers)
        {
            Add(engine, layer, WeightBlock, ActionBlock, 0, [], "Block all");
        }
    }

    // ---- filter plumbing ----------------------------------------------------------------------

    private static FWPM_FILTER_CONDITION0 Condition(Guid fieldKey, uint matchType, uint valueType, ulong value)
    {
        return new FWPM_FILTER_CONDITION0
        {
            fieldKey = fieldKey,
            matchType = matchType,
            conditionValue = new FWP_VALUE0 { type = valueType, value = value },
        };
    }

    private void Add(IntPtr engine, Guid layer, byte weight, uint actionType, uint flags, FWPM_FILTER_CONDITION0[] conditions, string name)
    {
        var rc = AddRaw(engine, layer, weight, actionType, flags, conditions, name);
        if (rc != 0)
        {
            throw new InvalidOperationException($"FwpmFilterAdd0('{name}') failed 0x{rc:X8}");
        }
    }

    private uint AddRaw(IntPtr engine, Guid layer, byte weight, uint actionType, uint flags, FWPM_FILTER_CONDITION0[] conditions, string name)
    {
        return AddRaw(engine, layer, weight, actionType, flags, conditions, name, out _);
    }

    private uint AddRaw(IntPtr engine, Guid layer, byte weight, uint actionType, uint flags, FWPM_FILTER_CONDITION0[] conditions, string name, out ulong id)
    {
        var namePtr = Marshal.StringToHGlobalUni(name);
        var conditionSize = Marshal.SizeOf<FWPM_FILTER_CONDITION0>();
        var conditionArray = IntPtr.Zero;
        if (conditions.Length > 0)
        {
            conditionArray = Marshal.AllocHGlobal(conditionSize * conditions.Length);
            for (var i = 0; i < conditions.Length; i++)
            {
                Marshal.StructureToPtr(conditions[i], conditionArray + (i * conditionSize), false);
            }
        }

        try
        {
            var filter = new FWPM_FILTER0
            {
                layerKey = layer,
                subLayerKey = SublayerKey,
                weight = new FWP_VALUE0 { type = FwpUint8, value = weight },
                flags = flags,
                numFilterConditions = (uint)conditions.Length,
                filterCondition = conditionArray,
                action = new FWPM_ACTION0 { type = actionType },
                displayData = new FWPM_DISPLAY_DATA0 { name = namePtr },
            };

            return FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out id);
        }
        finally
        {
            if (conditionArray != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(conditionArray);
            }

            Marshal.FreeHGlobal(namePtr);
        }
    }

    // Malformed CIDR returns false (skipped, not aborted).
    private static bool TryParseV4Cidr(string cidr, out uint addr, out uint mask)
    {
        addr = 0;
        mask = 0;
        var slash = cidr.IndexOf('/');
        if (slash < 0
            || !IPAddress.TryParse(cidr[..slash], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(cidr[(slash + 1)..], out var bits)
            || bits is < 0 or > 32)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        addr = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        addr &= mask;
        return true;
    }

    // ---- interop ------------------------------------------------------------------------------

    private static readonly Guid SublayerKey = new("c3a4f1d2-8b6e-4a2f-9c5d-1e7a3b9f4d80");

    // Layer / condition GUIDs (fwpmu.h).
    private static readonly Guid LayerAleAuthConnectV4 = new(0xc38d57d1, 0x05a7, 0x4c33, 0x90, 0x4f, 0x7f, 0xbc, 0xee, 0xe6, 0x0e, 0x82);
    private static readonly Guid LayerAleAuthConnectV6 = new(0x4a72393b, 0x319f, 0x44bc, 0x84, 0xc3, 0xba, 0x54, 0xdc, 0xb3, 0xb6, 0xb4);
    private static readonly Guid LayerAleAuthRecvAcceptV4 = new(0xe1cd9fe7, 0xf4b5, 0x4273, 0x96, 0xc0, 0x59, 0x2e, 0x48, 0x7b, 0x86, 0x50);
    private static readonly Guid LayerAleAuthRecvAcceptV6 = new(0xa3b42c97, 0x9f04, 0x4672, 0xb8, 0x7e, 0xce, 0xe9, 0xc4, 0x83, 0x25, 0x7f);
    private static readonly Guid LayerOutboundTransportV4 = new(0x09e61aea, 0xd214, 0x46e2, 0x9b, 0x21, 0xb2, 0x6b, 0x0b, 0x2f, 0x28, 0xc8);
    private static readonly Guid LayerOutboundMacFrameNative = new(0x94c44912, 0x9d6f, 0x4ebf, 0xb9, 0x95, 0x05, 0xab, 0x8a, 0x08, 0x8d, 0x1b);
    private static readonly Guid LayerInboundMacFrameNative = new(0xd4220bd3, 0x62ce, 0x4f08, 0xae, 0x88, 0xb5, 0x6e, 0x85, 0x26, 0xdf, 0x50);

    private static readonly Guid CondIpRemoteAddress = new(0xb235ae9a, 0x1d64, 0x49b8, 0xa4, 0x4c, 0x5f, 0xf3, 0xd9, 0x09, 0x50, 0x45);
    private static readonly Guid CondIpLocalInterface = new(0x4cd62a49, 0x59c3, 0x4969, 0xb7, 0xf3, 0xbd, 0xa5, 0xd3, 0x28, 0x90, 0xa4);
    private static readonly Guid CondFlags = new(0x632ce23b, 0x5167, 0x435c, 0x86, 0xd7, 0xe9, 0x03, 0x68, 0x4a, 0xa8, 0x0c);
    private static readonly Guid CondIpProtocol = new(0x3971ef2b, 0x623e, 0x4f9a, 0x8c, 0xb1, 0x6e, 0x79, 0xb8, 0x06, 0xb9, 0xa7);
    private static readonly Guid CondIpLocalPort = new(0x0c1ba1af, 0x5765, 0x453f, 0xaf, 0x22, 0xa8, 0xf7, 0x91, 0xac, 0x77, 0x5b);
    private static readonly Guid CondIpRemotePort = new(0xc35a604d, 0xd22b, 0x4e1a, 0x91, 0xb4, 0x68, 0xf6, 0x74, 0xee, 0x67, 0x4b);
    private static readonly Guid CondAleAppId = new(0xd78e1e87, 0x8644, 0x4ea5, 0x94, 0x37, 0xd8, 0x09, 0xec, 0xef, 0xc9, 0x71);
    private static readonly Guid CondL2Flags = new(0x7bc43cbf, 0x37ba, 0x45f1, 0xb7, 0x4a, 0x82, 0xff, 0x51, 0x8e, 0xeb, 0x10);

    private static readonly Guid[] AleLayers =
    [
        LayerAleAuthConnectV4,
        LayerAleAuthRecvAcceptV4,
        LayerAleAuthConnectV6,
        LayerAleAuthRecvAcceptV6,
    ];

    // The four layers a v4/v6 decision is installed on; the outbound v4 half follows the soft-block choice.
    private Guid[] DecisionLayers =>
    [
        _outboundV4,
        LayerAleAuthRecvAcceptV4,
        LayerAleAuthConnectV6,
        LayerAleAuthRecvAcceptV6,
    ];

    private static readonly Guid[] MacFrameLayers =
    [
        LayerOutboundMacFrameNative,
        LayerInboundMacFrameNative,
    ];

    // FWP_ACTION_TYPE (fwptypes.h): action | FWP_ACTION_FLAG_TERMINATING (0x1000).
    private const uint ActionBlock = 0x00000001 | 0x00001000;
    private const uint ActionPermit = 0x00000002 | 0x00001000;

    // FWP_DATA_TYPE.
    private const uint FwpUint8 = 1;
    private const uint FwpUint16 = 2;
    private const uint FwpUint32 = 3;
    private const uint FwpUint64 = 4;
    private const uint FwpByteBlobType = 12;
    private const uint FwpV4AddrMask = 256; // FWP_V4_ADDR_MASK
    private const uint FwpV6AddrMask = 257; // FWP_V6_ADDR_MASK

    // FWP error codes (fwpmtypes.h).
    private const uint FwpAlreadyExists = 0x80320009; // FWP_E_ALREADY_EXISTS
    private const uint FwpSublayerNotFound = 0x80320007; // FWP_E_SUBLAYER_NOT_FOUND

    // FWP_MATCH_TYPE.
    private const uint MatchEqual = 0;
    private const uint MatchFlagsAllSet = 6;

    // FWPM_ENGINE_OPTION: event collection, then the keyword filter that narrows it.
    private const uint EngineOptionCollectNetEvents = 0;
    private const uint EngineOptionNetEventMatchAnyKeywords = 1;
    private const uint NetEventKeywordClassifyDrop = 0x00000001;

    // FWPM_NET_EVENT_HEADER3 (x64): timeStamp 0, flags 8, ipVersion 12, ipProtocol 16, localAddr 20, remoteAddr 36.
    private const int HeaderIpVersionOffset = 12;
    private const int HeaderRemoteAddressOffset = 36;
    private const int HeaderFlagsOffset = 8;
    private const int HeaderAppIdSizeOffset = 64;
    private const int HeaderAppIdDataOffset = 72;
    private const uint HeaderFlagAppIdSet = 0x00000004;
    // Longest image path accepted from the event; anything above it is a layout mismatch, not a path.
    private const uint MaxAppIdBytes = 4096;
    private const int IpVersionV4 = 0;
    private const int IpVersionV6 = 1;

    private const uint FilterFlagClearActionRight = 0x00000008;
    private const uint SessionFlagDynamic = 0x00000001;
    private const uint RpcCAuthnWinnt = 10;
    private const uint ConditionFlagIsLoopback = 0x00000001;
    private const uint ConditionL2IsVm2Vm = 0x00000010;
    private const byte ProtocolUdp = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct FWP_VALUE0
    {
        public uint type;
        public ulong value; // inline integer, or a pointer for FWP_UINT64 / byte-blob / addr-and-mask
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWP_BYTE_BLOB
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_DISPLAY_DATA0
    {
        public IntPtr name;
        public IntPtr description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_ACTION0
    {
        public uint type;
        public Guid filterType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_FILTER_CONDITION0
    {
        public Guid fieldKey;
        public uint matchType;
        public FWP_VALUE0 conditionValue; // FWP_CONDITION_VALUE0, identical layout
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void NetEventCallback(IntPtr context, IntPtr netEvent);

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_NET_EVENT_SUBSCRIPTION0
    {
        public IntPtr enumTemplate;
        public uint flags;
        public Guid sessionKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_SESSION0
    {
        public Guid sessionKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public uint txnWaitTimeoutInMSec;
        public uint processId;
        public IntPtr sid;
        public IntPtr username;
        public int kernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FWPM_SUBLAYER0
    {
        public Guid subLayerKey;
        public FWPM_DISPLAY_DATA0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FWP_BYTE_BLOB providerData;
        public ushort weight;
    }

    // Explicit layout mirrors FWPM_FILTER0 (x64, 200 bytes); 4-byte gap before providerContextKey is the union 8-byte alignment.
    [StructLayout(LayoutKind.Explicit, Size = 200)]
    private struct FWPM_FILTER0
    {
        [FieldOffset(0)] public Guid filterKey;
        [FieldOffset(16)] public FWPM_DISPLAY_DATA0 displayData;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(40)] public IntPtr providerKey;
        [FieldOffset(48)] public FWP_BYTE_BLOB providerData;
        [FieldOffset(64)] public Guid layerKey;
        [FieldOffset(80)] public Guid subLayerKey;
        [FieldOffset(96)] public FWP_VALUE0 weight;
        [FieldOffset(112)] public uint numFilterConditions;
        [FieldOffset(120)] public IntPtr filterCondition;
        [FieldOffset(128)] public FWPM_ACTION0 action;
        [FieldOffset(152)] public Guid providerContextKey;
        [FieldOffset(168)] public IntPtr reserved;
        [FieldOffset(176)] public ulong filterId;
        [FieldOffset(184)] public FWP_VALUE0 effectiveWeight;
    }

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineOpen0(IntPtr serverName, uint authnService, IntPtr authIdentity, ref FWPM_SESSION0 session, out IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineClose0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FWPM_SUBLAYER0 subLayer, IntPtr sd);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterAdd0(IntPtr engineHandle, ref FWPM_FILTER0 filter, IntPtr sd, out ulong id);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionCommit0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmTransactionAbort0(IntPtr engineHandle);

    [LibraryImport("fwpuclnt.dll", EntryPoint = "FwpmGetAppIdFromFileName0", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFreeMemory0(ref IntPtr p);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineSetOption0(IntPtr engineHandle, uint option, ref FWP_VALUE0 newValue);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmEngineGetOption0(IntPtr engineHandle, uint option, out IntPtr value);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmNetEventSubscribe4(IntPtr engineHandle, ref FWPM_NET_EVENT_SUBSCRIPTION0 subscription, IntPtr callback, IntPtr context, out IntPtr eventsHandle);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmNetEventUnsubscribe0(IntPtr engineHandle, IntPtr eventsHandle);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong interfaceLuid);
}
