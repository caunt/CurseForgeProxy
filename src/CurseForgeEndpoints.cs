using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace CurseForgeProxy;

public sealed class CurseForgeEndpoints(EnvironmentConfiguration configuration, IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "curseforge-egress";

    private const string CurseForgeHost = "api.curseforge.com";
    private const string ApiKeyHeaderName = "x-api-key";

    private static readonly IPAddress[] CloudFrontAddresses =
    [
        IPAddress.Parse("2600:f0f0:601::"), // AWS AMAZON, GLOBAL
        IPAddress.Parse("2600:f0f0:602::"), // AWS AMAZON, GLOBAL
        IPAddress.Parse("2600:f0f0:603::"), // AWS AMAZON, GLOBAL
        IPAddress.Parse("2600:9000:2000::") // CloudFront / AWS global edge range
    ];

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Host",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Proxy-Connection",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    public IEndpointRouteBuilder ConfigureRoutes(IEndpointRouteBuilder endpoints)
    {
        endpoints.Map(pattern: "/", requestDelegate: ProxyAsync);
        endpoints.Map(pattern: "/{**path}", requestDelegate: ProxyAsync);
        return endpoints;
    }

    private async Task ProxyAsync(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey(ApiKeyHeaderName) &&
            string.IsNullOrWhiteSpace(configuration.CurseForgeApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync(
                "Missing x-api-key header and CURSEFORGE_API_KEY is not configured.",
                context.RequestAborted);
            return;
        }

        const int maxAttempts = 3;
        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var proxyRequest = CreateProxyRequest(context);

            HttpResponseMessage proxyResponse;
            try
            {
                proxyResponse = await httpClient.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsRetryableUpstreamFailure(exception))
            {
                if (attempt < maxAttempts)
                    continue;

                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync("Upstream connection was reset.", context.RequestAborted);
                return;
            }
            catch (HttpRequestException)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync("Upstream request failed.", context.RequestAborted);
                return;
            }

            using (proxyResponse)
            {
                context.Response.StatusCode = (int)proxyResponse.StatusCode;
                CopyResponseHeaders(context.Response, proxyResponse);

                await proxyResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            return;
        }
    }

    private HttpRequestMessage CreateProxyRequest(HttpContext context)
    {
        var request = context.Request;
        var targetUri = CreateTargetUri(request);
        var proxyRequest = new HttpRequestMessage(new HttpMethod(request.Method), targetUri);

        if (CanHaveBody(context))
            proxyRequest.Content = new StreamContent(request.Body);

        CopyRequestHeaders(request, proxyRequest);

        proxyRequest.Headers.Host = CurseForgeHost;
        proxyRequest.Headers.ConnectionClose = true;

        if (!request.Headers.ContainsKey(ApiKeyHeaderName))
            proxyRequest.Headers.TryAddWithoutValidation(ApiKeyHeaderName, configuration.CurseForgeApiKey);

        return proxyRequest;
    }

    private static Uri CreateTargetUri(HttpRequest request)
    {
        return new Uri(UriHelper.BuildAbsolute(
            scheme: Uri.UriSchemeHttps,
            host: new HostString(CloudFrontAddresses[0].ToString(), port: 443),
            pathBase: PathString.Empty,
            path: request.Path.HasValue ? request.Path : new PathString("/"),
            query: request.QueryString));
    }

    private static bool CanHaveBody(HttpContext context)
    {
        var request = context.Request;
        var bodyDetection = context.Features.Get<IHttpRequestBodyDetectionFeature>();

        return bodyDetection?.CanHaveBody ??
            (request.ContentLength > 0 || request.Headers.ContainsKey(HeaderNames.TransferEncoding));
    }

    private static void CopyRequestHeaders(HttpRequest request, HttpRequestMessage proxyRequest)
    {
        foreach (var header in request.Headers)
        {
            if (IsHopByHopHeader(header.Key))
                continue;

            var values = header.Value.ToArray();

            if (!proxyRequest.Headers.TryAddWithoutValidation(header.Key, values) && proxyRequest.Content is not null)
                proxyRequest.Content.Headers.TryAddWithoutValidation(header.Key, values);
        }
    }

    private static void CopyResponseHeaders(HttpResponse response, HttpResponseMessage proxyResponse)
    {
        foreach (var header in proxyResponse.Headers)
            CopyResponseHeader(response, header);

        foreach (var header in proxyResponse.Content.Headers)
            CopyResponseHeader(response, header);

        response.Headers.Remove(HeaderNames.TransferEncoding);
    }

    private static void CopyResponseHeader(HttpResponse response, KeyValuePair<string, IEnumerable<string>> header)
    {
        if (IsHopByHopHeader(header.Key))
            return;

        response.Headers[header.Key] = new StringValues([.. header.Value]);
    }

    private static bool IsHopByHopHeader(string headerName)
    {
        return HopByHopHeaders.Contains(headerName);
    }

    private static bool IsRetryableUpstreamFailure(Exception exception)
    {
        if (exception is HttpRequestException httpRequestException &&
            (httpRequestException.HttpRequestError == HttpRequestError.ConnectionError ||
             httpRequestException.HttpRequestError == HttpRequestError.SecureConnectionError))
            return true;

        var current = exception;
        while (current is not null)
        {
            if (current is SocketException socketException &&
                socketException.SocketErrorCode == SocketError.ConnectionReset)
                return true;
            current = current.InnerException;
        }
        return false;
    }
}
