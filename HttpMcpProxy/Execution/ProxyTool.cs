using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HttpMcpProxy.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HttpMcpProxy.Execution;

/// <summary>An MCP tool that forwards calls to one configured HTTP endpoint.</summary>
internal sealed partial class ProxyTool : McpServerTool
{
    private readonly ToolDefinition _tool;
    private readonly ServiceRuntime _runtime;

    public ProxyTool(ToolDefinition tool, ServiceRuntime runtime)
    {
        _tool = tool;
        _runtime = runtime;
        ProtocolTool = new Tool
        {
            Name = tool.Name,
            Title = tool.Endpoint.Title,
            Description = tool.Description,
            InputSchema = JsonSerializer.SerializeToElement(tool.InputSchema),
            OutputSchema = tool.OutputSchema is null ? null : JsonSerializer.SerializeToElement(tool.OutputSchema),
            Annotations = new ToolAnnotations
            {
                Title = tool.Annotations.Title,
                ReadOnlyHint = tool.Annotations.ReadOnlyHint,
                DestructiveHint = tool.Annotations.DestructiveHint,
                IdempotentHint = tool.Annotations.IdempotentHint,
                OpenWorldHint = tool.Annotations.OpenWorldHint,
            },
        };
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        using var _ = _runtime.BeginCall();
        try
        {
            var args = ToJsonObject(request.Params?.Arguments);
            var errors = RequestBuilder.ValidateAndApplyDefaults(_tool, args);
            if (errors.Count > 0)
                return Error("Invalid arguments: " + string.Join("; ", errors));

            if (_tool.RequiresConfirmation && !await ConfirmAsync(request.Server, args, cancellationToken))
                return Error("The user declined this action; it was not executed.");

            return _tool.Settings.PaginationMode == "all" && _tool.Settings.Pagination.Type != "none"
                ? await FetchAllPagesAsync(args, cancellationToken)
                : await FetchSinglePageAsync(args, cancellationToken);
        }
        catch (ToolCallException ex)
        {
            return Error(ex.Message);
        }
        catch (AuthException ex)
        {
            _runtime.Logger.LogWarning("{Tool}: {Error}", _tool.Name, ex.Message);
            return Error("Authentication with the API failed: " + ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _runtime.Logger.LogError(ex, "{Tool} failed", _tool.Name);
            return Error("Unexpected error while calling the API: " + ex.Message);
        }
    }

    private async Task<CallToolResult> FetchSinglePageAsync(JsonObject args, CancellationToken ct)
    {
        var pagination = _tool.Settings.Pagination;
        var prepared = RequestBuilder.Build(_runtime, _tool, args, PageQuery(args, pagination, nextCursor: null));
        ApplyCursorHeader(prepared, args, pagination);
        if (pagination.Type == "link_header" && args[SchemaBuilder.NextPageUrlArg] is JsonValue next && next.TryGetValue<string>(out var nextUrl) && nextUrl.Length > 0)
            prepared.Url = new Uri(prepared.Url, nextUrl);

        var response = await HttpSender.SendAsync(_runtime, _tool, prepared, ct);
        if (!ResponseFormatter.IsSuccess(_tool, response))
            return ToResult(ResponseFormatter.FormatError(_tool, response));

        var json = ResponseFormatter.TryParseJson(_tool, response);
        var note = PaginationNote(args, pagination, response, json);
        return ToResult(ResponseFormatter.FormatSuccess(_tool, response, json, skipSelect: false, note));
    }

    private async Task<CallToolResult> FetchAllPagesAsync(JsonObject args, CancellationToken ct)
    {
        var settings = _tool.Settings;
        var pagination = settings.Pagination;
        var items = new JsonArray();
        string? cursor = null;
        Uri? nextLink = null;
        CachedResponse? last = null;
        var pageArgs = (JsonObject)args.DeepClone();
        var truncated = false;

        for (var page = 0; page < Math.Max(1, settings.MaxPages); page++)
        {
            var prepared = RequestBuilder.Build(_runtime, _tool, pageArgs, PageQuery(pageArgs, pagination, cursor));
            if (nextLink is not null) prepared.Url = nextLink;
            if (pagination.Type == "cursor" && pagination.CursorIn == "header" && cursor is not null)
                prepared.Headers.Add(new(pagination.CursorParam, cursor));

            last = await HttpSender.SendAsync(_runtime, _tool, prepared, ct);
            if (!ResponseFormatter.IsSuccess(_tool, last))
                return ToResult(ResponseFormatter.FormatError(_tool, last));

            var json = ResponseFormatter.TryParseJson(_tool, last);
            var pageItems = settings.ItemsPath is { } itemsPath ? JsonPath.Select(json, itemsPath) : json;
            if (pageItems is not JsonArray array)
                return Error($"Pagination: could not find an array of items in the response (items_path: {settings.ItemsPath ?? "$"}).");
            foreach (var item in array) items.Add(item?.DeepClone());

            // Work out the next page; stop when there is none.
            switch (pagination.Type)
            {
                case "offset":
                {
                    var limit = Int(pageArgs[pagination.LimitParam]) ?? pagination.DefaultLimit ?? array.Count;
                    var offset = (Int(pageArgs[pagination.OffsetParam]) ?? 0) + array.Count;
                    var total = pagination.TotalPath is { } tp ? Int(JsonPath.Select(json, tp)) : null;
                    if (array.Count == 0 || array.Count < limit || (total is not null && offset >= total)) goto done;
                    pageArgs[pagination.OffsetParam] = offset;
                    pageArgs[pagination.LimitParam] = limit;
                    break;
                }
                case "page":
                {
                    var current = Int(pageArgs[pagination.PageParam]) ?? pagination.FirstPage;
                    var size = Int(pageArgs[pagination.SizeParam]) ?? pagination.DefaultSize;
                    var totalPages = pagination.TotalPagesPath is { } tpp ? Int(JsonPath.Select(json, tpp)) : null;
                    var lastPageIndex = totalPages is null ? (int?)null : totalPages + pagination.FirstPage - 1;
                    if (array.Count == 0 || (lastPageIndex is not null && current >= lastPageIndex) || (size is not null && array.Count < size)) goto done;
                    pageArgs[pagination.PageParam] = current + 1;
                    break;
                }
                case "cursor":
                {
                    cursor = NextCursor(json, pagination);
                    if (cursor is null) goto done;
                    break;
                }
                case "link_header":
                {
                    nextLink = NextLink(last, prepared.Url);
                    if (nextLink is null) goto done;
                    break;
                }
            }

            if (page == settings.MaxPages - 1) truncated = true;
        }

        done:
        var note = truncated ? $"Stopped after max_pages ({settings.MaxPages}); more results exist." : null;
        return ToResult(ResponseFormatter.FormatSuccess(_tool, last!, items, skipSelect: true, note));
    }

    /// <summary>Query parameters for the current page (only for pagination args the endpoint does not declare itself).</summary>
    private Dictionary<string, JsonNode?> PageQuery(JsonObject args, PaginationConfig pagination, string? nextCursor)
    {
        var query = new Dictionary<string, JsonNode?>();
        void Add(string name, JsonNode? value)
        {
            if (_tool.Endpoint.Params.Any(p => p.Name == name)) return;
            if (value is not null) query[name] = value.DeepClone();
        }

        switch (pagination.Type)
        {
            case "offset":
                Add(pagination.OffsetParam, args[pagination.OffsetParam]);
                Add(pagination.LimitParam, Clamp(args[pagination.LimitParam], pagination.DefaultLimit, pagination.MaxLimit));
                break;
            case "page":
                Add(pagination.PageParam, args[pagination.PageParam]);
                Add(pagination.SizeParam, Clamp(args[pagination.SizeParam], pagination.DefaultSize, pagination.MaxSize));
                break;
            case "cursor":
                if (pagination.CursorIn == "query")
                    Add(pagination.CursorParam, nextCursor is not null ? JsonValue.Create(nextCursor) : args[pagination.CursorParam]);
                Add(pagination.LimitParam, Clamp(args[pagination.LimitParam], pagination.DefaultLimit, pagination.MaxLimit));
                break;
        }

        return query;
    }

    private static void ApplyCursorHeader(PreparedRequest prepared, JsonObject args, PaginationConfig pagination)
    {
        if (pagination.Type == "cursor" && pagination.CursorIn == "header" && args[pagination.CursorParam] is JsonValue v && v.TryGetValue<string>(out var c))
            prepared.Headers.Add(new(pagination.CursorParam, c));
    }

    private static string? PaginationNote(JsonObject args, PaginationConfig pagination, CachedResponse response, JsonNode? json)
    {
        switch (pagination.Type)
        {
            case "cursor":
                return NextCursor(json, pagination) is { } next
                    ? $"More results available: call again with {pagination.CursorParam}=\"{next}\"."
                    : null;
            case "offset":
            {
                var offset = Int(args[pagination.OffsetParam]) ?? 0;
                var limit = Int(args[pagination.LimitParam]) ?? pagination.DefaultLimit;
                var total = pagination.TotalPath is { } tp ? Int(JsonPath.Select(json, tp)) : null;
                if (limit is null || total is null || offset + limit >= total) return null;
                return $"Showing {offset}–{offset + limit} of {total}. Call again with {pagination.OffsetParam}={offset + limit} for more.";
            }
            case "page":
            {
                var page = Int(args[pagination.PageParam]) ?? pagination.FirstPage;
                var totalPages = pagination.TotalPagesPath is { } tpp ? Int(JsonPath.Select(json, tpp)) : null;
                if (totalPages is null || page >= totalPages + pagination.FirstPage - 1) return null;
                return $"Page {page} of {totalPages}. Call again with {pagination.PageParam}={page + 1} for more.";
            }
            case "link_header":
                return NextLink(response, null) is { } link
                    ? $"More results available: call again with {SchemaBuilder.NextPageUrlArg}=\"{link}\"."
                    : null;
            default:
                return null;
        }
    }

    private static string? NextCursor(JsonNode? json, PaginationConfig pagination)
    {
        if (pagination.HasMorePath is { } hasMorePath && JsonPath.Select(json, hasMorePath) is JsonValue hm && hm.TryGetValue<bool>(out var hasMore) && !hasMore)
            return null;
        if (pagination.NextCursorPath is null) return null;
        var next = RequestBuilder.Scalar(JsonPath.Select(json, pagination.NextCursorPath));
        return string.IsNullOrEmpty(next) ? null : next;
    }

    private static Uri? NextLink(CachedResponse response, Uri? baseUri)
    {
        if (!response.Headers.TryGetValue("Link", out var link)) return null;
        var match = NextLinkRegex().Match(link);
        if (!match.Success) return null;
        return baseUri is null ? new Uri(match.Groups[1].Value, UriKind.RelativeOrAbsolute) : new Uri(baseUri, match.Groups[1].Value);
    }

    private async Task<bool> ConfirmAsync(McpServer server, JsonObject args, CancellationToken ct)
    {
        // Without elicitation support the host's own tool-approval UI is the confirmation
        // (the tool is annotated destructive/non-read-only accordingly).
        if (server.ClientCapabilities?.Elicitation is null)
            return true;

        var message = _tool.Endpoint.ConfirmMessage is { Length: > 0 } template
            ? PlaceholderRegex().Replace(template, m => RequestBuilder.Scalar(args[m.Groups[1].Value]))
            : $"Allow {_tool.Name} ({_tool.Method} {_tool.Endpoint.Path})?";

        var result = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema(),
        }, ct);
        return result.Action == "accept";
    }

    private static JsonObject ToJsonObject(IDictionary<string, JsonElement>? arguments)
    {
        var obj = new JsonObject();
        if (arguments is null) return obj;
        foreach (var (key, value) in arguments)
            obj[key] = JsonNode.Parse(value.GetRawText());
        return obj;
    }

    private static JsonNode? Clamp(JsonNode? value, int? defaultValue, int? max)
    {
        var n = Int(value) ?? defaultValue;
        if (n is null) return null;
        return JsonValue.Create(max is not null ? Math.Min(n.Value, max.Value) : n.Value);
    }

    private static int? Int(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<long>(out var l) => (int)l,
        JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) => p,
        _ => null,
    };

    private CallToolResult ToResult(FormattedResult result)
    {
        var callResult = new CallToolResult
        {
            Content = [new TextContentBlock { Text = result.Text }],
            IsError = result.IsError,
        };
        if (!result.IsError && _tool.Settings.Response.StructuredContent && result.Json is JsonObject obj)
            callResult.StructuredContent = JsonSerializer.SerializeToElement(obj);
        return callResult;
    }

    private static CallToolResult Error(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true,
    };

    [GeneratedRegex("""<([^>]+)>\s*;[^,]*rel="?next"?""", RegexOptions.IgnoreCase)]
    private static partial Regex NextLinkRegex();

    [GeneratedRegex(@"\{([A-Za-z0-9_\-]+)\}")]
    private static partial Regex PlaceholderRegex();
}
