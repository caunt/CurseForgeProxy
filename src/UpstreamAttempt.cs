namespace CurseForgeProxy;

/// <summary>
/// Records whether one upstream attempt was ever written to an upstream connection, so a timeout can tell a wait for a free connection from a slow CloudFront response.
/// </summary>
/// <remarks>
/// The attempt flows with the calling async context: SocketsHttpHandler writes a request to its connection on that request's own flow, where <see cref="UpstreamConnectionStream" /> records the use.
/// </remarks>
internal sealed class UpstreamAttempt
{
    private static readonly AsyncLocal<UpstreamAttempt?> CurrentAttempt = new();

    public bool HasConnection { get; private set; }

    public static void RecordConnectionUse()
    {
        CurrentAttempt.Value?.HasConnection = true;
    }

    public static UpstreamAttempt Start()
    {
        UpstreamAttempt attempt = new();

        CurrentAttempt.Value = attempt;

        return attempt;
    }
}
