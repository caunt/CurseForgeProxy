using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CurseForgeProxy.Tests;

public sealed class CurseForgeProxyTests
{
    [Fact]
    public async Task CurseForgeProxyWithoutAnyApiKeyReturnsBadRequest()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", null);

        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Missing x-api-key header", body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyForwardsRequestPathWithoutPrefix()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            var handler = new CapturingHandler();
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                }));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/v1/mods/search?gameId=432");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(handler.Request);
            Assert.Equal("/v1/mods/search", handler.Request.RequestUri?.AbsolutePath);
            Assert.Equal("?gameId=432", handler.Request.RequestUri?.Query);
            Assert.Equal("api.curseforge.com", handler.Request.Headers.Host);
            Assert.True(handler.Request.Headers.Contains("x-api-key"));
            AssertNoConnectionHeader(handler);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyRetriesForbiddenResponses()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            var handler = new SequencedHandler(
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("forbidden")
                },
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                });

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                }));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/v1/mods/search");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{}", body);
            Assert.Equal(2, handler.RequestCount);
            AssertDistinctCloudFrontAddresses(handler, expectedCount: 2);
            AssertNoConnectionHeader(handler);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyReturnsForbiddenAfterMaxRetries()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            using var loggerProvider = new CapturingLoggerProvider();
            var handler = new SequencedHandler(
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("forbidden-1")
                },
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("forbidden-2")
                },
                new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("forbidden-3")
                });

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddProvider(loggerProvider);
                    });

                    builder.ConfigureTestServices(services =>
                    {
                        services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                    });
                });
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/v1/mods/search");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("forbidden-3", body);
            Assert.Equal(3, handler.RequestCount);
            AssertDistinctCloudFrontAddresses(handler, expectedCount: 3);
            AssertNoConnectionHeader(handler);
            Assert.Contains(loggerProvider.Entries, entry =>
                entry.CategoryName == typeof(CurseForgeEndpoints).FullName &&
                entry.LogLevel == LogLevel.Warning &&
                entry.Message.Contains("Upstream request failed after status 403 on attempt 3/3", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyRetriesConnectionFailuresOnDifferentCloudFrontAddresses()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            var handler = new SequencedHandler(
                () => throw new SocketException((int)SocketError.ConnectionReset),
                () => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                });

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                }));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/v1/mods/search");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{}", body);
            Assert.Equal(2, handler.RequestCount);
            AssertDistinctCloudFrontAddresses(handler, expectedCount: 2);
            AssertNoConnectionHeader(handler);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyReturnsGatewayForConnectionFailureAfterMaxRetries()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            using var loggerProvider = new CapturingLoggerProvider();
            var handler = new SequencedHandler(
                () => throw new SocketException((int)SocketError.ConnectionReset),
                () => throw new SocketException((int)SocketError.ConnectionReset),
                () => throw new SocketException((int)SocketError.ConnectionReset));

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddProvider(loggerProvider);
                    });

                    builder.ConfigureTestServices(services =>
                    {
                        services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                    });
                });
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/v1/mods/search");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Equal("Upstream connection was reset.", body);
            Assert.Equal(3, handler.RequestCount);
            AssertDistinctCloudFrontAddresses(handler, expectedCount: 3);
            AssertNoConnectionHeader(handler);
            Assert.Contains(loggerProvider.Entries, entry =>
                entry.CategoryName == typeof(CurseForgeEndpoints).FullName &&
                entry.LogLevel == LogLevel.Warning &&
                entry.Exception is SocketException &&
                entry.Message.Contains("Upstream request failed after connection failure on attempt 3/3", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyForwardsFallbackDownloadToWwwCurseForge()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            var handler = new CapturingHandler();
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                }));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/v1/mods/783522/files/7842394/download");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(handler.Request);
            Assert.Equal("/api/v1/mods/783522/files/7842394/download", handler.Request.RequestUri?.AbsolutePath);
            Assert.Equal("www.curseforge.com", handler.Request.Headers.Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyPassesThroughRedirectResponses()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            var handler = new SequencedHandler(
                new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://edge.forgecdn.net/files/mod.jar") },
                    Content = new StringContent(string.Empty)
                });

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                }));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            using var response = await client.GetAsync("/v1/mods/783522/files/7842394/download");

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal("https://edge.forgecdn.net/files/mod.jar", response.Headers.Location?.ToString());
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyReturnsGatewayTimeoutForUpstreamTimeout()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            var handler = new TimeoutHandler();
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                }));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/v1/mods/search");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            Assert.Equal("Upstream request timed out.", body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyForwardsNonDownloadFilePathToApiCurseForge()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            var handler = new CapturingHandler();
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                }));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/v1/mods/123/files/456");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(handler.Request);
            Assert.Equal("/v1/mods/123/files/456", handler.Request.RequestUri?.AbsolutePath);
            Assert.Equal("api.curseforge.com", handler.Request.Headers.Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyPreservesQueryStringOnFallbackDownload()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            var handler = new CapturingHandler();
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                }));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/v1/mods/123/files/456/download?token=abc");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(handler.Request);
            Assert.Equal("/api/v1/mods/123/files/456/download", handler.Request.RequestUri?.AbsolutePath);
            Assert.Equal("?token=abc", handler.Request.RequestUri?.Query);
            Assert.Equal("www.curseforge.com", handler.Request.Headers.Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    [Fact]
    public async Task CurseForgeProxyPreservesQueryStringOnNonDownloadPath()
    {
        var originalApiKey = Environment.GetEnvironmentVariable("CURSEFORGE_API_KEY");
        Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", "test-api-key");

        try
        {
            var handler = new CapturingHandler();
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton<IHttpClientFactory>(new CapturingHttpClientFactory(handler));
                }));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/v1/mods/search?gameId=432&pageSize=10");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(handler.Request);
            Assert.Equal("/v1/mods/search", handler.Request.RequestUri?.AbsolutePath);
            Assert.Equal("?gameId=432&pageSize=10", handler.Request.RequestUri?.Query);
            Assert.Equal("api.curseforge.com", handler.Request.Headers.Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }

    private sealed class CapturingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public bool? RequestConnectionClose { get; private set; }

        public string[] RequestConnectionHeaderValues { get; private set; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            RequestConnectionClose = request.Headers.ConnectionClose;
            RequestConnectionHeaderValues = GetHeaderValues(request, "Connection");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }

    private static void AssertNoConnectionHeader(CapturingHandler handler)
    {
        Assert.False(handler.RequestConnectionClose == true);
        Assert.Empty(handler.RequestConnectionHeaderValues);
    }

    private static void AssertDistinctCloudFrontAddresses(SequencedHandler handler, int expectedCount)
    {
        Assert.Equal(expectedCount, handler.RequestUris.Count);
        Assert.Equal(
            expectedCount,
            handler.RequestUris
                .Select(uri => uri.Host)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    private static void AssertNoConnectionHeader(SequencedHandler handler)
    {
        Assert.Equal(handler.RequestCount, handler.RequestConnectionCloseValues.Count);
        Assert.Equal(handler.RequestCount, handler.RequestConnectionHeaderValues.Count);
        Assert.All(handler.RequestConnectionCloseValues, connectionClose => Assert.False(connectionClose == true));

        foreach (var connectionHeaderValues in handler.RequestConnectionHeaderValues)
            Assert.Empty(connectionHeaderValues);
    }

    private static string[] GetHeaderValues(HttpRequestMessage request, string headerName)
    {
        return request.Headers.TryGetValues(headerName, out var values) ? values.ToArray() : [];
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> responsesQueue;

        public SequencedHandler(params HttpResponseMessage[] responses)
            : this(responses.Select<HttpResponseMessage, Func<HttpResponseMessage>>(response => () => response).ToArray())
        {
        }

        public SequencedHandler(params Func<HttpResponseMessage>[] responses)
        {
            responsesQueue = new Queue<Func<HttpResponseMessage>>(responses);
        }

        public List<Uri> RequestUris { get; } = [];

        public List<bool?> RequestConnectionCloseValues { get; } = [];

        public List<string[]> RequestConnectionHeaderValues { get; } = [];

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri is not null)
                RequestUris.Add(request.RequestUri);

            RequestConnectionCloseValues.Add(request.Headers.ConnectionClose);
            RequestConnectionHeaderValues.Add(GetHeaderValues(request, "Connection"));

            if (responsesQueue.Count == 0)
                throw new InvalidOperationException("No more queued responses are available.");

            return Task.FromResult(responsesQueue.Dequeue()());
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IEnumerable<LogEntry> Entries => entries;

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(categoryName, entries);
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(
            string categoryName,
            ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
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

    private sealed record LogEntry(
        string CategoryName,
        LogLevel LogLevel,
        Exception? Exception,
        string Message);

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException();
        }
    }
}
