using AmneziaGeo.Windows.App;
using Xunit;

namespace AmneziaGeo.Tests;

/// <summary>
/// The ETW session of the agent lets go of what it holds once, however many times it is disposed.
/// </summary>
public sealed class TraceSessionTests
{
    [WindowsFact("the session is controlled through advapi32")]
    public void ASessionDisposedTwice_FreesItsBlockOnce()
    {
        var session = new TraceSession(0);

        session.Dispose();
        session.Dispose();
        session.Flush();
        session.Stop();
    }
}
