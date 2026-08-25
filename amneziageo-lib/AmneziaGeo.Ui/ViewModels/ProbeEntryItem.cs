using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Names the tokens a probe is written with in the reader's language. The journal itself stays English, so
/// what is copied, exported and sent for support reads the same everywhere; only the card is translated.
/// </summary>
internal static class ProbeWords
{
    /// <summary>
    /// The path a run was asked for.
    /// </summary>
    public static string Path(string token)
    {
        return token switch
        {
            ProbePaths.Auto => Loc.Instance.Get("Probe_PathAuto"),
            ProbePaths.Tunnel => Loc.Instance.Get("Probe_PathTunnel"),
            ProbePaths.Bypass => Loc.Instance.Get("Probe_PathBypass"),
            _ => token,
        };
    }

    /// <summary>
    /// Where the routing in force sent the destination.
    /// </summary>
    public static string Taken(string phrase)
    {
        return phrase switch
        {
            "tunnel by rule" => Loc.Instance.Get("Probe_TakenTunnelRule"),
            "tunnel by default" => Loc.Instance.Get("Probe_TakenTunnelDefault"),
            "bypass by rule" => Loc.Instance.Get("Probe_TakenBypassRule"),
            "bypass by default" => Loc.Instance.Get("Probe_TakenBypassDefault"),
            "blocked by rule" => Loc.Instance.Get("Probe_TakenBlocked"),
            "bypass, no live routing to ask" => Loc.Instance.Get("Probe_TakenNoRouting"),
            _ => phrase,
        };
    }

    /// <summary>
    /// The leg of a probe.
    /// </summary>
    public static string Leg(string name)
    {
        return name switch
        {
            ProbeLegs.Reach => Loc.Instance.Get("Probe_LegReach"),
            ProbeLegs.Receive => Loc.Instance.Get("Probe_LegReceive"),
            ProbeLegs.Send => Loc.Instance.Get("Probe_LegSend"),
            _ => name,
        };
    }

    /// <summary>
    /// The verdict over the legs.
    /// </summary>
    public static string Verdict(string sentence)
    {
        var (key, args) = ProbePhrase.Read(sentence);
        if (key.Length == 0)
        {
            return sentence;
        }

        if (key == ProbeVerdicts.PathUnavailable)
        {
            return Loc.Instance.Get(key, Side(args.Count > 0 ? args[0] : string.Empty));
        }

        return key == ProbeVerdicts.Measured && args.Count < 3
            ? Loc.Instance.Get("Probe_MeasuredPlain", [.. args])
            : Loc.Instance.Get(key, [.. args]);
    }

    /// <summary>
    /// What a leg measured; a phrase with no name of its own goes on as it stands.
    /// </summary>
    public static string Detail(string text)
    {
        var parts = new List<string>();
        var rest = text;
        while (rest.Length > 0)
        {
            if (Said(rest) is { } said)
            {
                parts.Add(said.Text);
                rest = rest[said.Length..].TrimStart(' ', ',');
                continue;
            }

            var stop = rest.IndexOf(", ", StringComparison.Ordinal);
            if (stop < 0)
            {
                parts.Add(rest);
                break;
            }

            parts.Add(rest[..stop]);
            rest = rest[(stop + 2)..];
        }

        return string.Join(", ", parts);
    }

    // Which side of the tunnel a refused run asked for.
    private static string Side(string word)
    {
        return word switch
        {
            "through" => Loc.Instance.Get("Probe_SideThrough"),
            "past" => Loc.Instance.Get("Probe_SidePast"),
            _ => word,
        };
    }

    // The phrase the text opens with, named in the reader's language.
    private static (string Text, int Length)? Said(string text)
    {
        foreach (var (pattern, key) in _phrases)
        {
            var match = pattern.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var args = match.Groups.Values.Skip(1).Select(group => (object?)group.Value).ToArray();
            return (Loc.Instance.Get(key, args), match.Length);
        }

        return null;
    }

    // What a leg says, tried in this order: a phrase carrying a comma comes before the parts around it.
    private static readonly (Regex Pattern, string Key)[] _phrases =
    [
        (new Regex(@"^handed over ([^,]+), too little to time"), "Probe_NoteThin"),
        (new Regex(@"^handed over nothing to time"), "Probe_NoteNothing"),
        (new Regex(@"^rx ([^,]+), tx (\S+)"), "Probe_MetricTraffic"),
        (new Regex(@"^rtt (\S+) ms"), "Probe_MetricRtt"),
        (new Regex(@"^jitter (\S+) ms"), "Probe_MetricJitter"),
        (new Regex(@"^loss (\S+)%"), "Probe_MetricLoss"),
        (new Regex(@"^(\S+)-byte packets pass"), "Probe_MetricPackets"),
        (new Regex(@"^age (\S+) s"), "Probe_MetricAge"),
        (new Regex(@"^(\S+) rekey\(s\) per minute"), "Probe_MetricRekeys"),
        (new Regex(@"^(\S+) Mbit/s"), "Probe_MetricRate"),
        (new Regex(@"^against (\S+)"), "Probe_NoteAgainst"),
        (new Regex(@"^the name does not resolve"), "Probe_NoteNoResolve"),
        (new Regex(@"^(\S+) answers on (\S+)"), "Probe_NoteAnswersOn"),
        (new Regex(@"^(\S+) accepted no connection"), "Probe_NoteNoConnection"),
        (new Regex(@"^(\S+) never answered"), "Probe_NoteNeverAnswered"),
        (new Regex(@"^not an address"), "Probe_NoteNotAddress"),
        (new Regex(@"^no address to probe"), "Probe_NoteNoAddress"),
        (new Regex(@"^the download never started"), "Probe_NoteNoDownload"),
        (new Regex(@"^the upload never started"), "Probe_NoteNoUpload"),
        (new Regex(@"^took nothing to time"), "Probe_NoteNoTime"),
    ];

    /// <summary>
    /// How a leg came out.
    /// </summary>
    public static string State(string state)
    {
        return state switch
        {
            LegState.Ok => Loc.Instance.Get("Probe_StateOk"),
            LegState.Weak => Loc.Instance.Get("Probe_StateWeak"),
            LegState.Bad => Loc.Instance.Get("Probe_StateBad"),
            LegState.Unknown => Loc.Instance.Get("Probe_StateUnknown"),
            LegState.Skipped => Loc.Instance.Get("Probe_StateSkipped"),
            _ => state,
        };
    }
}

/// <summary>
/// One leg of a probe as a narrow window shows it: what was measured, how it came out and what it found.
/// </summary>
internal sealed record ProbeLegItem(string Name, string State, string Detail)
{
    /// <summary>
    /// The leg named in the reader's language.
    /// </summary>
    public string NameText => ProbeWords.Leg(Name);

    /// <summary>
    /// How the leg came out, in the reader's language.
    /// </summary>
    public string StateText => ProbeWords.State(State);

    /// <summary>
    /// Whether the leg carries what is put into it.
    /// </summary>
    public bool IsGood => State == LegState.Ok;

    /// <summary>
    /// Whether the leg works and pays for it.
    /// </summary>
    public bool IsWeak => State == LegState.Weak;

    /// <summary>
    /// Whether the leg is where the traffic dies.
    /// </summary>
    public bool IsBad => State == LegState.Bad;

    /// <summary>
    /// What the leg measured, in the reader's language.
    /// </summary>
    public string DetailText => ProbeWords.Detail(Detail);

    /// <summary>
    /// Whether the leg found anything to say.
    /// </summary>
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// One probe of the journal as a narrow window shows it: the destination, the path it was measured over, what
/// each leg found and the verdict over them. The block is rendered as a padded table, which no phone reads, so
/// it is taken apart here and laid out again.
/// </summary>
internal sealed record ProbeEntryItem(
    string Time,
    string Target,
    string Path,
    string Taken,
    string Verdict,
    IReadOnlyList<ProbeLegItem> Legs,
    string Line)
{
    /// <summary>
    /// The path asked for and where the routing sent it, in the reader's language.
    /// </summary>
    public string PathText => Taken.Length > 0
        ? ProbeWords.Path(Path) + " → " + ProbeWords.Taken(Taken)
        : ProbeWords.Path(Path);

    /// <summary>
    /// The verdict over the legs, in the reader's language.
    /// </summary>
    public string VerdictText => ProbeWords.Verdict(Verdict);

    /// <summary>
    /// Whether a leg died or the destination never answered.
    /// </summary>
    public bool Alarm => Legs.Any(leg => leg.State == LegState.Bad)
        || (Legs.Count > 0 && Legs[0].State == LegState.Unknown);

    /// <summary>
    /// Whether a leg pays for what it carries, says nothing, or the run never happened.
    /// </summary>
    public bool Warning => !Alarm && (Legs.Count == 0 || Legs.Any(leg => leg.State != LegState.Ok));

    /// <summary>
    /// Whether the probe names a path.
    /// </summary>
    public bool HasPath => Path.Length > 0;

    /// <summary>
    /// Whether the probe carries a verdict.
    /// </summary>
    public bool HasVerdict => Verdict.Length > 0;

    /// <inheritdoc/>
    public bool Equals(ProbeEntryItem? other)
    {
        return other is not null && string.Equals(Line, other.Line, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return Line.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a probe back from its journal block: the head names the destination and the path, each row under
    /// it is a leg, and the last one is the verdict. A block shaped otherwise goes in whole.
    /// </summary>
    public static ProbeEntryItem Parse(string line)
    {
        var rows = line.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var head = LogEntryItem.Parse(rows[0]);
        if (!head.Text.StartsWith("probe ", StringComparison.Ordinal))
        {
            return new ProbeEntryItem(head.Time, head.Text, string.Empty, string.Empty, string.Empty, [], line);
        }

        var (target, path, taken) = Head(head.Text[6..]);
        var legs = new List<ProbeLegItem>();
        var verdict = string.Empty;
        foreach (var row in rows.Skip(1))
        {
            var text = row.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var (name, rest) = Word(text);
            if (name == "verdict")
            {
                verdict = rest;
                continue;
            }

            var (state, detail) = Word(rest);
            legs.Add(new ProbeLegItem(name, state, detail));
        }

        return new ProbeEntryItem(head.Time, target, path, taken, verdict, legs, line);
    }

    // The destination, the path asked for and the one taken; the stamp at the end is the row's own.
    private static (string Target, string Path, string Taken) Head(string text)
    {
        var at = text.LastIndexOf(" at ", StringComparison.Ordinal);
        var body = at > 0 ? text[..at] : text;
        var over = body.IndexOf(" over ", StringComparison.Ordinal);
        if (over <= 0)
        {
            return (body, string.Empty, string.Empty);
        }

        var target = body[..over];
        var rest = body[(over + 6)..];
        var arrow = rest.IndexOf(" -> ", StringComparison.Ordinal);
        return arrow > 0 ? (target, rest[..arrow], rest[(arrow + 4)..]) : (target, rest, string.Empty);
    }

    // Splits the first word off a row.
    private static (string First, string Tail) Word(string text)
    {
        var space = text.IndexOf(' ');
        return space > 0 ? (text[..space], text[(space + 1)..].TrimStart()) : (text, string.Empty);
    }
}
