using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HttpMcpProxy.Configuration;

// Typed views of config.yml and tools/*.yml. Keys are snake_case in YAML.
// Defaults here are the documented defaults; anything omitted in YAML keeps them.

public sealed class ServiceConfig
{
    public int ConfigVersion { get; set; }
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> Tags { get; set; } = [];
    public string? Owner { get; set; }
    public string? DocsUrl { get; set; }

    public ToolsConfig Tools { get; set; } = new();

    public string? BaseUrl { get; set; }
    public Dictionary<string, UrlVariable> UrlVariables { get; set; } = new();
    public Dictionary<string, EnvironmentConfig>? Environments { get; set; }
    public string? ActiveEnvironment { get; set; }

    public AuthConfig Auth { get; set; } = new();
    public TlsConfig Tls { get; set; } = new();
    public ProxyConfig Proxy { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    // The remaining sections can be overridden per endpoint; see EffectiveSettings.
}

public sealed class ToolsConfig
{
    public string? Prefix { get; set; }
    public string Separator { get; set; } = ".";
    public List<string> Include { get; set; } = ["*"];
    public List<string> Exclude { get; set; } = [];
}

public sealed class UrlVariable
{
    public string? Default { get; set; }
    public List<string>? Enum { get; set; }
    public string? FromEnv { get; set; }
}

public sealed class EnvironmentConfig
{
    public string? BaseUrl { get; set; }
}

public sealed class AuthConfig
{
    public string Type { get; set; } = "none";

    // api_key
    public string In { get; set; } = "header";
    public string? Name { get; set; }
    public string? Value { get; set; }
    public string Prefix { get; set; } = "";

    // bearer
    public string? Token { get; set; }
    public string Header { get; set; } = "Authorization";
    public string Scheme { get; set; } = "Bearer";

    // basic
    public string? Username { get; set; }
    public string? Password { get; set; }

    // oauth2
    public string Flow { get; set; } = "client_credentials";
    public string? TokenUrl { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RefreshToken { get; set; }
    public List<string> Scopes { get; set; } = [];
    public string? Audience { get; set; }
    public string ClientAuth { get; set; } = "body";
    public Dictionary<string, string> ExtraParams { get; set; } = new();
    public int RefreshBeforeExpiryS { get; set; } = 60;
    public string TokenCache { get; set; } = "memory";
    public string? TokenCachePath { get; set; }

    // custom
    public Dictionary<string, string> Headers { get; set; } = new();
    public Dictionary<string, string> Query { get; set; } = new();
    public Dictionary<string, string> Cookies { get; set; } = new();
}

public sealed class HttpConfig
{
    public Dictionary<string, string> Headers { get; set; } = new();
    public Dictionary<string, string> Query { get; set; } = new();
    public string? UserAgent { get; set; }
    public TimeoutConfig Timeout { get; set; } = new();
    public bool FollowRedirects { get; set; } = true;
    public int MaxRedirects { get; set; } = 5;
    public bool Compression { get; set; } = true;
    public bool Http2 { get; set; }
    public bool KeepAlive { get; set; } = true;
}

public sealed class TimeoutConfig
{
    public int ConnectMs { get; set; } = 5000;
    public int ReadMs { get; set; } = 10000;
    public int TotalMs { get; set; } = 15000;
}

public sealed class TlsConfig
{
    public bool Verify { get; set; } = true;
    public string? CaFile { get; set; }
    public string? ClientCert { get; set; }
    public string? ClientKey { get; set; }
    public string? ClientKeyPassword { get; set; }
    public string MinVersion { get; set; } = "1.2";
}

public sealed class ProxyConfig
{
    public string? Url { get; set; }
    public List<string> NoProxy { get; set; } = [];
}

public sealed class RetryConfig
{
    public int MaxAttempts { get; set; } = 1;
    public List<int> OnStatus { get; set; } = [429, 502, 503, 504];
    public bool OnNetworkError { get; set; } = true;
    public bool OnlyIdempotentMethods { get; set; } = true;
    public bool RespectRetryAfter { get; set; } = true;
    public BackoffConfig Backoff { get; set; } = new();
}

public sealed class BackoffConfig
{
    public string Type { get; set; } = "exponential";
    public int InitialMs { get; set; } = 500;
    public int MaxMs { get; set; } = 8000;
    public double Multiplier { get; set; } = 2.0;
    public string Jitter { get; set; } = "full";
}

public sealed class RateLimitConfig
{
    public int? Requests { get; set; }
    public string Per { get; set; } = "minute";
    public int? Burst { get; set; }
    public string OnLimit { get; set; } = "wait";
    public int MaxWaitMs { get; set; } = 10000;
    public string Scope { get; set; } = "api";
}

public sealed class ConcurrencyConfig
{
    public int MaxInFlight { get; set; } = 10;
}

public sealed class CircuitBreakerConfig
{
    public bool Enabled { get; set; }
    public int FailureThreshold { get; set; } = 5;
    public int ResetAfterMs { get; set; } = 30000;
}

public sealed class CacheConfig
{
    public bool Enabled { get; set; }
    public int TtlS { get; set; } = 60;
    public List<string> Methods { get; set; } = ["GET"];
    public int MaxEntries { get; set; } = 500;
    public List<string> KeyIncludeHeaders { get; set; } = [];
    public bool RespectCacheControl { get; set; } = true;
}

public sealed class RequestConfig
{
    public string ContentType { get; set; } = "json";
    public string QueryArrayStyle { get; set; } = "repeat";
    public bool DropNullParams { get; set; } = true;
    public bool DropEmptyStrings { get; set; }
    public string PathParamEncoding { get; set; } = "strict";
}

public sealed class ResponseConfig
{
    public string Format { get; set; } = "auto";
    public long MaxBytes { get; set; } = 1024 * 1024;
    public string OnTooLarge { get; set; } = "truncate";
    public int MaxCharsToModel { get; set; } = 20000;
    public bool IncludeStatus { get; set; }
    public List<string> IncludeHeaders { get; set; } = [];
    public bool StructuredContent { get; set; } = true;
    public bool Pretty { get; set; }

    // Endpoint-level only
    public string? Select { get; set; }
    public List<string>? Pick { get; set; }
    public List<string>? Omit { get; set; }
    public Dictionary<string, string>? Rename { get; set; }
    public int? MaxItems { get; set; }
    public JsonNode? OutputSchema { get; set; }
    public string? EmptyMessage { get; set; }
}

public sealed class ErrorsConfig
{
    public bool IncludeBody { get; set; } = true;
    public int BodyMaxChars { get; set; } = 2000;
    public List<int>? SuccessStatus { get; set; }
    public Dictionary<string, string> Messages { get; set; } = new();
    public List<int> TreatAsSuccess { get; set; } = [];
}

public sealed class PaginationConfig
{
    public string Type { get; set; } = "none";

    // offset
    public string OffsetParam { get; set; } = "offset";
    public string LimitParam { get; set; } = "limit";
    public int? DefaultLimit { get; set; }
    public int? MaxLimit { get; set; }
    public string? TotalPath { get; set; }

    // page
    public string PageParam { get; set; } = "page";
    public string SizeParam { get; set; } = "per_page";
    public int FirstPage { get; set; } = 1;
    public int? DefaultSize { get; set; }
    public int? MaxSize { get; set; }
    public string? TotalPagesPath { get; set; }

    // cursor
    public string CursorParam { get; set; } = "cursor";
    public string CursorIn { get; set; } = "query";
    public string? NextCursorPath { get; set; }
    public string? HasMorePath { get; set; }
}

public sealed class SecurityConfig
{
    public List<string>? AllowedHosts { get; set; }
    public bool AllowPrivateNetworks { get; set; }
    public List<string>? AllowedMethods { get; set; }
    public bool ReadOnly { get; set; }
    public List<string> RequireConfirmationFor { get; set; } = ["DELETE"];
}

public sealed class AnnotationsConfig
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("readOnlyHint")] public bool? ReadOnlyHint { get; set; }
    [JsonPropertyName("destructiveHint")] public bool? DestructiveHint { get; set; }
    [JsonPropertyName("idempotentHint")] public bool? IdempotentHint { get; set; }
    [JsonPropertyName("openWorldHint")] public bool? OpenWorldHint { get; set; }
}

public sealed class LoggingConfig
{
    public string Level { get; set; } = "info";
    public bool LogRequests { get; set; } = true;
    public bool LogBodies { get; set; }
    public List<string> RedactHeaders { get; set; } = ["Authorization", "Cookie", "X-Api-Key"];
    public List<string> RedactQuery { get; set; } = [];
    public List<string> RedactBodyFields { get; set; } = [];
}

/// <summary>Settings that live in config.yml and can be overridden per endpoint.</summary>
public sealed class EffectiveSettings
{
    public HttpConfig Http { get; set; } = new();
    public RetryConfig Retry { get; set; } = new();
    public RateLimitConfig RateLimit { get; set; } = new();
    public ConcurrencyConfig Concurrency { get; set; } = new();
    public CircuitBreakerConfig CircuitBreaker { get; set; } = new();
    public CacheConfig Cache { get; set; } = new();
    public RequestConfig Request { get; set; } = new();
    public ResponseConfig Response { get; set; } = new();
    public ErrorsConfig Errors { get; set; } = new();
    public PaginationConfig Pagination { get; set; } = new();
    public AnnotationsConfig Annotations { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    /// <summary>Shared across endpoints: "single" or "all".</summary>
    public string PaginationMode { get; set; } = "single";
    public int MaxPages { get; set; } = 10;
    public string? ItemsPath { get; set; }
}

public sealed class EndpointConfig
{
    public string? Title { get; set; }
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> Tags { get; set; } = [];
    public bool Deprecated { get; set; }
    public List<EndpointExample> Examples { get; set; } = [];

    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public List<ParamConfig> Params { get; set; } = [];
    public BodyConfig? Body { get; set; }

    public bool? Confirm { get; set; }
    public string? ConfirmMessage { get; set; }
}

public sealed class EndpointExample
{
    public JsonObject? Arguments { get; set; }
    public string? Description { get; set; }
}

public sealed class ParamConfig
{
    public string Name { get; set; } = "";
    public string In { get; set; } = "query";
    public string? ApiName { get; set; }
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Description { get; set; }

    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public string? Pattern { get; set; }
    public string? Format { get; set; }
    public JsonArray? Examples { get; set; }
    public JsonArray? Enum { get; set; }
    public JsonNode? Default { get; set; }
    public bool Nullable { get; set; }
    public bool Hidden { get; set; }
    public bool Deprecated { get; set; }
    public JsonNode? Const { get; set; }
    public JsonNode? Value { get; set; }

    public JsonObject? Items { get; set; }
    public int? MinItems { get; set; }
    public int? MaxItems { get; set; }
    public bool? UniqueItems { get; set; }
    public string? ArrayStyle { get; set; }

    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public double? ExclusiveMinimum { get; set; }
    public double? ExclusiveMaximum { get; set; }
    public double? MultipleOf { get; set; }

    [JsonIgnore] public string WireName => ApiName ?? Name;

    /// <summary>Hidden/const/value params are never exposed to the model.</summary>
    [JsonIgnore] public bool IsExposed => !Hidden && Const is null && Value is null;
}

public sealed class BodyConfig
{
    public string? ContentType { get; set; }
    public string ArgName { get; set; } = "spread";
    public JsonObject? Schema { get; set; }
    public JsonObject? Template { get; set; }
    public string? WrapIn { get; set; }

    [JsonIgnore] public bool IsSpread => ArgName == "spread";
}

internal static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new LenientStringConverter() },
    };

    /// <summary>Allows YAML scalars such as <c>min_version: 1.2</c> or <c>"401"</c> to bind to string properties.</summary>
    private sealed class LenientStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => null,
                _ => throw new JsonException($"Expected a string but found {reader.TokenType}"),
            };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
