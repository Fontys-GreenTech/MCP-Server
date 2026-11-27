using HttpMcpProxy.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HttpMcpProxy;

/// <summary>
/// Entry point for the HTTP-proxy MCP server. Every folder under the services path describes one
/// REST API; each endpoint file becomes an MCP tool. Folders are hot-reloaded while the server runs.
/// </summary>
public static class McpProxyServer
{
    public const string ConfigSection = "Proxy";

    private static readonly Dictionary<string, string> SwitchMappings = new()
    {
        ["--services"] = $"{ConfigSection}:{nameof(ProxyOptions.ServicesPath)}",
    };

    /// <summary>
    /// Runs the server until shutdown. Pass <c>--stdio</c> to serve over stdin/stdout instead of HTTP.
    /// The services folder comes from <c>--services &lt;path&gt;</c>, the <c>MCP_SERVICES_PATH</c> env var or
    /// <c>Proxy:ServicesPath</c> in configuration (default: <c>./services</c>).
    /// </summary>
    public static Task RunAsync(string[] args, CancellationToken cancellationToken = default) =>
        args.Contains("--stdio")
            ? RunStdioAsync(args.Where(a => a != "--stdio").ToArray(), cancellationToken)
            : RunHttpAsync(args, cancellationToken);

    private static async Task RunHttpAsync(string[] args, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddCommandLine(args, SwitchMappings);
        builder.Services.AddHttpMcpProxy(builder.Configuration);
        // Sessions are needed to push notifications/tools/list_changed after a hot reload (and for elicitation).
        builder.Services.AddMcpServer(ConfigureServer)
            .WithHttpTransport(o => o.SessionMode = ModelContextProtocol.AspNetCore.HttpServerSessionMode.StatefulForInitializeClients);

        var app = builder.Build();
        app.MapMcp("/mcp");
        app.MapGet("/health", (McpServerPrimitiveCollection<McpServerTool> tools) => Results.Ok(new { status = "ok", tools = tools.Count }));
        await app.RunAsync(cancellationToken);
    }

    private static async Task RunStdioAsync(string[] args, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddCommandLine(args, SwitchMappings);
        // stdout is the MCP channel: all logs must go to stderr.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddHttpMcpProxy(builder.Configuration);
        builder.Services.AddMcpServer(ConfigureServer).WithStdioServerTransport();
        await builder.Build().RunAsync(cancellationToken);
    }

    /// <summary>Registers the service registry and the shared tool collection.</summary>
    private static IServiceCollection AddHttpMcpProxy(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ProxyOptions>(configuration.GetSection(ConfigSection));
        services.PostConfigure<ProxyOptions>(o =>
        {
            if (Environment.GetEnvironmentVariable("MCP_SERVICES_PATH") is { Length: > 0 } fromEnv
                && configuration[$"{ConfigSection}:{nameof(ProxyOptions.ServicesPath)}"] is null)
                o.ServicesPath = fromEnv;
        });
        services.AddSingleton(new McpServerPrimitiveCollection<McpServerTool>());
        services.AddHostedService<ServiceRegistry>();
        services.AddOptions<ModelContextProtocol.Server.McpServerOptions>()
            .Configure<McpServerPrimitiveCollection<McpServerTool>>((options, tools) => options.ToolCollection = tools);
        return services;
    }

    private static void ConfigureServer(ModelContextProtocol.Server.McpServerOptions options)
    {
        options.ServerInfo = new Implementation { Name = "http-mcp-proxy", Version = "1.0.0" };
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Tools = new ToolsCapability { ListChanged = true };
        options.ServerInstructions =
            "Tools are generated from REST API configurations and named <service>.<endpoint>. " +
            "The tool list can change at runtime when configurations are edited.";
    }
}
