using HttpMcpProxy.Configuration;
using HttpMcpProxy.Execution;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace HttpMcpProxy.Hosting;

public sealed class ProxyOptions
{
    /// <summary>Folder with one sub-folder per service (or a single service folder containing config.yml).</summary>
    public string ServicesPath { get; set; } = "services";

    /// <summary>Quiet period after the last file change before reloading.</summary>
    public TimeSpan ReloadDebounce { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Fallback poll interval, for file systems where change events are unreliable (Docker bind mounts, NFS).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Loads every service folder into the shared MCP tool collection and keeps it in sync with the disk.
/// Changing the collection makes the MCP server send <c>notifications/tools/list_changed</c> to connected clients.
/// </summary>
internal sealed class ServiceRegistry(
    McpServerPrimitiveCollection<McpServerTool> tools,
    IOptions<ProxyOptions> options,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("HttpMcpProxy.Services");
    private readonly Dictionary<string, LoadedService> _services = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failed = new(StringComparer.Ordinal); // dir -> fingerprint of broken config
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly SemaphoreSlim _changeSignal = new(0);
    private FileSystemWatcher? _watcher;

    private string Root => Path.GetFullPath(options.Value.ServicesPath);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Root))
        {
            _logger.LogWarning("Services folder {Path} does not exist yet; it will be picked up when created", Root);
            Directory.CreateDirectory(Root);
        }

        // Load synchronously so tools are available before the first client connects.
        await ReloadAsync(cancellationToken);
        StartWatcher();
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var debounce = options.Value.ReloadDebounce;
        var poll = options.Value.PollInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for a change (or the poll interval), then for the burst of events to settle.
                if (await _changeSignal.WaitAsync(poll, stoppingToken))
                    while (await _changeSignal.WaitAsync(debounce, stoppingToken)) { }

                await ReloadAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reloading services failed");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        await base.StopAsync(cancellationToken);
        foreach (var service in _services.Values)
            await service.Runtime.DisposeAsync();
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, e) =>
        {
            if (!IsIgnored(e.FullPath)) _changeSignal.Release();
        };
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, e) => _changeSignal.Release();
        _watcher.Error += (_, e) =>
        {
            _logger.LogWarning(e.GetException(), "File watcher error; falling back to polling");
            _changeSignal.Release();
        };
        _watcher.EnableRaisingEvents = true;
        _logger.LogInformation("Watching {Path} for service changes", Root);
    }

    /// <summary>Editors' swap files and the OAuth token cache should not trigger reloads.</summary>
    internal static bool IsIgnored(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith('~') || name.EndsWith(".swp") || name.EndsWith(".tmp") || name.StartsWith(".#")
               || path.Split(Path.DirectorySeparatorChar).Contains(".cache");
    }

    private IEnumerable<string> DiscoverServiceDirectories()
    {
        if (File.Exists(Path.Combine(Root, ServiceLoader.ConfigFileName)))
            yield return Root;
        foreach (var dir in Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal))
            if (!Path.GetFileName(dir).StartsWith('.') && File.Exists(Path.Combine(dir, ServiceLoader.ConfigFileName)))
                yield return dir;
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        await _reloadLock.WaitAsync(ct);
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var retired = new List<ServiceRuntime>();

            // One list_changed notification for the whole batch.
            using (tools.DeferChangedEvents())
            {
                foreach (var dir in Directory.Exists(Root) ? DiscoverServiceDirectories().ToList() : [])
                {
                    seen.Add(dir);
                    var fingerprint = ServiceLoader.Fingerprint(dir);
                    if (_services.TryGetValue(dir, out var existing) && existing.Fingerprint == fingerprint)
                        continue;
                    if (existing is null && _failed.GetValueOrDefault(dir) == fingerprint)
                        continue; // still broken, already reported

                    LoadService(dir, fingerprint, existing, retired);
                }

                foreach (var dir in _failed.Keys.Where(d => !seen.Contains(d)).ToList())
                    _failed.Remove(dir);

                foreach (var dir in _services.Keys.Where(d => !seen.Contains(d)).ToList())
                {
                    var removed = _services[dir];
                    RemoveTools(removed);
                    retired.Add(removed.Runtime);
                    _services.Remove(dir);
                    _logger.LogInformation("Service {Service} removed ({Path})", removed.Name, dir);
                }
            }

            foreach (var runtime in retired)
                _ = runtime.DisposeAsync().AsTask(); // waits for in-flight calls in the background
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private void LoadService(string dir, string fingerprint, LoadedService? existing, List<ServiceRuntime> retired)
    {
        ServiceDefinition? definition;
        try
        {
            definition = ServiceLoader.Load(dir);
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            // Keep serving the last good version; a typo while editing should not drop tools.
            if (existing is not null)
            {
                _logger.LogError("Service in {Path} failed to reload, keeping previous version: {Error}", dir, ex.Message);
                _services[dir] = existing with { Fingerprint = fingerprint };
            }
            else
            {
                _logger.LogError("Service in {Path} failed to load: {Error}", dir, ex.Message);
                _failed[dir] = fingerprint;
            }

            return;
        }

        _failed.Remove(dir);
        if (existing is not null)
        {
            RemoveTools(existing);
            retired.Add(existing.Runtime);
            _services.Remove(dir);
        }

        if (definition is null)
        {
            _logger.LogInformation("Service in {Path} is disabled", dir);
            return;
        }

        foreach (var warning in definition.Warnings)
            _logger.LogWarning("{Service}: {Warning}", definition.Config.Name, warning);

        var runtime = new ServiceRuntime(definition, loggerFactory.CreateLogger($"HttpMcpProxy.Service.{definition.Config.Name}"));
        var names = new List<string>();
        foreach (var tool in definition.Tools)
        {
            if (tools.TryAdd(new ProxyTool(tool, runtime)))
                names.Add(tool.Name);
            else
                _logger.LogError("{Service}: tool {Tool} is already provided by another service; skipped", definition.Config.Name, tool.Name);
        }

        _services[dir] = new LoadedService(definition.Config.Name, fingerprint, runtime, names);
        _logger.LogInformation("Service {Service} {Action} with {Count} tool(s): {Tools}",
            definition.Config.Name, existing is null ? "loaded" : "reloaded", names.Count, string.Join(", ", names));
    }

    private void RemoveTools(LoadedService service)
    {
        foreach (var name in service.ToolNames)
            if (tools.TryGetPrimitive(name, out var tool))
                tools.Remove(tool);
    }

    private sealed record LoadedService(string Name, string Fingerprint, ServiceRuntime Runtime, IReadOnlyList<string> ToolNames);
}
