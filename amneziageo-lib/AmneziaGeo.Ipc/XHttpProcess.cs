using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using AmneziaGeo.Decl;
using Microsoft.Win32.SafeHandles;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Owns one Xray UDP carrier.
/// </summary>
public sealed class XHttpProcess : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private readonly Process _process;
    private readonly ProcessJob? _job;
    private readonly Action<string, Exception?>? _note;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _stopGate = new();
    private readonly Task _output;
    private readonly Task _errors;
    private Task? _stop;
    private int _stopping;

    /// <summary>
    /// ctor
    /// </summary>
    private XHttpProcess(Process process, ProcessJob? job, int port, Action<string, Exception?>? note)
    {
        _process = process;
        _job = job;
        _note = note;
        LocalPort = port;
        ProcessId = process.Id;
        _output = DrainAsync(process.StandardOutput);
        _errors = DrainAsync(process.StandardError);
        Completion = ObserveAsync();
    }

    public int LocalPort { get; }
    public int ProcessId { get; }
    public Task<int> Completion { get; }
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "xray.exe" : "xray");

    /// <summary>
    /// Reports whether the owned child is running.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Starts a carrier for the selected UDP destination.
    /// </summary>
    public static Task<XHttpProcess> StartAsync(ResolvedTransportProxy selected, IPAddress dialAddress, CancellationToken ct,
        Action<string, Exception?>? note = null) => StartAsync(selected, dialAddress, ExecutablePath, ct, note);

    internal static async Task<XHttpProcess> StartAsync(ResolvedTransportProxy selected, IPAddress dialAddress, string executable,
        CancellationToken ct, Action<string, Exception?>? note = null)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(dialAddress);
        ct.ThrowIfCancellationRequested();
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        var profile = XHttpProfile.Create(selected, port, dialAddress);
        if (!File.Exists(executable))
        {
            throw new TransportProxyFault("xhttp-runtime-missing");
        }

        await CheckVersionAsync(executable, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        reservation.Dispose();
        var (process, job) = StartOwned(Command(executable, "run", "-format", "json", "-c", "stdin:"));
        var carrier = new XHttpProcess(process, job, port, note);
        try
        {
            await process.StandardInput.WriteAsync(profile.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            process.StandardInput.Close();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(StartTimeout);
            try
            {
                var ready = await Task.WhenAny(carrier._ready.Task, carrier.Completion).WaitAsync(deadline.Token).ConfigureAwait(false);
                if (ready != carrier._ready.Task || !carrier.IsRunning)
                {
                    throw new TransportProxyFault("xhttp-runtime-exited");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TransportProxyFault("xhttp-runtime-timeout");
            }

            ct.ThrowIfCancellationRequested();
            return carrier;
        }
        catch
        {
            await carrier.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static ProcessStartInfo Command(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var key in start.Environment.Keys.Where(one => one.StartsWith("XRAY_", StringComparison.OrdinalIgnoreCase)
            || one.StartsWith("V2RAY_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }

        return start;
    }

    internal static async Task CheckVersionAsync(string executable, CancellationToken ct)
    {
        var (process, job) = StartOwned(Command(executable, "version"));
        using (process)
        using (job)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(StartTimeout);
            var output = VersionOutputAsync(process.StandardOutput);
            var errors = DiscardAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                var version = await output.ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                if (process.ExitCode != 0 || !version.StartsWith("Xray " + XHttpProfile.Version + " ", StringComparison.Ordinal))
                {
                    throw new TransportProxyFault("xhttp-runtime-version");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TransportProxyFault("xhttp-version-timeout");
            }
            finally
            {
                Kill(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(output, errors).ConfigureAwait(false);
            }
        }
    }

    private static (Process Process, ProcessJob? Job) StartOwned(ProcessStartInfo start)
    {
        var process = new Process { StartInfo = start };
        var job = OperatingSystem.IsWindows() ? ProcessJob.Create() : null;
        try
        {
            if (!process.Start())
            {
                throw new TransportProxyFault("xhttp-runtime-start");
            }

            job?.Attach(process);
            return (process, job);
        }
        catch
        {
            Kill(process);
            job?.Dispose();
            process.Dispose();
            throw;
        }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Contains("Xray " + XHttpProfile.Version + " started", StringComparison.Ordinal))
            {
                _ready.TrySetResult();
            }
        }
    }

    private static async Task<string> VersionOutputAsync(StreamReader reader)
    {
        var output = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToString();
            }

            if (output.Length < 8192)
            {
                output.Append(buffer, 0, Math.Min(read, 8192 - output.Length));
            }
        }
    }

    private static async Task DiscardAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0)
        {
        }
    }

    private async Task<int> ObserveAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        var code = _process.ExitCode;
        await Task.WhenAll(_output, _errors).ConfigureAwait(false);
        if (Volatile.Read(ref _stopping) == 0)
        {
            _note?.Invoke($"The XHTTP carrier stopped with exit code {code}.", null);
        }

        return code;
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        lock (_stopGate)
        {
            return new ValueTask(_stop ??= StopAsync());
        }
    }

    private async Task StopAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _job?.Dispose();
        Kill(_process);
        try
        {
            await Completion.WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private sealed class ProcessJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        /// <summary>
        /// ctor
        /// </summary>
        private ProcessJob() : base(ownsHandle: true)
        {
        }

        public static ProcessJob Create()
        {
            var job = CreateJobObject(0, null);
            if (job.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                job.Dispose();
                throw new Win32Exception(error);
            }

            var limits = new JobLimits { BasicLimitInformation = new BasicLimits { LimitFlags = 0x00002000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()))
            {
                var error = Marshal.GetLastPInvokeError();
                job.Dispose();
                throw new Win32Exception(error);
            }

            return job;
        }

        public void Attach(Process process)
        {
            if (!AssignProcessToJobObject(this, process.SafeHandle))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobLimits
        {
            public BasicLimits BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ProcessJob CreateJobObject(nint attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(ProcessJob job, int infoClass, ref JobLimits info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(ProcessJob job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }
}
