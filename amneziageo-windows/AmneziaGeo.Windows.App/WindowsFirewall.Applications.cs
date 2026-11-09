using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AmneziaGeo.Windows.App;

internal sealed partial class WindowsFirewall
{
    private IntPtr _applicationEngine;
    private IReadOnlyList<ApplicationFilterTarget> _applicationTargets = [];
    private const uint MatchApplicationRange = 5;
    private const uint ApplicationRangeType = 258;
    private const uint MatchApplicationSuffix = 11;

    /// <summary>
    /// Replaces the blocked files and directories.
    /// </summary>
    public bool BlockApplications(IReadOnlyList<ApplicationFilterTarget> targets)
    {
        lock (_gate)
        {
            if (_applicationTargets.SequenceEqual(targets))
            {
                return true;
            }

            if (targets.Count == 0)
            {
                DisableApplicationBlocksLocked();
                return true;
            }

            var next = IntPtr.Zero;
            try
            {
                var session = new FWPM_SESSION0 { flags = SessionFlagDynamic };
                var opened = FwpmEngineOpen0(IntPtr.Zero, RpcCAuthnWinnt, IntPtr.Zero, ref session, out next);
                if (opened != 0)
                {
                    throw new InvalidOperationException($"FwpmEngineOpen0 failed 0x{opened:X8}");
                }

                var layer = Guid.NewGuid();
                if (!CreateSublayer(next, layer, "AmneziaGeo application blocks"))
                {
                    throw new InvalidOperationException("Application sublayer could not be created");
                }

                foreach (var target in targets)
                {
                    AddApplicationBlock(next, layer, target);
                }

                var previous = _applicationEngine;
                _applicationEngine = next;
                next = IntPtr.Zero;
                _applicationTargets = targets.ToArray();
                if (previous != IntPtr.Zero)
                {
                    FwpmEngineClose0(previous);
                }

                logger.LogInformation("application traffic is blocked by {Count} file and directory rules", targets.Count);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "application blocking rules could not be installed");
                return false;
            }
            finally
            {
                if (next != IntPtr.Zero)
                {
                    FwpmEngineClose0(next);
                }
            }
        }
    }

    private void AddApplicationBlock(IntPtr engine, Guid sublayer, ApplicationFilterTarget target)
    {
        var path = target.Name ? "\\" + target.Path : AppMatcher.ToDevicePath(target.Path)
            ?? throw new InvalidOperationException($"The application path could not be resolved: {target.Path}");
        var allocations = new List<IntPtr>();
        try
        {
            IntPtr Blob(string value)
            {
                var bytes = Encoding.Unicode.GetBytes(value.ToLowerInvariant() + '\0');
                var data = Marshal.AllocHGlobal(bytes.Length);
                allocations.Add(data);
                Marshal.Copy(bytes, 0, data, bytes.Length);
                var blob = Marshal.AllocHGlobal(Marshal.SizeOf<FWP_BYTE_BLOB>());
                allocations.Add(blob);
                Marshal.StructureToPtr(new FWP_BYTE_BLOB { size = (uint)bytes.Length, data = data }, blob, false);
                return blob;
            }

            var matchType = target.Name ? MatchApplicationSuffix : MatchEqual;
            var valueType = FwpByteBlobType;
            var value = Blob(path);
            if (target.Directory)
            {
                var prefix = path.TrimEnd('\\') + '\\';
                var range = new ApplicationIdRange
                {
                    Low = new FWP_VALUE0 { type = FwpByteBlobType, value = (ulong)Blob(prefix) },
                    High = new FWP_VALUE0 { type = FwpByteBlobType, value = (ulong)Blob(prefix[..^1] + ']') },
                };
                value = Marshal.AllocHGlobal(Marshal.SizeOf<ApplicationIdRange>());
                allocations.Add(value);
                Marshal.StructureToPtr(range, value, false);
                matchType = MatchApplicationRange;
                valueType = ApplicationRangeType;
            }

            var condition = Condition(CondAleAppId, matchType, valueType, (ulong)value);
            foreach (var layer in new[] { LayerAleAuthConnectV4, LayerAleAuthConnectV6, LayerAleAuthRecvAcceptV4, LayerAleAuthRecvAcceptV6 })
            {
                var result = AddRaw(engine, sublayer, layer, WeightBlockList, ActionBlock, 0, [condition], "Block an application", out _);
                if (result != 0)
                {
                    throw new InvalidOperationException($"Application filter failed 0x{result:X8}");
                }
            }
        }
        finally
        {
            foreach (var pointer in allocations)
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
    }

    private void DisableApplicationBlocksLocked()
    {
        if (_applicationEngine != IntPtr.Zero)
        {
            FwpmEngineClose0(_applicationEngine);
            _applicationEngine = IntPtr.Zero;
        }

        _applicationTargets = [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ApplicationIdRange
    {
        public FWP_VALUE0 Low;
        public FWP_VALUE0 High;
    }
}
