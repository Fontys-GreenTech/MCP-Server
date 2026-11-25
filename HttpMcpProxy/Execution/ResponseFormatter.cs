using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HttpMcpProxy.Configuration;

namespace HttpMcpProxy.Execution;

internal sealed record FormattedResult(string Text, JsonNode? Json, bool IsError);

internal static class ResponseFormatter
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool IsSuccess(ToolDefinition tool, CachedResponse response)
    {
        var errors = tool.Settings.Errors;
        if (errors.TreatAsSuccess.Contains(response.Status)) return true;
        return errors.SuccessStatus is { Count: > 0 } ok ? ok.Contains(response.Status) : response.Status is >= 200 and < 300;
    }

    public static FormattedResult FormatError(ToolDefinition tool, CachedResponse response)
    {
        var errors = tool.Settings.Errors;
        var status = response.Status.ToString();
        var message = errors.Messages.GetValueOrDefault(status)
                      ?? errors.Messages.GetValueOrDefault($"{status[0]}xx")
                      ?? errors.Messages.GetValueOrDefault($"{status[0]}XX")
                      ?? $"The API returned HTTP {response.Status} {response.ReasonPhrase}".TrimEnd();

        var sb = new StringBuilder(message);
        if (!message.Contains(status, StringComparison.Ordinal))
            sb.Append($" (HTTP {status})");
        if (errors.IncludeBody && response.Body.Length > 0)
            sb.Append("\n\nResponse body:\n").Append(Truncate(Decode(response.Body), errors.BodyMaxChars));
        return new FormattedResult(sb.ToString(), null, true);
    }

    /// <summary>Parses the body as JSON when the format says so; returns null for text bodies.</summary>
    public static JsonNode? TryParseJson(ToolDefinition tool, CachedResponse response)
    {
        if (response.Body.Length == 0) return null;
        var format = tool.Settings.Response.Format;
        var looksJson = format == "json" || (format == "auto" && (response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false));
        if (!looksJson || response.Truncated) return null;
        try
        {
            return JsonNode.Parse(response.Body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Applies select/pick/omit/rename/max_items and the output limits.</summary>
    public static FormattedResult FormatSuccess(ToolDefinition tool, CachedResponse response, JsonNode? json, bool skipSelect, string? paginationNote)
    {
        var settings = tool.Settings.Response;

        string text;
        JsonNode? shaped = null;
        if (json is not null)
        {
            shaped = skipSelect || settings.Select is null ? json : JsonPath.Select(json, settings.Select);
            shaped = Shape(shaped, settings);
            text = shaped is null ? "" : shaped.ToJsonString(settings.Pretty ? Pretty : Compact);
        }
        else
        {
            text = Decode(response.Body);
            if (response.Truncated) text += "\n…[response truncated at max_bytes]";
        }

        if (string.IsNullOrWhiteSpace(text) || text == "null")
            text = settings.EmptyMessage ?? $"Request succeeded (HTTP {response.Status}) with an empty response.";

        text = Truncate(text, settings.MaxCharsToModel);

        var prefix = new StringBuilder();
        if (settings.IncludeStatus)
            prefix.AppendLine($"HTTP {response.Status}");
        foreach (var header in settings.IncludeHeaders)
            if (response.Headers.TryGetValue(header, out var value))
                prefix.AppendLine($"{header}: {value}");
        if (prefix.Length > 0)
            text = prefix.AppendLine().Append(text).ToString();
        if (paginationNote is not null)
            text += "\n\n" + paginationNote;

        return new FormattedResult(text, shaped, false);
    }

    private static JsonNode? Shape(JsonNode? node, ResponseConfig settings)
    {
        if (node is JsonArray array)
        {
            var items = array.Select(item => ShapeObject(item, settings)).ToList();
            var dropped = 0;
            if (settings.MaxItems is { } max && items.Count > max)
            {
                dropped = items.Count - max;
                items = items.Take(max).ToList();
            }

            var result = new JsonArray(items.ToArray());
            return dropped > 0 ? new JsonObject { ["items"] = result, ["omitted_items"] = dropped } : result;
        }

        return ShapeObject(node, settings);
    }

    private static JsonNode? ShapeObject(JsonNode? node, ResponseConfig settings)
    {
        if (node is not JsonObject obj) return node?.DeepClone();

        var result = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (settings.Pick is { Count: > 0 } pick && !pick.Contains(key)) continue;
            if (settings.Omit is { } omit && omit.Contains(key)) continue;
            var name = settings.Rename?.GetValueOrDefault(key) ?? key;
            result[name] = value?.DeepClone();
        }

        return result;
    }

    private static string Decode(byte[] body) => Encoding.UTF8.GetString(body);

    private static string Truncate(string text, int max) =>
        max > 0 && text.Length > max ? text[..max] + $"\n…[truncated {text.Length - max} characters]" : text;
}
