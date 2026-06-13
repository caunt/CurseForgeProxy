using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace CurseForgeProxy;

public sealed class CurseForgeEndpoints(
    EnvironmentConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<CurseForgeEndpoints> logger)
{
    public const string HttpClientName = "curseforge-egress";

    private const string ApiCurseForgeHost = "api.curseforge.com";
    private const string WwwCurseForgeHost = "www.curseforge.com";
    private const string ApiKeyHeaderName = "x-api-key";

    // Matches /v1/mods/{modId}/files/{fileId}/download
    private static readonly System.Text.RegularExpressions.Regex FallbackDownloadPathPattern =
        new(@"^/v1/mods/\d+/files/\d+/download$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly IPAddress[] CloudFrontAddresses =
    [
        IPAddress.Parse("2600:f0f0:601::"), // AWS AMAZON, GLOBAL
        IPAddress.Parse("2600:f0f0:602::"), // AWS AMAZON, GLOBAL
        IPAddress.Parse("2600:f0f0:603::"), // AWS AMAZON, GLOBAL
        IPAddress.Parse("2600:9000:2000::") // CloudFront / AWS global edge range
    ];

    private static int nextCloudFrontAddressIndex;

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
        var cloudFrontStartIndex = GetCloudFrontStartIndex();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var cloudFrontAddress = SelectCloudFrontAddress(cloudFrontStartIndex, attempt);
            using var proxyRequest = CreateProxyRequest(context, cloudFrontAddress);

            logger.LogTrace(
                "Sending upstream request attempt {Attempt}/{MaxAttempts} to {TargetHost} via {CloudFrontAddress}.",
                attempt,
                maxAttempts,
                proxyRequest.Headers.Host,
                cloudFrontAddress);

            HttpResponseMessage proxyResponse;
            try
            {
                proxyResponse = await httpClient.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                logger.LogDebug(
                    "Upstream request attempt {Attempt}/{MaxAttempts} to {TargetHost} via {CloudFrontAddress} timed out.",
                    attempt,
                    maxAttempts,
                    proxyRequest.Headers.Host,
                    cloudFrontAddress);

                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                await context.Response.WriteAsync("Upstream request timed out.", context.RequestAborted);
                return;
            }
            catch (Exception exception) when (IsRetryableConnectionFailure(exception))
            {
                if (attempt < maxAttempts)
                {
                    logger.LogDebug(
                        exception,
                        "Retrying upstream request after connection failure on attempt {Attempt}/{MaxAttempts} to {TargetHost} via {CloudFrontAddress}.",
                        attempt,
                        maxAttempts,
                        proxyRequest.Headers.Host,
                        cloudFrontAddress);
                    continue;
                }

                logger.LogWarning(
                    exception,
                    "Upstream request failed after connection failure on attempt {Attempt}/{MaxAttempts} to {TargetHost} via {CloudFrontAddress}.",
                    attempt,
                    maxAttempts,
                    proxyRequest.Headers.Host,
                    cloudFrontAddress);

                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync(
                    IsConnectionReset(exception) ? "Upstream connection was reset." : "Upstream connection failed.",
                    context.RequestAborted);
                return;
            }
            catch (HttpRequestException exception)
            {
                logger.LogDebug(
                    exception,
                    "Upstream request failed on attempt {Attempt}/{MaxAttempts} to {TargetHost} via {CloudFrontAddress}.",
                    attempt,
                    maxAttempts,
                    proxyRequest.Headers.Host,
                    cloudFrontAddress);

                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync("Upstream request failed.", context.RequestAborted);
                return;
            }

            using (proxyResponse)
            {
                if (proxyResponse.StatusCode == HttpStatusCode.Forbidden && attempt < maxAttempts)
                {
                    logger.LogDebug(
                        "Retrying upstream request after status {StatusCode} on attempt {Attempt}/{MaxAttempts} to {TargetHost} via {CloudFrontAddress}.",
                        (int)proxyResponse.StatusCode,
                        attempt,
                        maxAttempts,
                        proxyRequest.Headers.Host,
                        cloudFrontAddress);
                    continue;
                }

                logger.LogTrace(
                    "Upstream request completed with status {StatusCode} on attempt {Attempt}/{MaxAttempts} to {TargetHost} via {CloudFrontAddress}.",
                    (int)proxyResponse.StatusCode,
                    attempt,
                    maxAttempts,
                    proxyRequest.Headers.Host,
                    cloudFrontAddress);

                context.Response.StatusCode = (int)proxyResponse.StatusCode;
                CopyResponseHeaders(context.Response, proxyResponse);

                await proxyResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            return;
        }
    }

    private HttpRequestMessage CreateProxyRequest(HttpContext context, IPAddress cloudFrontAddress)
    {
        var request = context.Request;
        var (targetHost, targetPath) = ResolveTarget(request);
        var targetUri = CreateTargetUri(request, targetPath, cloudFrontAddress);
        var proxyRequest = new HttpRequestMessage(new HttpMethod(request.Method), targetUri);

        if (CanHaveBody(context))
            proxyRequest.Content = new StreamContent(request.Body);

        CopyRequestHeaders(request, proxyRequest);

        proxyRequest.Headers.Host = targetHost;

        if (!request.Headers.ContainsKey(ApiKeyHeaderName))
            proxyRequest.Headers.TryAddWithoutValidation(ApiKeyHeaderName, configuration.CurseForgeApiKey);

        return proxyRequest;
    }

    private static (string TargetHost, string TargetPath) ResolveTarget(HttpRequest request)
    {
        var requestPath = request.Path.HasValue ? request.Path.Value! : "/";

        if (FallbackDownloadPathPattern.IsMatch(requestPath))
            return (WwwCurseForgeHost, "/api" + requestPath);

        return (ApiCurseForgeHost, requestPath);
    }

    private static Uri CreateTargetUri(HttpRequest request, string targetPath, IPAddress cloudFrontAddress)
    {
        return new Uri(UriHelper.BuildAbsolute(
            scheme: Uri.UriSchemeHttps,
            host: new HostString(cloudFrontAddress.ToString(), port: 443),
            pathBase: PathString.Empty,
            path: new PathString(targetPath),
            query: request.QueryString));
    }

    private static int GetCloudFrontStartIndex()
    {
        var index = Interlocked.Increment(ref nextCloudFrontAddressIndex) - 1;
        return PositiveModulo(index, CloudFrontAddresses.Length);
    }

    private static IPAddress SelectCloudFrontAddress(int startIndex, int attempt)
    {
        var index = (startIndex + attempt - 1) % CloudFrontAddresses.Length;
        return CloudFrontAddresses[index];
    }

    private static int PositiveModulo(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
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

    private static bool IsConnectionReset(Exception exception)
    {
        return HasSocketError(exception, SocketError.ConnectionReset);
    }

    private static bool IsRetryableConnectionFailure(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            if (current is SocketException socketException && IsRetryableSocketError(socketException.SocketErrorCode))
                return true;

            current = current.InnerException;
        }

        return false;
    }

    private static bool HasSocketError(Exception exception, SocketError socketError)
    {
        var current = exception;
        while (current is not null)
        {
            if (current is SocketException socketException &&
                socketException.SocketErrorCode == socketError)
                return true;
            current = current.InnerException;
        }
        return false;
    }

    private static bool IsRetryableSocketError(SocketError socketError)
    {
        return socketError is
            SocketError.ConnectionReset or
            SocketError.ConnectionAborted or
            SocketError.ConnectionRefused or
            SocketError.TimedOut or
            SocketError.NetworkDown or
            SocketError.NetworkReset or
            SocketError.NetworkUnreachable or
            SocketError.HostDown or
            SocketError.HostUnreachable;
    }
}
