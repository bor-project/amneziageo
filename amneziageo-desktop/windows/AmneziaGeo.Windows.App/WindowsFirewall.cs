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
    // QUIC block outranks tunnel permit (10) but not the app hard-permit (14).
    private const byte WeightQuicBlock = 12;
    private const byte WeightApp = 14;
    private const byte WeightHyperV = 14;

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
    private IntPtr _engine = IntPtr.Zero;

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
    /// Arms the kill-switch; permits before block. Returns false on failure.
    /// </summary>
    public bool Enable(uint tunnelInterfaceIndex, bool killSwitch, bool dualStack, string? underlayAppPath = null, IReadOnlyList<string>? extraLanCidrs = null)
    {
        lock (_gate)
        {
            DisableLocked();

            if (ConvertInterfaceIndexToLuid(tunnelInterfaceIndex, out var luid) != 0)
            {
                logger.LogError("firewall: could not resolve LUID for interface index {Index}", tunnelInterfaceIndex);
                return false;
            }

            var session = new FWPM_SESSION0 { flags = SessionFlagDynamic };
            var open = FwpmEngineOpen0(IntPtr.Zero, RpcCAuthnWinnt, IntPtr.Zero, ref session, out var engine);
            if (open != 0)
            {
                logger.LogError("firewall: FwpmEngineOpen0 failed 0x{Code:X8}", open);
                return false;
            }

            try
            {
                CreateSublayer(engine);

                // Block QUIC (UDP/443) on tunnel so HTTP/3 falls back to TCP.
                BlockTunnelQuic(engine, luid);

                if (killSwitch)
                {
                    PermitApp(engine);

                    // Permit wstunnel.exe (carries the encrypted underlay in a child process).
                    if (!string.IsNullOrEmpty(underlayAppPath) && File.Exists(underlayAppPath))
                    {
                        PermitExe(engine, underlayAppPath, "Permit wstunnel underlay");
                    }

                    PermitTunInterface(engine, luid);
                    PermitLoopback(engine);
                    PermitDhcpV4(engine);
                    PermitLan(engine, extraLanCidrs ?? []);
                    if (dualStack)
                    {
                        PermitLanV6(engine);
                    }

                    TryPermitHyperV(engine); // best-effort.

                    BlockAll(engine);
                }

                _engine = engine;
                logger.LogInformation("firewall armed on interface {Index} (killSwitch={KillSwitch}, dualStack={DualStack})", tunnelInterfaceIndex, killSwitch, dualStack);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "firewall: failed to install filters");
                FwpmEngineClose0(engine);
                return false;
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
        if (_engine != IntPtr.Zero)
        {
            FwpmEngineClose0(_engine);
            _engine = IntPtr.Zero;
            logger.LogInformation("kill-switch disabled");
        }
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
            logger.LogWarning("kill-switch: FwpmGetAppIdFromFileName0 failed 0x{Code:X8} for {Path}", rc, path);
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

            foreach (var layer in AleLayers)
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

        foreach (var layer in AleLayers)
        {
            Add(engine, layer, WeightLoopback, ActionPermit, 0, cond, "Permit loopback");
        }
    }

    private void PermitDhcpV4(IntPtr engine)
    {
        var cond = new[]
        {
            Condition(CondIpProtocol, MatchEqual, FwpUint8, ProtocolUdp),
            Condition(CondIpLocalPort, MatchEqual, FwpUint16, 68),
            Condition(CondIpRemotePort, MatchEqual, FwpUint16, 67),
        };

        Add(engine, LayerAleAuthConnectV4, WeightDhcp, ActionPermit, 0, cond, "Permit outbound DHCP");
        Add(engine, LayerAleAuthRecvAcceptV4, WeightDhcp, ActionPermit, 0, cond, "Permit inbound DHCP");
    }

    private void PermitLan(IntPtr engine, IReadOnlyList<string> extraCidrs)
    {
        // Infrastructure ranges always permitted.
        foreach (var cidr in LanInfraCidrsV4)
        {
            PermitV4Cidr(engine, cidr);
        }

        // A malformed entry skips, never aborts arming.
        foreach (var cidr in extraCidrs)
        {
            try
            {
                PermitV4Cidr(engine, cidr);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "kill-switch: skipping LAN CIDR {Cidr}", cidr);
            }
        }
    }

    private void PermitV4Cidr(IntPtr engine, string cidr)
    {
        if (!TryParseV4Cidr(cidr, out var addr, out var mask))
        {
            logger.LogWarning("kill-switch: skipping invalid LAN CIDR {Cidr}", cidr);
            return;
        }

        var maskPtr = Marshal.AllocHGlobal(2 * sizeof(uint)); // FWP_V4_ADDR_AND_MASK { UINT32 addr; UINT32 mask; }
        try
        {
            Marshal.WriteInt32(maskPtr, 0, (int)addr);
            Marshal.WriteInt32(maskPtr, sizeof(uint), (int)mask);
            var cond = new[]
            {
                Condition(CondIpRemoteAddress, MatchEqual, FwpV4AddrMask, (ulong)maskPtr),
            };

            Add(engine, LayerAleAuthConnectV4, WeightLan, ActionPermit, 0, cond, $"Permit LAN {cidr} (out)");
            Add(engine, LayerAleAuthRecvAcceptV4, WeightLan, ActionPermit, 0, cond, $"Permit LAN {cidr} (in)");
        }
        finally
        {
            Marshal.FreeHGlobal(maskPtr);
        }
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
                    logger.LogWarning("kill-switch: v6 LAN permit {Cidr} not fully installed (out=0x{Out:X8}, in=0x{In:X8})", cidr, rcOut, rcIn);
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
                logger.LogWarning("kill-switch: Hyper-V permit not installed (0x{Code:X8}); continuing", rc);
                return;
            }
        }
    }

    private void BlockTunnelQuic(IntPtr engine, ulong luid)
    {
        // Block QUIC (UDP/443) egressing the tunnel; v4 only (AAAA denied at proxy).
        var luidPtr = Marshal.AllocHGlobal(sizeof(ulong));
        try
        {
            Marshal.WriteInt64(luidPtr, (long)luid);
            var cond = new[]
            {
                Condition(CondIpLocalInterface, MatchEqual, FwpUint64, (ulong)luidPtr),
                Condition(CondIpProtocol, MatchEqual, FwpUint8, ProtocolUdp),
                Condition(CondIpRemotePort, MatchEqual, FwpUint16, 443),
            };

            Add(engine, LayerAleAuthConnectV4, WeightQuicBlock, ActionBlock, 0, cond, "Block QUIC (UDP/443) on tunnel");
        }
        finally
        {
            Marshal.FreeHGlobal(luidPtr);
        }
    }

    private void BlockAll(IntPtr engine)
    {
        // Block-all at lowest weight; also blocks all v6 intentionally (v4-only tunnel).
        foreach (var layer in AleLayers)
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

            return FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _);
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

    // FWP_MATCH_TYPE.
    private const uint MatchEqual = 0;
    private const uint MatchFlagsAllSet = 6;

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
    private static partial uint FwpmFilterAdd0(IntPtr engineHandle, ref FWPM_FILTER0 filter, IntPtr sd, out ulong id);

    [LibraryImport("fwpuclnt.dll", EntryPoint = "FwpmGetAppIdFromFileName0", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [LibraryImport("fwpuclnt.dll")]
    private static partial uint FwpmFreeMemory0(ref IntPtr p);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out ulong interfaceLuid);
}
