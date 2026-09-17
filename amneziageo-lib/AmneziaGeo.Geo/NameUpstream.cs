using AmneziaGeo.Ipc;

namespace AmneziaGeo.Geo;

/// <summary>
/// Asks the resolver behind the tunnel over the transport the settings allow, and on auto keeps the one that
/// answered where plain DNS was refused.
/// </summary>
public sealed class NameUpstream(
    string transport,
    Func<byte[], CancellationToken, Task<byte[]>> plain,
    Func<byte[], CancellationToken, Task<byte[]>>? stream = null,
    Func<byte[], CancellationToken, Task<byte[]>>? encrypted = null,
    Action<string>? report = null)
{
    /// <summary>
    /// Name of the plain transport in the state a check reports.
    /// </summary>
    public const string OnPlain = "plain 53";

    /// <summary>
    /// Name of the framed transport in the state a check reports.
    /// </summary>
    public const string OnStream = "tcp 53";

    /// <summary>
    /// Name of the encrypted transport in the state a check reports.
    /// </summary>
    public const string OnEncrypted = "doh";

    private readonly string _transport = DnsTransports.Of(transport);
    private readonly object _sync = new();
    private string _carrying = string.Empty;
    private string _reason = string.Empty;

    /// <summary>
    /// The transport the last answer came over, empty until one does.
    /// </summary>
    public string Carrying
    {
        get
        {
            lock (_sync)
            {
                return _carrying;
            }
        }
    }

    /// <summary>
    /// Why the transport in force was taken, empty where it was never in doubt.
    /// </summary>
    public string Reason
    {
        get
        {
            lock (_sync)
            {
                return _reason;
            }
        }
    }

    /// <summary>
    /// The transport and the reason as one line for a status report.
    /// </summary>
    public string State
    {
        get
        {
            lock (_sync)
            {
                var carrying = _carrying.Length > 0 ? _carrying : Planned();
                return _reason.Length > 0 ? $"{carrying} ({_reason})" : carrying;
            }
        }
    }

    /// <summary>
    /// Forgets the transport that was taken, so the next query starts at plain DNS again.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            if (_carrying.Length == 0 && _reason.Length == 0)
            {
                return;
            }

            _carrying = string.Empty;
            _reason = string.Empty;
        }
    }

    /// <summary>
    /// Sends one query and returns the answer, trying the transports the setting leaves open.
    /// </summary>
    public async Task<byte[]> AskAsync(byte[] query, CancellationToken ct = default)
    {
        if (_transport == DnsTransports.Plain || (encrypted is null && stream is null))
        {
            var answer = await plain(query, ct).ConfigureAwait(false);
            Took(OnPlain, string.Empty);
            return answer;
        }

        if (_transport == DnsTransports.Doh && encrypted is not null)
        {
            var answer = await encrypted(query, ct).ConfigureAwait(false);
            Took(OnEncrypted, string.Empty);
            return answer;
        }

        return await LadderAsync(query, ct).ConfigureAwait(false);
    }

    // Plain DNS, then the transports a filter on port 53 cannot reach, keeping the one that answers.
    private async Task<byte[]> LadderAsync(byte[] query, CancellationToken ct)
    {
        if (Carrying is OnStream or OnEncrypted)
        {
            var kept = Carrying == OnStream ? stream : encrypted;
            if (kept is not null)
            {
                try
                {
                    return await kept(query, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Reset();
                }
            }
        }

        var refusal = string.Empty;
        var refused = OnPlain;
        try
        {
            var answer = await plain(query, ct).ConfigureAwait(false);
            Took(OnPlain, string.Empty);
            return answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            refusal = Short(ex);
        }

        foreach (var (name, ask) in Rungs())
        {
            try
            {
                var answer = await ask(query, ct).ConfigureAwait(false);
                Took(name, $"{refused} {refusal}");
                return answer;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                refused = name;
                refusal = Short(ex);
            }
        }

        throw new IOException($"no transport reached the resolver behind the tunnel: {refused} {refusal}");
    }

    // The transports tried after plain DNS, in the order a filter on port 53 gives way to.
    private IEnumerable<(string Name, Func<byte[], CancellationToken, Task<byte[]>> Ask)> Rungs()
    {
        if (stream is not null)
        {
            yield return (OnStream, stream);
        }

        if (encrypted is not null)
        {
            yield return (OnEncrypted, encrypted);
        }
    }

    // The transport a query would take before any answer has settled it.
    private string Planned()
    {
        return _transport switch
        {
            DnsTransports.Doh when encrypted is not null => OnEncrypted,
            _ => OnPlain,
        };
    }

    private void Took(string carrying, string reason)
    {
        lock (_sync)
        {
            if (_carrying == carrying)
            {
                return;
            }

            _carrying = carrying;
            _reason = reason;
        }

        report?.Invoke(reason.Length > 0
            ? $"names behind the tunnel now leave over {carrying}, because {reason}"
            : $"names behind the tunnel leave over {carrying}");
    }

    private static string Short(Exception ex)
    {
        return ex.Message.Split('\n')[0].Trim();
    }
}
