using System.Globalization;
using System.Text.Json;
using AmneziaGeo.Decl;
using AmneziaGeo.Ipc;

namespace AmneziaGeo.Cli;

/// <summary>
/// Manages transport proxies and configuration selections.
/// </summary>
internal static class TransportProxyCommands
{
    /// <summary>
    /// Runs one transport proxy command.
    /// </summary>
    public static async Task<int> RunAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return Reply.Usage("usage: amneziageo transport-proxy <list|showlocal|save|remove|probe>");
        }

        var rest = (IReadOnlyList<string>)[.. args.Skip(1)];
        return args[0] switch
        {
            "list" => rest.Count == 0
                ? await ListAsync(agent).ConfigureAwait(false)
                : Reply.Usage("usage: amneziageo transport-proxy list"),
            "showlocal" => await OneAsync(agent, IpcContract.OpGetTransportProxy, rest).ConfigureAwait(false),
            "save" => await SaveAsync(agent, rest).ConfigureAwait(false),
            "remove" => await OneAsync(agent, IpcContract.OpRemoveTransportProxy, rest).ConfigureAwait(false),
            "probe" => await OneAsync(agent, IpcContract.OpProbeTransportProxy, rest).ConfigureAwait(false),
            _ => Reply.Usage($"unknown transport-proxy command '{args[0]}'"),
        };
    }

    /// <summary>
    /// Selects a proxy or direct UDP for one configuration.
    /// </summary>
    public static async Task<int> SelectAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        if (args.Count != 2)
        {
            return Reply.Usage("usage: amneziageo config proxy <name> none|<source/id>");
        }

        var direct = string.Equals(args[1], "none", StringComparison.OrdinalIgnoreCase);
        var reference = direct ? null : ParseReference(args[1]);
        if (!direct && reference is null)
        {
            return Reply.Usage("a proxy reference is source/id; use transport-proxy list");
        }

        return Reply.Report(await agent.SendAsync(IpcContract.OpSelectTransportProxy, args[0],
            JsonSerializer.Serialize(reference, IpcJson.Options)).ConfigureAwait(false));
    }

    private static async Task<int> ListAsync(IAgentLink agent)
    {
        var ack = await agent.SendAsync(IpcContract.OpListTransportProxies).ConfigureAwait(false);
        if (!ack.Ok)
        {
            return Reply.Report(ack);
        }

        try
        {
            var proxies = JsonSerializer.Deserialize<TransportProxySummary[]>(ack.Message, IpcJson.Options);
            if (proxies is null || proxies.Any(one => one is null))
            {
                return InvalidReply();
            }

            if (Output.Json)
            {
                Output.AsJson(proxies);
                return Exit.Ok;
            }

            Output.Table(["REFERENCE", "NAME", "TYPE", "ADDRESS", "SOURCE", "STATE", "FAULT"],
                [.. proxies.Select(one => (IReadOnlyList<string>)
                [
                    ReferenceText(one.Reference), one.Name, one.Transport == "http" ? "HTTP " + one.Http?.Method : one.Transport == "quic" ? "QUIC" : one.Transport,
                    Address(one.Host, one.Port), one.Local ? "local" : string.Join(", ", one.Sources),
                    one.Missing ? "missing" : one.Availability.ToString().ToLowerInvariant(), one.Fault,
                ])], "no transport proxies yet");
            return Exit.Ok;
        }
        catch (JsonException)
        {
            return InvalidReply();
        }
    }

    private static async Task<int> SaveAsync(IAgentLink agent, IReadOnlyList<string> args)
    {
        var flags = Flags.Parse(args, "stdin");
        if (!flags.Allowed("file", "stdin") || flags.Positional.Count > 1)
        {
            return Reply.Usage(flags.Error ?? "usage: amneziageo transport-proxy save [<local-id>] (--file <path> | --stdin)");
        }

        var id = flags.Positional.Count == 0 ? "" : flags.Positional[0];
        if (id.Length > 0 && !ValidId(id))
        {
            return Reply.Usage("local-id contains letters, digits, '-' or '_'");
        }

        if (!TextInput.TryRead(flags, out var text, out var error))
        {
            return Reply.Usage(error);
        }

        try
        {
            var draft = JsonSerializer.Deserialize<OfferedProxy>(text, IpcJson.Options);
            if (draft is null)
            {
                return Reply.Usage("the proxy JSON must contain a proxy definition");
            }

            var ack = await agent.SendAsync(IpcContract.OpSaveTransportProxy, id,
                JsonSerializer.Serialize(draft, IpcJson.Options)).ConfigureAwait(false);
            if (!ack.Ok)
            {
                return Reply.Report(ack);
            }

            var reference = JsonSerializer.Deserialize<ProxyReference>(ack.Message, IpcJson.Options);
            if (reference is null)
            {
                return InvalidReply();
            }

            if (Output.Json)
            {
                Output.AsJson(reference);
            }
            else
            {
                Output.Line(ReferenceText(reference));
            }

            return Exit.Ok;
        }
        catch (JsonException)
        {
            return Reply.Usage("invalid proxy JSON");
        }
    }

    private static async Task<int> OneAsync(IAgentLink agent, string operation, IReadOnlyList<string> args)
    {
        if (args.Count != 1 || ParseReference(args[0]) is not { } reference)
        {
            return Reply.Usage("a proxy reference is source/id; use transport-proxy list");
        }

        var ack = await agent.SendAsync(operation, JsonSerializer.Serialize(reference, IpcJson.Options)).ConfigureAwait(false);
        if (!ack.Ok || operation == IpcContract.OpRemoveTransportProxy)
        {
            return Reply.Report(ack);
        }

        try
        {
            if (operation == IpcContract.OpGetTransportProxy)
            {
                var local = JsonSerializer.Deserialize<CatalogTransportProxy>(ack.Message, IpcJson.Options);
                if (local is not { Local: true })
                {
                    return InvalidReply();
                }

                Output.AsJson(local.Proxy);
            }
            else
            {
                var probe = JsonSerializer.Deserialize<TransportProxyProbeResult>(ack.Message, IpcJson.Options);
                if (probe is null)
                {
                    return InvalidReply();
                }

                if (Output.Json)
                {
                    Output.AsJson(probe);
                }
                else
                {
                    Output.Pairs([("state", probe.Availability.ToString().ToLowerInvariant()), ("fault", probe.Fault),
                        ("checked", probe.Checked?.ToString("O", CultureInfo.InvariantCulture) ?? "")]);
                }
            }

            return Exit.Ok;
        }
        catch (JsonException)
        {
            return InvalidReply();
        }
    }

    private static ProxyReference? ParseReference(string text)
    {
        var slash = text.IndexOf('/');
        return slash > 0 && Guid.TryParse(text[..slash], out var source) && source != Guid.Empty && ValidId(text[(slash + 1)..])
            ? new ProxyReference(source.ToString("D"), text[(slash + 1)..]) : null;
    }

    private static bool ValidId(string id) => id is { Length: > 0 and <= 64 }
        && id.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '-' or '_');

    private static string ReferenceText(ProxyReference reference) => reference.Source + "/" + reference.Id;

    private static string Address(string host, int port) =>
        string.Create(CultureInfo.InvariantCulture, $"{(host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host)}:{port}");

    private static int InvalidReply()
    {
        Output.Error("the agent returned an invalid transport proxy reply");
        return Exit.Failed;
    }
}
