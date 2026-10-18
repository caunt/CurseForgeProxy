namespace CurseForgeProxy.Tests;

/// <summary>
/// Checks that writes through an upstream connection stream are credited to the attempt running on the writing async flow.
/// </summary>
public sealed class UpstreamConnectionStreamTests
{
    private static readonly byte[] RequestBytes = "GET / HTTP/1.1\r\nHost: api.curseforge.com\r\n\r\n"u8.ToArray();

    /// <summary>
    /// A fresh attempt reports no connection until a request is written through the wrapped stream on its own flow.
    /// </summary>
    [Fact]
    public async Task UpstreamConnectionStreamCreditsWritesToCurrentAttempt()
    {
        UpstreamAttempt attempt = UpstreamAttempt.Start();

        using MemoryStream inner = new();

        using UpstreamConnectionStream stream = new(inner);

        Assert.False(attempt.HasConnection);

        await stream.WriteAsync(RequestBytes);

        Assert.True(attempt.HasConnection);
        Assert.Equal(RequestBytes, inner.ToArray());
    }

    /// <summary>
    /// A write made while a child flow runs its own attempt is credited to that child attempt and never to the parent's.
    /// </summary>
    [Fact]
    public async Task UpstreamConnectionStreamIgnoresAttemptsOfOtherFlows()
    {
        UpstreamAttempt parentAttempt = UpstreamAttempt.Start();

        using MemoryStream inner = new();

        using UpstreamConnectionStream stream = new(inner);

        bool childHasConnection = await Task.Run(() => WriteUnderChildAttempt(stream));

        Assert.True(childHasConnection);
        Assert.False(parentAttempt.HasConnection);
    }

    private static bool WriteUnderChildAttempt(Stream stream)
    {
        UpstreamAttempt childAttempt = UpstreamAttempt.Start();

        stream.Write(RequestBytes);

        return childAttempt.HasConnection;
    }
}
