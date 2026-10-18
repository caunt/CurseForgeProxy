using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CurseForgeProxy.Tests;

/// <summary>
/// Exercises the proxy end to end through an in-memory test server, with fake message handlers standing in for CurseForge.
/// </summary>
public sealed class CurseForgeProxyTests
{
    private const string CloudFrontTrafficLimitForbiddenBody =
        "403 ERROR The request could not be satisfied. Request blocked. " +
        "There might be too much traffic or a configuration error. CloudFront";

    /// <summary>
    /// A plain 403 without CloudFront's traffic-limit page goes straight back to the caller after a single upstream attempt.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyDoesNotRetryNonCloudFrontForbiddenResponses()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using SequencedHandler handler = new(
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(content: "forbidden")
                },
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content: "{}")
                }
            );

            using ProxyApplicationFactory factory = new(handler);

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/search", UriKind.Relative));

            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(expected: "forbidden", body);
            Assert.Equal(expected: 1, handler.RequestCount);
            AssertDistinctCloudFrontAddresses(handler, expectedCount: 1);
            AssertNoConnectionHeader(handler);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// A mod file download URL is sent to www.curseforge.com under the /api prefix instead of the API host.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyForwardsFallbackDownloadToWwwCurseForge()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using CapturingHandler handler = new();

            using ProxyApplicationFactory factory = new(handler);

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/783522/files/7842394/download", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(handler.Request);
            Assert.Equal(expected: "/api/v1/mods/783522/files/7842394/download", handler.Request.RequestUri?.AbsolutePath);
            Assert.Equal(expected: "www.curseforge.com", handler.Request.Headers.Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// A file metadata path without the trailing /download segment keeps its path and goes to api.curseforge.com.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyForwardsNonDownloadFilePathToApiCurseForge()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using CapturingHandler handler = new();

            using ProxyApplicationFactory factory = new(handler);

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/123/files/456", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(handler.Request);
            Assert.Equal(expected: "/v1/mods/123/files/456", handler.Request.RequestUri?.AbsolutePath);
            Assert.Equal(expected: "api.curseforge.com", handler.Request.Headers.Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// An ordinary API request keeps its path and query, gains the configured x-api-key, and carries no Connection header upstream.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyForwardsRequestPathWithoutPrefix()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using CapturingHandler handler = new();

            using ProxyApplicationFactory factory = new(handler);

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/search?gameId=432", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(handler.Request);
            Assert.Equal(expected: "/v1/mods/search", handler.Request.RequestUri?.AbsolutePath);
            Assert.Equal(expected: "?gameId=432", handler.Request.RequestUri?.Query);
            Assert.Equal(expected: "api.curseforge.com", handler.Request.Headers.Host);
            Assert.True(handler.Request.Headers.Contains(name: "x-api-key"));
            AssertNoConnectionHeader(handler);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// The client factory never retires the upstream handler, so its connection pool lives as long as the process and only PooledConnectionLifetime rotates connections.
    /// </summary>
    [Fact]
    public void CurseForgeProxyKeepsUpstreamHandlerForProcessLifetime()
    {
        using WebApplicationFactory<Program> factory = new();

        HttpClientFactoryOptions options = factory.Services
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(CurseForgeEndpoints.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, options.HandlerLifetime);
    }

    /// <summary>
    /// An upstream 302 reaches the caller untouched, Location header included, instead of being followed by the proxy.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyPassesThroughRedirectResponses()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using SequencedHandler handler = new(
                new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri(uriString: "https://edge.forgecdn.net/files/mod.jar") },
                    Content = new StringContent(string.Empty)
                }
            );

            using ProxyApplicationFactory factory = new(handler);

            using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/783522/files/7842394/download", UriKind.Relative));

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(expected: "https://edge.forgecdn.net/files/mod.jar", response.Headers.Location?.ToString());
            Assert.Equal(expected: 1, handler.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// Query parameters survive the rewrite of a download URL to the website's /api route.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyPreservesQueryStringOnFallbackDownload()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using CapturingHandler handler = new();

            using ProxyApplicationFactory factory = new(handler);

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/123/files/456/download?token=abc", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(handler.Request);
            Assert.Equal(expected: "/api/v1/mods/123/files/456/download", handler.Request.RequestUri?.AbsolutePath);
            Assert.Equal(expected: "?token=abc", handler.Request.RequestUri?.Query);
            Assert.Equal(expected: "www.curseforge.com", handler.Request.Headers.Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// Several query parameters reach api.curseforge.com exactly as the caller sent them, order included.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyPreservesQueryStringOnNonDownloadPath()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using CapturingHandler handler = new();

            using ProxyApplicationFactory factory = new(handler);

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/search?gameId=432&pageSize=10", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(handler.Request);
            Assert.Equal(expected: "/v1/mods/search", handler.Request.RequestUri?.AbsolutePath);
            Assert.Equal(expected: "?gameId=432&pageSize=10", handler.Request.RequestUri?.Query);
            Assert.Equal(expected: "api.curseforge.com", handler.Request.Headers.Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// CloudFront's traffic-limit 403 page triggers a retry through another edge address, and the caller gets the successful second response.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyRetriesCloudFrontTrafficLimitForbiddenResponses()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using SequencedHandler handler = new(
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(CloudFrontTrafficLimitForbiddenBody)
                },
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content: "{}")
                }
            );

            using ProxyApplicationFactory factory = new(handler);

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/search", UriKind.Relative));

            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected: "{}", body);
            Assert.Equal(expected: 2, handler.RequestCount);
            AssertDistinctCloudFrontAddresses(handler, expectedCount: 2);
            AssertNoConnectionHeader(handler);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// A connection reset on the first edge address is retried once on another address before the response is returned.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyRetriesConnectionFailuresOnDifferentCloudFrontAddresses()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using SequencedHandler handler = new(
                static () => throw new SocketException((int)SocketError.ConnectionReset),
                static () => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content: "{}")
                }
            );

            using ProxyApplicationFactory factory = new(handler);

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/search", UriKind.Relative));

            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected: "{}", body);
            Assert.Equal(expected: 2, handler.RequestCount);
            AssertDistinctCloudFrontAddresses(handler, expectedCount: 2);
            AssertNoConnectionHeader(handler);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// When all three attempts hit CloudFront's traffic-limit page, the last 403 body is returned and a warning names the final attempt.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyReturnsForbiddenAfterMaximumRetries()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using CapturingLoggerProvider loggerProvider = new();

            using SequencedHandler handler = new(
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(CloudFrontTrafficLimitForbiddenBody + " retry-1")
                },
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(CloudFrontTrafficLimitForbiddenBody + " retry-2")
                },
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(CloudFrontTrafficLimitForbiddenBody + " retry-3")
                }
            );

            using ProxyApplicationFactory factory = new(handler, loggerProvider);

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/search", UriKind.Relative));

            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(CloudFrontTrafficLimitForbiddenBody + " retry-3", body);
            Assert.Equal(expected: 3, handler.RequestCount);
            AssertDistinctCloudFrontAddresses(handler, expectedCount: 3);
            AssertNoConnectionHeader(handler);
            Assert.Contains(
                loggerProvider.Entries,
                static entry => entry.CategoryName == typeof(CurseForgeEndpoints).FullName
                    && entry.LogLevel == LogLevel.Warning
                    && entry.Message.Contains(value: "Upstream request failed after status 403 on attempt 3/3", StringComparison.Ordinal)
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// Three consecutive connection resets end in 502 Bad Gateway, with a warning that carries the socket exception.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyReturnsGatewayForConnectionFailureAfterMaximumRetries()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using CapturingLoggerProvider loggerProvider = new();

            using SequencedHandler handler = new(
                static () => throw new SocketException((int)SocketError.ConnectionReset),
                static () => throw new SocketException((int)SocketError.ConnectionReset),
                static () => throw new SocketException((int)SocketError.ConnectionReset)
            );

            using ProxyApplicationFactory factory = new(handler, loggerProvider);

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/search", UriKind.Relative));

            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Equal(expected: "Upstream connection was reset.", body);
            Assert.Equal(expected: 3, handler.RequestCount);
            AssertDistinctCloudFrontAddresses(handler, expectedCount: 3);
            AssertNoConnectionHeader(handler);
            Assert.Contains(
                loggerProvider.Entries,
                static entry => entry.CategoryName == typeof(CurseForgeEndpoints).FullName
                    && entry.LogLevel == LogLevel.Warning
                    && entry.Exception is SocketException
                    && entry.Message.Contains(value: "Upstream request failed after connection failure on attempt 3/3", StringComparison.Ordinal)
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// An upstream that never answers is abandoned after the five-second attempt timeout with 504 Gateway Timeout.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyReturnsGatewayTimeoutForUpstreamTimeout()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: "test-api-key");

        try
        {
            using TimeoutHandler handler = new();

            using ProxyApplicationFactory factory = new(handler);

            using HttpClient client = factory.CreateClient();

            Stopwatch stopwatch = Stopwatch.StartNew();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/v1/mods/search", UriKind.Relative));

            stopwatch.Stop();
            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Equal(expected: "Upstream request timed out.", body);
            Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(seconds: 4), $"Expected a 5-second timeout, got {stopwatch.Elapsed}.");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(seconds: 15), $"Expected a 5-second timeout, got {stopwatch.Elapsed}.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    /// <summary>
    /// With neither a caller x-api-key header nor CURSEFORGE_API_KEY, the proxy answers 400 without contacting upstream.
    /// </summary>
    [Fact]
    public async Task CurseForgeProxyWithoutAnyApiKeyReturnsBadRequest()
    {
        string? originalApiKey = Environment.GetEnvironmentVariable(variable: "CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", value: null);

        try
        {
            using WebApplicationFactory<Program> factory = new();

            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/", UriKind.Relative));

            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(expectedSubstring: "Missing x-api-key header", body, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable: "CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    private static void AssertDistinctCloudFrontAddresses(SequencedHandler handler, int expectedCount)
    {
        Assert.Equal(expectedCount, handler.RequestUris.Count);
        Assert.Equal(expectedCount, handler.RequestUris.Select(static uri => uri.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static void AssertNoConnectionHeader(CapturingHandler handler)
    {
        Assert.False(handler.RequestConnectionClose.GetValueOrDefault());
        Assert.Empty(handler.RequestConnectionHeaderValues);
    }

    private static void AssertNoConnectionHeader(SequencedHandler handler)
    {
        Assert.Equal(handler.RequestCount, handler.RequestConnectionCloseValues.Count);
        Assert.Equal(handler.RequestCount, handler.RequestConnectionHeaderValues.Count);
        Assert.All(handler.RequestConnectionCloseValues, static connectionClose => Assert.False(connectionClose.GetValueOrDefault()));

        foreach (string[] connectionHeaderValues in handler.RequestConnectionHeaderValues)
            Assert.Empty(connectionHeaderValues);
    }

    private static string[] GetHeaderValues(HttpRequestMessage request, string headerName)
    {
        return request.Headers.TryGetValues(headerName, out IEnumerable<string>? values) ? [.. values] : [];
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public bool? RequestConnectionClose { get; private set; }

        public string[] RequestConnectionHeaderValues { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestConnectionClose = request.Headers.ConnectionClose;
            RequestConnectionHeaderValues = GetHeaderValues(request, headerName: "Connection");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content: "{}") });
        }
    }

    private sealed class CapturingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IEnumerable<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(categoryName, _entries);
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string categoryName, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue(new LogEntry(categoryName, logLevel, exception, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record LogEntry(string CategoryName, LogLevel LogLevel, Exception? Exception, string Message);

    private sealed class ProxyApplicationFactory(HttpMessageHandler handler, ILoggerProvider? loggerProvider = null) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            return base.CreateHost(builder.ConfigureServices(RegisterTestServices));
        }

        private void RegisterTestServices(IServiceCollection services)
        {
            services.Add(ServiceDescriptor.Singleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler)));

            if (loggerProvider is not null)
                services.RemoveAll<ILoggerProvider>().Add(ServiceDescriptor.Singleton(loggerProvider));
        }
    }

    private sealed class SequencedHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responsesQueue = new(responses);

        public SequencedHandler(params HttpResponseMessage[] responses)
            : this(responses.Select<HttpResponseMessage, Func<HttpResponseMessage>>(response => () => response).ToArray())
        {
        }

        public List<Uri> RequestUris { get; } = [];

        public List<bool?> RequestConnectionCloseValues { get; } = [];

        public int RequestCount { get; private set; }

        public List<string[]> RequestConnectionHeaderValues { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            if (request.RequestUri is not null)
                RequestUris.Add(request.RequestUri);

            RequestConnectionCloseValues.Add(request.Headers.ConnectionClose);
            RequestConnectionHeaderValues.Add(GetHeaderValues(request, headerName: "Connection"));

            return _responsesQueue.Count == 0
                ? throw new InvalidOperationException(message: "No more queued responses are available.")
                : Task.FromResult(_responsesQueue.Dequeue()());
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return WaitForTimeoutAsync(cancellationToken);
        }

        private static async Task<HttpResponseMessage> WaitForTimeoutAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            throw new InvalidOperationException(message: "Timeout handler should only complete by cancellation.");
        }
    }
}
