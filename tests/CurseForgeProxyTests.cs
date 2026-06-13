using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

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

            using var response = await client.GetAsync("/curseforge");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Missing x-api-key header", body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSEFORGE_API_KEY", originalApiKey);
        }
    }
}
