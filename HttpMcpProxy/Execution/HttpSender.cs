using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HttpMcpProxy.Configuration;
using Microsoft.Extensions.Logging;

namespace HttpMcpProxy.Execution;

/// <summary>A failure that should be reported to the model as a tool error (not a protocol error).</summary>
internal sealed class ToolCallException(string message) : Exception(message);

/// <summary>Sends a prepared request through cache, circuit breaker, rate limit, concurrency, retry and redirects.</summary>
internal static partial class HttpSender
{
    private static readonly HashSet<string> IdempotentMethods = ["GET", "HEAD", "PUT", "DELETE", "OPTIONS"];

    public static async Task<CachedResponse> SendAsync(ServiceRuntime runtime, ToolDefinition tool, PreparedRequest request, CancellationToken ct)
    {
        var settings = tool.Settings;
        EnsureHostAllowed(runtime, request.Url);

        var cacheKey = CacheKey(tool, request);
        if (cacheKey is not null && runtime.Cache.TryGet(cacheKey, out var cached))
        {
            Log(runtime, LogLevel.Debug, "{Tool} cache hit {Url}", tool.Name, Redact(runtime, request.Url));
            return cached;
        }

        if (!runtime.Breaker.TryEnter(out var retryIn))
            throw new ToolCallException($"{runtime.Definition.Config.Title ?? runtime.Definition.Config.Name} is temporarily unavailable (circuit open); retry in {Math.Ceiling(retryIn.TotalSeconds)}s.");

        var limiter = runtime.LimiterFor(tool);
        if (limiter is not null)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(settings.RateLimit.MaxWaitMs);
            System.Threading.RateLimiting.RateLimitLease lease;
            try
            {
                lease = await limiter.AcquireAsync(1, waitCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ToolCallException("Rate limit reached for this API; try again later.");
            }

            using (lease)
                if (!lease.IsAcquired)
                    throw new ToolCallException("Rate limit reached for this API; try again later.");
        }

        await runtime.Concurrency.WaitAsync(ct);
        try
        {
            var response = await SendWithRetryAsync(runtime, tool, request, ct);
            runtime.Breaker.Record(response.Status < 500);

            if (cacheKey is not null && response.Status is >= 200 and < 300 && CacheTtl(settings.Cache, response) is { } ttl)
                runtime.Cache.Set(cacheKey, response, ttl);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            runtime.Breaker.Record(false);
            throw;
        }
        finally
        {
            runtime.Concurrency.Release();
        }
    }

    private static async Task<CachedResponse> SendWithRetryAsync(ServiceRuntime runtime, ToolDefinition tool, PreparedRequest request, CancellationToken ct)
    {
        var retry = tool.Settings.Retry;
        var canRetry = !retry.OnlyIdempotentMethods || IdempotentMethods.Contains(request.Method.Method);
        var maxAttempts = canRetry ? Math.Max(1, retry.MaxAttempts) : 1;
        var refreshedToken = false;

        for (var attempt = 1; ; attempt++)
        {
            CachedResponse response;
            try
            {
                response = await SendFollowingRedirectsAsync(runtime, tool, request, ct);
            }
            catch (Exception ex) when (IsNetworkError(ex, ct))
            {
                var message = ex is OperationCanceledException ? "timed out" : ex.Message;
                if (attempt < maxAttempts && retry.OnNetworkError)
                {
                    Log(runtime, LogLevel.Warning, "{Tool} attempt {Attempt} failed: {Error}; retrying", tool.Name, attempt, message);
                    await Task.Delay(Backoff(retry.Backoff, attempt), ct);
                    continue;
                }

                throw new ToolCallException($"Request to the API failed: {message}");
            }

            // An expired/revoked OAuth token: fetch a new one and try once more, independent of retry config.
            if (response.Status == 401 && runtime.Auth.IsOAuth && !refreshedToken)
            {
                refreshedToken = true;
                runtime.Auth.InvalidateToken();
                attempt--;
                continue;
            }

            if (attempt < maxAttempts && retry.OnStatus.Contains(response.Status))
            {
                var delay = Backoff(retry.Backoff, attempt);
                if (retry.RespectRetryAfter && RetryAfter(response) is { } retryAfter)
                    delay = retryAfter > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : retryAfter;
                Log(runtime, LogLevel.Warning, "{Tool} got HTTP {Status}; retry {Attempt}/{Max} in {Delay}ms", tool.Name, response.Status, attempt + 1, maxAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
                continue;
            }

            return response;
        }
    }

    private static async Task<CachedResponse> SendFollowingRedirectsAsync(ServiceRuntime runtime, ToolDefinition tool, PreparedRequest request, CancellationToken ct)
    {
        var http = tool.Settings.Http;
        var url = request.Url;
        var method = request.Method;
        var sendBody = true;

        for (var hop = 0; ; hop++)
        {
            var response = await SendOnceAsync(runtime, tool, request, url, method, sendBody, ct);
            if (!http.FollowRedirects || response.Status is not (301 or 302 or 303 or 307 or 308)
                || !response.Headers.TryGetValue("Location", out var location))
                return response;

            if (hop >= http.MaxRedirects)
                throw new ToolCallException($"Too many redirects (max {http.MaxRedirects}).");

            url = new Uri(url, location);
            EnsureHostAllowed(runtime, url);
            if (response.Status == 303 || (response.Status is 301 or 302 && method == HttpMethod.Post))
            {
                method = HttpMethod.Get;
                sendBody = false;
            }
        }
    }

    private static async Task<CachedResponse> SendOnceAsync(ServiceRuntime runtime, ToolDefinition tool, PreparedRequest prepared, Uri url, HttpMethod method, bool sendBody, CancellationToken ct)
    {
        var settings = tool.Settings;
        using var message = new HttpRequestMessage(method, url);
        foreach (var (name, value) in prepared.Headers)
        {
            message.Headers.Remove(name);
            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (sendBody && prepared.CreateContent is not null)
            message.Content = prepared.CreateContent();
        if (!settings.Http.KeepAlive)
            message.Headers.ConnectionClose = true;
        if (settings.Http.Http2)
        {
            message.Version = HttpVersion.Version20;
            message.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        await runtime.Auth.ApplyHeadersAsync(message, ct);

        var logging = settings.Logging;
        if (logging.LogBodies && prepared.BodyText is not null && sendBody)
            Log(runtime, LogLevel.Debug, "{Tool} request body: {Body}", tool.Name, RedactBody(prepared.BodyText, logging.RedactBodyFields));

        var stopwatch = Stopwatch.StartNew();
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalCts.CancelAfter(settings.Http.Timeout.TotalMs);

        using var response = await runtime.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, totalCts.Token);

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token);
        readCts.CancelAfter(settings.Http.Timeout.ReadMs);
        var (body, truncated) = await ReadBodyAsync(response, settings.Response.MaxBytes, readCts.Token);
        if (truncated && settings.Response.OnTooLarge == "error")
            throw new ToolCallException($"Response is larger than {settings.Response.MaxBytes} bytes.");

        if (logging.LogRequests)
            Log(runtime, LogLevel.Information, "{Tool} {Method} {Url} -> {Status} ({Elapsed} ms)",
                tool.Name, method.Method, Redact(runtime, url), (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
        if (logging.LogBodies)
            Log(runtime, LogLevel.Debug, "{Tool} response body: {Body}", tool.Name,
                RedactBody(Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 4000)), logging.RedactBodyFields));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            headers[name] = string.Join(", ", values);

        return new CachedResponse((int)response.StatusCode, response.ReasonPhrase, headers,
            response.Content.Headers.ContentType?.MediaType, body, truncated);
    }

    private static async Task<(byte[] Body, bool Truncated)> ReadBodyAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            var room = maxBytes - buffer.Length;
            if (read > room)
            {
                buffer.Write(chunk, 0, (int)Math.Max(0, room));
                return (buffer.ToArray(), true);
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), false);
    }

    private static void EnsureHostAllowed(ServiceRuntime runtime, Uri url)
    {
        if (url.Scheme is not ("http" or "https"))
            throw new ToolCallException($"Blocked: scheme '{url.Scheme}' is not allowed.");
        if (!runtime.Definition.AllowedHosts.Contains(url.Host))
            throw new ToolCallException($"Blocked: host '{url.Host}' is not in security.allowed_hosts.");
    }

    private static bool IsNetworkError(Exception ex, CancellationToken callerToken) =>
        ex is HttpRequestException or IOException || (ex is OperationCanceledException && !callerToken.IsCancellationRequested);

    private static TimeSpan Backoff(BackoffConfig backoff, int attempt)
    {
        double ms = backoff.Type switch
        {
            "fixed" => backoff.InitialMs,
            "linear" => backoff.InitialMs * attempt,
            _ => backoff.InitialMs * Math.Pow(backoff.Multiplier, attempt - 1),
        };
        ms = Math.Min(ms, backoff.MaxMs);
        ms = backoff.Jitter switch
        {
            "full" => Random.Shared.NextDouble() * ms,
            "equal" => ms / 2 + Random.Shared.NextDouble() * ms / 2,
            _ => ms,
        };
        return TimeSpan.FromMilliseconds(ms);
    }

    private static TimeSpan? RetryAfter(CachedResponse response)
    {
        if (!response.Headers.TryGetValue("Retry-After", out var value)) return null;
        if (int.TryParse(value, out var seconds)) return TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (DateTimeOffset.TryParse(value, out var date)) return date - DateTimeOffset.UtcNow is var d && d > TimeSpan.Zero ? d : TimeSpan.Zero;
        return null;
    }

    private static string? CacheKey(ToolDefinition tool, PreparedRequest request)
    {
        var cache = tool.Settings.Cache;
        if (!cache.Enabled || !cache.Methods.Contains(request.Method.Method, StringComparer.OrdinalIgnoreCase))
            return null;

        var sb = new StringBuilder().Append(tool.Name).Append('|').Append(request.Method).Append('|').Append(request.Url).Append('|').Append(request.BodyText);
        foreach (var header in cache.KeyIncludeHeaders)
            sb.Append('|').Append(header).Append('=').Append(request.Headers.LastOrDefault(h => h.Key.Equals(header, StringComparison.OrdinalIgnoreCase)).Value);
        return sb.ToString();
    }

    private static TimeSpan? CacheTtl(CacheConfig cache, CachedResponse response)
    {
        var ttl = TimeSpan.FromSeconds(cache.TtlS);
        if (!cache.RespectCacheControl || !response.Headers.TryGetValue("Cache-Control", out var cc))
            return ttl;
        if (cc.Contains("no-store", StringComparison.OrdinalIgnoreCase) || cc.Contains("no-cache", StringComparison.OrdinalIgnoreCase))
            return null;
        var maxAge = MaxAgeRegex().Match(cc);
        return maxAge.Success ? TimeSpan.FromSeconds(int.Parse(maxAge.Groups[1].Value)) : ttl;
    }

    private static string Redact(ServiceRuntime runtime, Uri url)
    {
        if (string.IsNullOrEmpty(url.Query)) return url.ToString();
        var redact = runtime.Definition.Settings.Logging.RedactQuery.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (runtime.Definition.Config.Auth is { Type: "api_key", In: "query", Name: { } keyName })
            redact.Add(keyName);
        foreach (var name in runtime.Definition.Config.Auth.Query.Keys)
            redact.Add(name);

        var parts = url.Query.TrimStart('?').Split('&').Select(part =>
        {
            var eq = part.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            return redact.Contains(name) ? $"{part[..Math.Max(eq, 0)]}=***" : part;
        });
        return url.GetLeftPart(UriPartial.Path) + "?" + string.Join('&', parts);
    }

    private static string RedactBody(string body, List<string> fields)
    {
        if (fields.Count == 0) return body;
        try
        {
            var node = JsonNode.Parse(body);
            RedactNode(node, fields.ToHashSet(StringComparer.OrdinalIgnoreCase));
            return node?.ToJsonString() ?? body;
        }
        catch (System.Text.Json.JsonException)
        {
            return body;
        }
    }

    private static void RedactNode(JsonNode? node, HashSet<string> fields)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                    if (fields.Contains(key)) obj[key] = "***";
                    else RedactNode(obj[key], fields);
                break;
            case JsonArray arr:
                foreach (var item in arr) RedactNode(item, fields);
                break;
        }
    }

    private static void Log(ServiceRuntime runtime, LogLevel level, string template, params object?[] args)
    {
        var minimum = runtime.Definition.Settings.Logging.Level switch
        {
            "debug" => LogLevel.Debug,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Information,
        };
        if (level >= minimum)
#pragma warning disable CA2254
            runtime.Logger.Log(level, template, args);
#pragma warning restore CA2254
    }

    [GeneratedRegex(@"max-age=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MaxAgeRegex();
}
