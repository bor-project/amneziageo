using AmneziaGeo.Ipc;

namespace AmneziaGeo.Linux.Cli;

/// <summary>
/// Turns an agent reply into console output and an exit code.
/// </summary>
internal static class Reply
{
    /// <summary>
    /// Resource key the agent answers with for a command it does not implement.
    /// </summary>
    private const string _notWiredKey = "Linux_OpNotWired";

    /// <summary>
    /// Prints the reply and returns the exit code that matches it.
    /// </summary>
    public static int Report(IpcAck ack, string? done = null)
    {
        var unsupported = IpcMessage.TryParse(ack.Message, out var key, out _) && key == _notWiredKey;
        var text = AgentClient.Localize(ack.Message);

        if (Output.Json)
        {
            Output.AsJson(new { ok = ack.Ok, message = text });
        }
        else if (!ack.Ok)
        {
            Output.Error(text.Length > 0 ? text : "the agent refused the command");
        }
        else
        {
            Output.Info(text.Length > 0 ? text : done ?? "done");
        }

        return ack.Ok ? Exit.Ok : unsupported ? Exit.Unsupported : Exit.Failed;
    }

    /// <summary>
    /// Prints a payload reply verbatim and returns the exit code that matches it.
    /// </summary>
    public static int Payload(IpcAck ack)
    {
        if (!ack.Ok)
        {
            return Report(ack);
        }

        Output.Line(ack.Message);
        return Exit.Ok;
    }

    /// <summary>
    /// Reports a usage problem.
    /// </summary>
    public static int Usage(string message)
    {
        Output.Error(message);
        return Exit.Usage;
    }
}
