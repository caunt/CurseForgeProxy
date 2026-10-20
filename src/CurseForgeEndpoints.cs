using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace CurseForgeProxy;

internal sealed partial class CurseForgeEndpoints(
    EnvironmentConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    CloudFrontAddressSelector addressSelector,
    ILogger<CurseForgeEndpoints> logger
)
{
    public const string HttpClientName = "curseforge-egress";

    private const string ApiCurseForgeHost = "api.curseforge.com";
    private const string ApiKeyHeaderName = "x-api-key";
    private const string WwwCurseForgeHost = "www.curseforge.com";
    private static readonly TimeSpan UpstreamAttemptTimeout = TimeSpan.FromSeconds(seconds: 5);
    private static readonly TimeSpan UpstreamConnectionIdleTimeout = TimeSpan.FromMinutes(minutes: 5);
    private static readonly TimeSpan UpstreamConnectionLifetime = TimeSpan.FromMinutes(minutes: 10);

    // Shorter than an attempt, so an edge that drops SYNs fails the connect and is retried elsewhere, but longer than
    // EgressPool's two-second warm-up of a new source address, which runs inside the connect.
    private static readonly TimeSpan UpstreamConnectTimeout = TimeSpan.FromSeconds(seconds: 3);

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

    // Matches /v1/mods/{modId}/files/{fileId}/download
    [GeneratedRegex(@"^/v1/mods/\d+/files/\d+/download$", RegexOptions.IgnoreCase)]
    private static partial Regex FallbackDownloadPathPattern { get; }

    public static void ConfigureUpstreamHandler(SocketsHttpHandler handler)
    {
        handler.AllowAutoRedirect = false;
        handler.ConnectTimeout = UpstreamConnectTimeout;
        handler.MaxConnectionsPerServer = CloudFrontAddressSelector.MaximumConnectionsPerAddress;
        handler.PlaintextStreamFilter = UpstreamConnectionStream.WrapAsync;
        handler.PooledConnectionIdleTimeout = UpstreamConnectionIdleTimeout;
        handler.PooledConnectionLifetime = UpstreamConnectionLifetime;
    }

    public void ConfigurePipeline(IApplicationBuilder application)
    {
        application.Run(ProxyAsync);
    }

    private static bool CanHaveBody(HttpContext context)
    {
        HttpRequest request = context.Request;
        IHttpRequestBodyDetectionFeature? bodyDetection = context.Features.Get<IHttpRequestBodyDetectionFeature>();

        return bodyDetection?.CanHaveBody ??
            (request.ContentLength > 0 || request.Headers.ContainsKey(HeaderNames.TransferEncoding));
    }

    private static void CopyResponseHeader(HttpResponse response, KeyValuePair<string, IEnumerable<string>> header)
    {
        if (IsHopByHopHeader(header.Key))
            return;

        response.Headers[header.Key] = new StringValues([.. header.Value]);
    }

    private static void CopyResponseHeaders(HttpResponse response, HttpResponseMessage proxyResponse)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in proxyResponse.Headers)
            CopyResponseHeader(response, header);

        foreach (KeyValuePair<string, IEnumerable<string>> header in proxyResponse.Content.Headers)
            CopyResponseHeader(response, header);
    }

    private static Uri CreateTargetUri(HttpRequest request, string targetPath, IPAddress cloudFrontAddress)
    {
        return new Uri(
            UriHelper.BuildAbsolute(
                Uri.UriSchemeHttps,
                new HostString(cloudFrontAddress.ToString(), port: 443),
                PathString.Empty,
                new PathString(targetPath),
                request.QueryString
            )
        );
    }

    private static string DecodeResponseBody(byte[] responseBody, HttpResponseMessage proxyResponse)
    {
        string? charset = proxyResponse.Content.Headers.ContentType?.CharSet;

        if (string.IsNullOrWhiteSpace(charset))
            return Encoding.UTF8.GetString(responseBody);

        try
        {
            return Encoding.GetEncoding(charset.Trim(trimChar: '"')).GetString(responseBody);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(responseBody);
        }
    }

    private static bool HasSocketError(Exception exception, SocketError socketError)
    {
        Exception? current = exception;

        while (current is not null)
        {
            if (current is SocketException socketException && socketException.SocketErrorCode == socketError)
                return true;

            current = current.InnerException;
        }

        return false;
    }

    private static bool IsCloudFrontTrafficLimitForbiddenResponse(byte[] responseBody, HttpResponseMessage proxyResponse)
    {
        string body = DecodeResponseBody(responseBody, proxyResponse);

        return body.Contains(value: "The request could not be satisfied", StringComparison.OrdinalIgnoreCase) &&
            body.Contains(value: "Request blocked", StringComparison.OrdinalIgnoreCase) &&
            body.Contains(value: "There might be too much traffic or a configuration error", StringComparison.OrdinalIgnoreCase) &&
            body.Contains(value: "CloudFront", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConnectTimeout(Exception exception)
    {
        return exception is OperationCanceledException { InnerException: TimeoutException };
    }

    private static bool IsConnectionReset(Exception exception)
    {
        return HasSocketError(exception, SocketError.ConnectionReset);
    }

    private static bool IsHopByHopHeader(string headerName)
    {
        return HopByHopHeaders.Contains(headerName);
    }

    private static bool IsRetryableConnectionFailure(Exception exception)
    {
        Exception? current = exception;

        while (current is not null)
        {
            if (current is SocketException socketException && IsRetryableSocketError(socketException.SocketErrorCode))
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

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Upstream request completed with status {StatusCode} on attempt {Attempt}/{MaximumAttempts} to {TargetHost} via {CloudFrontAddress}."
    )]
    private static partial void LogAttemptCompleted(
        ILogger logger,
        int statusCode,
        int attempt,
        int maximumAttempts,
        string? targetHost,
        IPAddress cloudFrontAddress
    );

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Sending upstream request attempt {Attempt}/{MaximumAttempts} to {TargetHost} via {CloudFrontAddress}."
    )]
    private static partial void LogAttemptStarted(ILogger logger, int attempt, int maximumAttempts, string? targetHost, IPAddress cloudFrontAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Upstream request attempt {Attempt}/{MaximumAttempts} to {TargetHost} via {CloudFrontAddress} timed out waiting for the upstream response."
    )]
    private static partial void LogAttemptTimedOut(ILogger logger, int attempt, int maximumAttempts, string? targetHost, IPAddress cloudFrontAddress);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Upstream request failed after connection failure on attempt {Attempt}/{MaximumAttempts} to {TargetHost} via {CloudFrontAddress}."
    )]
    private static partial void LogConnectionFailureExhausted(
        ILogger logger,
        Exception exception,
        int attempt,
        int maximumAttempts,
        string? targetHost,
        IPAddress cloudFrontAddress
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Retrying upstream request after connection failure on attempt {Attempt}/{MaximumAttempts} to {TargetHost} via {CloudFrontAddress}."
    )]
    private static partial void LogConnectionFailureRetrying(
        ILogger logger,
        Exception exception,
        int attempt,
        int maximumAttempts,
        string? targetHost,
        IPAddress cloudFrontAddress
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Upstream request attempt {Attempt}/{MaximumAttempts} to {TargetHost} via {CloudFrontAddress} timed out waiting for an upstream connection."
    )]
    private static partial void LogConnectionWaitTimedOut(ILogger logger, int attempt, int maximumAttempts, string? targetHost, IPAddress cloudFrontAddress);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Upstream request failed on attempt {Attempt}/{MaximumAttempts} to {TargetHost} via {CloudFrontAddress}."
    )]
    private static partial void LogRequestFailed(
        ILogger logger,
        Exception exception,
        int attempt,
        int maximumAttempts,
        string? targetHost,
        IPAddress cloudFrontAddress
    );

    [LoggerMessage(Level = LogLevel.Trace, Message = "Dropped request header {HeaderName} because the upstream request does not accept it.")]
    private static partial void LogRequestHeaderDropped(ILogger logger, string headerName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Upstream request failed after status {StatusCode} on attempt {Attempt}/{MaximumAttempts} to {TargetHost} via {CloudFrontAddress}."
    )]
    private static partial void LogStatusExhausted(
        ILogger logger,
        int statusCode,
        int attempt,
        int maximumAttempts,
        string? targetHost,
        IPAddress cloudFrontAddress
    );

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Retrying upstream request after status {StatusCode} on attempt {Attempt}/{MaximumAttempts} to {TargetHost} via {CloudFrontAddress}."
    )]
    private static partial void LogStatusRetrying(
        ILogger logger,
        int statusCode,
        int attempt,
        int maximumAttempts,
        string? targetHost,
        IPAddress cloudFrontAddress
    );

    private static UpstreamTarget ResolveTarget(HttpRequest request)
    {
        string requestPath = request.Path.HasValue ? request.Path.Value : "/";

        return FallbackDownloadPathPattern.IsMatch(requestPath)
            ? new UpstreamTarget(WwwCurseForgeHost, "/api" + requestPath)
            : new UpstreamTarget(ApiCurseForgeHost, requestPath);
    }

    private void CopyRequestHeaders(HttpRequest request, HttpRequestMessage proxyRequest)
    {
        foreach (KeyValuePair<string, StringValues> header in request.Headers)
        {
            if (IsHopByHopHeader(header.Key))
                continue;

            string?[] values = [.. header.Value];

            if (proxyRequest.Headers.TryAddWithoutValidation(header.Key, values))
                continue;

            bool isContentHeaderAdded = proxyRequest.Content?.Headers.TryAddWithoutValidation(header.Key, values) ?? false;

            if (!isContentHeaderAdded)
                LogRequestHeaderDropped(logger, header.Key);
        }
    }

    private HttpRequestMessage CreateProxyRequest(HttpContext context, IPAddress cloudFrontAddress)
    {
        HttpRequest request = context.Request;
        UpstreamTarget target = ResolveTarget(request);
        Uri targetUri = CreateTargetUri(request, target.Path, cloudFrontAddress);
        HttpRequestMessage proxyRequest = new(new HttpMethod(request.Method), targetUri);

        if (CanHaveBody(context))
            proxyRequest.Content = new StreamContent(request.Body);

        CopyRequestHeaders(request, proxyRequest);

        proxyRequest.Headers.Host = target.Host;

        if (!request.Headers.ContainsKey(ApiKeyHeaderName))
            proxyRequest.Headers.Add(ApiKeyHeaderName, configuration.CurseForgeApiKey);

        return proxyRequest;
    }

    private async Task ProxyAsync(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey(ApiKeyHeaderName) && string.IsNullOrWhiteSpace(configuration.CurseForgeApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response
                .WriteAsync(text: "Missing x-api-key header and CURSEFORGE_API_KEY is not configured.", context.RequestAborted)
                .ConfigureAwait(continueOnCapturedContext: false);

            return;
        }

        const int maximumAttempts = 3;
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        int cloudFrontStartIndex = addressSelector.NextStartIndex();
        List<IPAddress> triedAddresses = new(maximumAttempts);

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using CloudFrontAddressSelector.AddressLease addressLease = addressSelector.Acquire(cloudFrontStartIndex, triedAddresses);

            IPAddress cloudFrontAddress = addressLease.Address;

            triedAddresses.Add(cloudFrontAddress);

            using HttpRequestMessage proxyRequest = CreateProxyRequest(context, cloudFrontAddress);

            LogAttemptStarted(logger, attempt, maximumAttempts, proxyRequest.Headers.Host, cloudFrontAddress);

            HttpResponseMessage proxyResponse;

            using CancellationTokenSource attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            attemptTimeout.CancelAfter(UpstreamAttemptTimeout);

            UpstreamAttempt upstreamAttempt = UpstreamAttempt.Start();

            try
            {
                proxyResponse = await httpClient
                    .SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead, attemptTimeout.Token)
                    .ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsConnectTimeout(exception) || IsRetryableConnectionFailure(exception))
            {
                if (!upstreamAttempt.HasConnection)
                    addressLease.RecordConnectionFailure();

                if (attempt < maximumAttempts)
                {
                    LogConnectionFailureRetrying(logger, exception, attempt, maximumAttempts, proxyRequest.Headers.Host, cloudFrontAddress);

                    continue;
                }

                LogConnectionFailureExhausted(logger, exception, attempt, maximumAttempts, proxyRequest.Headers.Host, cloudFrontAddress);

                if (IsConnectTimeout(exception))
                {
                    context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                    await context.Response
                        .WriteAsync(text: "Upstream connection timed out.", context.RequestAborted)
                        .ConfigureAwait(continueOnCapturedContext: false);

                    return;
                }

                string failureMessage = IsConnectionReset(exception) ? "Upstream connection was reset." : "Upstream connection failed.";

                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response.WriteAsync(failureMessage, context.RequestAborted).ConfigureAwait(continueOnCapturedContext: false);

                return;
            }
            catch (OperationCanceledException)
            {
                if (upstreamAttempt.HasConnection)
                    LogAttemptTimedOut(logger, attempt, maximumAttempts, proxyRequest.Headers.Host, cloudFrontAddress);
                else
                    LogConnectionWaitTimedOut(logger, attempt, maximumAttempts, proxyRequest.Headers.Host, cloudFrontAddress);

                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                await context.Response
                    .WriteAsync(text: "Upstream request timed out.", context.RequestAborted)
                    .ConfigureAwait(continueOnCapturedContext: false);

                return;
            }
            catch (HttpRequestException exception)
            {
                LogRequestFailed(logger, exception, attempt, maximumAttempts, proxyRequest.Headers.Host, cloudFrontAddress);

                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await context.Response
                    .WriteAsync(text: "Upstream request failed.", context.RequestAborted)
                    .ConfigureAwait(continueOnCapturedContext: false);

                return;
            }

            addressLease.RecordResponse();

            using (proxyResponse)
            {
                int statusCode = (int)proxyResponse.StatusCode;

                byte[]? responseBody = null;
                bool isRetryableForbiddenResponse = false;

                if (proxyResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    responseBody = await proxyResponse.Content
                        .ReadAsByteArrayAsync(context.RequestAborted)
                        .ConfigureAwait(continueOnCapturedContext: false);
                    isRetryableForbiddenResponse = IsCloudFrontTrafficLimitForbiddenResponse(responseBody, proxyResponse);
                }

                if (isRetryableForbiddenResponse)
                {
                    addressLease.RecordBlocked();

                    if (attempt < maximumAttempts)
                    {
                        LogStatusRetrying(logger, statusCode, attempt, maximumAttempts, proxyRequest.Headers.Host, cloudFrontAddress);

                        continue;
                    }

                    LogStatusExhausted(logger, statusCode, attempt, maximumAttempts, proxyRequest.Headers.Host, cloudFrontAddress);
                }
                else
                {
                    LogAttemptCompleted(logger, statusCode, attempt, maximumAttempts, proxyRequest.Headers.Host, cloudFrontAddress);
                }

                context.Response.StatusCode = statusCode;
                CopyResponseHeaders(context.Response, proxyResponse);

                if (responseBody is not null)
                {
                    await context.Response.Body
                        .WriteAsync(responseBody, context.RequestAborted)
                        .ConfigureAwait(continueOnCapturedContext: false);
                }
                else
                {
                    await proxyResponse.Content
                        .CopyToAsync(context.Response.Body, context.RequestAborted)
                        .ConfigureAwait(continueOnCapturedContext: false);
                }
            }

            return;
        }
    }

    private readonly record struct UpstreamTarget(string Host, string Path);
}
