using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using HttpMcpProxy.Configuration;
using Microsoft.Extensions.Logging;

namespace HttpMcpProxy.Execution;

/// <summary>
/// Live state for one loaded service: HTTP client, auth, rate limiters, breaker and cache.
/// A new runtime is created on every reload; the old one is disposed once in-flight calls are done.
/// </summary>
internal sealed class ServiceRuntime : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RateLimiter?> _endpointLimiters = new();
    private int _inFlight;
    private bool _retired;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ServiceRuntime(ServiceDefinition definition, ILogger logger)
    {
        Definition = definition;
        Logger = logger;
        Client = HttpClientFactory.Create(definition);
        Auth = new AuthHandler(definition.Config.Auth, Client, definition.Directory);
        ApiLimiter = RateLimiterFactory.Create(definition.Settings.RateLimit);
        Concurrency = new SemaphoreSlim(Math.Max(1, definition.Settings.Concurrency.MaxInFlight));
        Breaker = new CircuitBreaker(definition.Settings.CircuitBreaker);
        Cache = new ResponseCache(Math.Max(1, definition.Settings.Cache.MaxEntries));
    }

    public ServiceDefinition Definition { get; }
    public ILogger Logger { get; }
    public HttpClient Client { get; }
    public AuthHandler Auth { get; }
    public RateLimiter? ApiLimiter { get; }
    public SemaphoreSlim Concurrency { get; }
    public CircuitBreaker Breaker { get; }
    public ResponseCache Cache { get; }

    public RateLimiter? LimiterFor(ToolDefinition tool) =>
        tool.Settings.RateLimit.Scope == "endpoint"
            ? _endpointLimiters.GetOrAdd(tool.Name, _ => RateLimiterFactory.Create(tool.Settings.RateLimit))
            : ApiLimiter;

    /// <summary>Tracks a call so the runtime is not disposed underneath it after a reload.</summary>
    public IDisposable BeginCall()
    {
        Interlocked.Increment(ref _inFlight);
        return new CallScope(this);
    }

    public void Retire()
    {
        Volatile.Write(ref _retired, true);
        if (Volatile.Read(ref _inFlight) == 0)
            _drained.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        Retire();
        await Task.WhenAny(_drained.Task, Task.Delay(TimeSpan.FromMinutes(2)));
        Client.Dispose();
        ApiLimiter?.Dispose();
        foreach (var limiter in _endpointLimiters.Values) limiter?.Dispose();
    }

    private sealed class CallScope(ServiceRuntime owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            if (Interlocked.Decrement(ref owner._inFlight) == 0 && Volatile.Read(ref owner._retired))
                owner._drained.TrySetResult();
        }
    }
}
