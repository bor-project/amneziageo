namespace Java.Net;

/// <summary>
/// The socket failure of the Java network stack the Android handler carries out.
/// </summary>
internal sealed class SocketException(string message) : Exception(message);
