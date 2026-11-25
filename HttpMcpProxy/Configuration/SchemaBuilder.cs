using System.Text.Json.Nodes;

namespace HttpMcpProxy.Configuration;

/// <summary>Builds the MCP tool inputSchema from endpoint params, body schema and pagination settings.</summary>
public static class SchemaBuilder
{
    /// <summary>Argument name used for link_header pagination (an opaque next-page URL).</summary>
    public const string NextPageUrlArg = "next_page_url";

    public static JsonObject BuildInputSchema(EndpointConfig endpoint, EffectiveSettings settings)
    {
        var properties = new JsonObject();
        var required = new List<string>();

        foreach (var p in endpoint.Params.Where(p => p.IsExposed))
        {
            properties[p.Name] = ParamSchema(p);
            if (p.Required && p.Default is null)
                required.Add(p.Name);
        }

        if (endpoint.Body is { Schema: { } schema } body)
        {
            if (body.IsSpread)
            {
                if (schema["properties"] is JsonObject bodyProps)
                {
                    foreach (var (key, value) in bodyProps)
                    {
                        if (properties.ContainsKey(key))
                            throw new ConfigException($"body property '{key}' collides with a param of the same name; use body.arg_name");
                        properties[key] = value?.DeepClone();
                    }
                }

                if (schema["required"] is JsonArray bodyRequired)
                    required.AddRange(bodyRequired.Select(r => r!.GetValue<string>()));
            }
            else
            {
                var clone = (JsonObject)schema.DeepClone();
                clone.Remove("$schema");
                properties[body.ArgName] = clone;
                required.Add(body.ArgName);
            }
        }

        if (settings.PaginationMode == "single")
            AddPaginationArgs(properties, endpoint, settings.Pagination);

        var result = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
            result["required"] = new JsonArray(required.Distinct().Select(r => (JsonNode)r).ToArray());
        if (endpoint.Body is { IsSpread: true, Schema: { } s } && s["additionalProperties"] is JsonValue ap && !ap.GetValue<bool>())
            result["additionalProperties"] = false;
        return result;
    }

    private static void AddPaginationArgs(JsonObject properties, EndpointConfig endpoint, PaginationConfig pagination)
    {
        void Add(string name, JsonObject schema)
        {
            if (!properties.ContainsKey(name) && endpoint.Params.All(p => p.Name != name))
                properties[name] = schema;
        }

        switch (pagination.Type)
        {
            case "offset":
                Add(pagination.OffsetParam, new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Number of items to skip (pagination)" });
                Add(pagination.LimitParam, LimitSchema(pagination.DefaultLimit, pagination.MaxLimit, "Maximum number of items to return"));
                break;
            case "page":
                Add(pagination.PageParam, new JsonObject { ["type"] = "integer", ["minimum"] = pagination.FirstPage, ["description"] = $"Page number (first page is {pagination.FirstPage})" });
                Add(pagination.SizeParam, LimitSchema(pagination.DefaultSize, pagination.MaxSize, "Items per page"));
                break;
            case "cursor":
                Add(pagination.CursorParam, new JsonObject { ["type"] = "string", ["description"] = "Cursor from a previous response to fetch the next page; omit for the first page" });
                if (pagination.DefaultLimit is not null || pagination.MaxLimit is not null)
                    Add(pagination.LimitParam, LimitSchema(pagination.DefaultLimit, pagination.MaxLimit, "Maximum number of items to return"));
                break;
            case "link_header":
                Add(NextPageUrlArg, new JsonObject { ["type"] = "string", ["description"] = "Next-page URL from a previous response; omit for the first page" });
                break;
        }
    }

    private static JsonObject LimitSchema(int? defaultValue, int? max, string description)
    {
        var schema = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = description };
        if (defaultValue is not null) schema["default"] = defaultValue;
        if (max is not null) schema["maximum"] = max;
        return schema;
    }

    private static JsonObject ParamSchema(ParamConfig p)
    {
        var schema = new JsonObject();
        schema["type"] = p.Nullable ? new JsonArray(p.Type, "null") : p.Type;
        if (p.Description is not null) schema["description"] = p.Description;
        if (p.Enum is not null) schema["enum"] = p.Enum.DeepClone();
        if (p.Default is not null) schema["default"] = p.Default.DeepClone();
        if (p.Format is not null) schema["format"] = p.Format;
        if (p.Examples is not null) schema["examples"] = p.Examples.DeepClone();
        if (p.Deprecated) schema["deprecated"] = true;

        if (p.MinLength is not null) schema["minLength"] = p.MinLength;
        if (p.MaxLength is not null) schema["maxLength"] = p.MaxLength;
        if (p.Pattern is not null) schema["pattern"] = p.Pattern;

        if (p.Minimum is not null) schema["minimum"] = p.Minimum;
        if (p.Maximum is not null) schema["maximum"] = p.Maximum;
        if (p.ExclusiveMinimum is not null) schema["exclusiveMinimum"] = p.ExclusiveMinimum;
        if (p.ExclusiveMaximum is not null) schema["exclusiveMaximum"] = p.ExclusiveMaximum;
        if (p.MultipleOf is not null) schema["multipleOf"] = p.MultipleOf;

        if (p.Items is not null) schema["items"] = p.Items.DeepClone();
        if (p.MinItems is not null) schema["minItems"] = p.MinItems;
        if (p.MaxItems is not null) schema["maxItems"] = p.MaxItems;
        if (p.UniqueItems is not null) schema["uniqueItems"] = p.UniqueItems;
        return schema;
    }
}
