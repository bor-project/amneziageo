using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Reads a failed request down to what the network did to it.
/// </summary>
public static partial class NetworkFailure
{
    /// <summary>
    /// The cause in a few words, or null when the failure is not the network's.
    /// </summary>
    public static string? Describe(Exception error)
    {
        var connection = false;
        var innermost = error;
        for (var one = error; one is not null; one = one.InnerException)
        {
            innermost = one;
            var cause = Cause(one);
            if (cause is not null)
            {
                return cause;
            }

            connection |= one is HttpRequestException or IOException or SocketException;
        }

        if (error is HttpRequestException { StatusCode: { } status })
        {
            return $"the server answered {(int)status}";
        }

        return connection ? $"the connection failed: {FirstLine(innermost.Message)}" : null;
    }

    // What one link of the chain says about the network; null when it names nothing known.
    private static string? Cause(Exception one)
    {
        var message = one.Message ?? string.Empty;
        var host = HostPattern().Match(message);
        if (host.Success)
        {
            var code = CodePattern().Match(Chain(one));
            return code.Success
                ? $"the name {host.Groups[1].Value} did not resolve ({code.Value})"
                : $"the name {host.Groups[1].Value} did not resolve";
        }

        switch (one)
        {
            case SocketException socket:
                return Socket(socket.SocketErrorCode);
            case TimeoutException:
                return "timed out";
            case AuthenticationException:
                return "the secure connection failed";
        }

        var name = one.GetType().Name;
        if (name.EndsWith("SocketTimeoutException", StringComparison.Ordinal)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "timed out";
        }

        if (name.StartsWith("SSL", StringComparison.Ordinal)
            || message.Contains("SSL handshake", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Trust anchor", StringComparison.Ordinal))
        {
            return "the secure connection failed";
        }

        if (message.Contains("ECONNREFUSED", StringComparison.Ordinal))
        {
            return "the connection was refused";
        }

        if (message.Contains("ENETUNREACH", StringComparison.Ordinal) || message.Contains("EHOSTUNREACH", StringComparison.Ordinal))
        {
            return "no route to the server";
        }

        if (message.Contains("ECONNRESET", StringComparison.Ordinal) || message.Contains("Connection reset", StringComparison.Ordinal))
        {
            return "the connection was reset";
        }

        return null;
    }

    private static string Socket(SocketError code)
    {
        return code switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "the name did not resolve",
            SocketError.TimedOut => "timed out",
            SocketError.ConnectionRefused => "the connection was refused",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable => "no route to the server",
            SocketError.ConnectionReset => "the connection was reset",
            _ => $"the connection failed ({code})",
        };
    }

    // Every message down the chain: the resolver code sits below the line that names the host.
    private static string Chain(Exception error)
    {
        var text = new StringBuilder();
        for (var one = error; one is not null; one = one.InnerException)
        {
            text.Append(one.Message).Append('\n');
        }

        return text.ToString();
    }

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }

    [GeneratedRegex("resolve host \"([^\"]+)\"")]
    private static partial Regex HostPattern();

    [GeneratedRegex("EAI_[A-Z]+")]
    private static partial Regex CodePattern();
}
