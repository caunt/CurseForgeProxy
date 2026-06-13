using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
