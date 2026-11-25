using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HttpMcpProxy.Configuration;

namespace HttpMcpProxy.Execution;

/// <summary>An HTTP request ready to send; <see cref="CreateContent"/> is called once per attempt.</summary>
internal sealed class PreparedRequest
{
    public required HttpMethod Method { get; init; }
    public required Uri Url { get; set; }
    public required List<KeyValuePair<string, string>> Headers { get; init; }
    public Func<HttpContent>? CreateContent { get; init; }
    public string? BodyText { get; init; }
}

internal static class RequestBuilder
{
    private static readonly HashSet<string> BodyMethods = ["POST", "PUT", "PATCH"];

    /// <summary>Applies defaults and checks the arguments against param constraints. Returns error messages.</summary>
    public static List<string> ValidateAndApplyDefaults(ToolDefinition tool, JsonObject args)
    {
        var errors = new List<string>();
        foreach (var p in tool.Endpoint.Params.Where(p => p.IsExposed))
        {
            var present = args.TryGetPropertyValue(p.Name, out var value);
            if ((!present || value is null) && p.Default is not null)
            {
                args[p.Name] = p.Default.DeepClone();
                continue;
            }

            if (!present || value is null)
            {
                if (p.Required) errors.Add($"'{p.Name}' is required");
                else if (present && !p.Nullable && p.Type != "null") { /* explicit null on optional param: dropped later */ }
                continue;
            }

            ValidateValue(p, value, errors);
        }

        if (tool.Endpoint.Body is { Schema: { } schema } body)
        {
            var bodyArgs = body.IsSpread ? args : args[body.ArgName] as JsonObject;
            if (!body.IsSpread && args[body.ArgName] is null)
                errors.Add($"'{body.ArgName}' is required");
            else if (bodyArgs is not null && schema["required"] is JsonArray required)
                foreach (var name in required.Select(r => r!.GetValue<string>()))
                    if (bodyArgs[name] is null)
                        errors.Add($"'{name}' is required");

            if (bodyArgs is not null && schema["properties"] is JsonObject props)
                foreach (var (name, propSchema) in props)
                    if (bodyArgs[name] is null && propSchema?["default"] is { } def)
                        bodyArgs[name] = def.DeepClone();
        }

        return errors;
    }

    private static void ValidateValue(ParamConfig p, JsonNode value, List<string> errors)
    {
        var kind = value.GetValueKind();
        var typeOk = p.Type switch
        {
            "string" => kind == JsonValueKind.String,
            "integer" => kind == JsonValueKind.Number && value.GetValue<double>() % 1 == 0,
            "number" => kind == JsonValueKind.Number,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "array" => kind == JsonValueKind.Array,
            "object" => kind == JsonValueKind.Object,
            _ => true,
        };
        if (!typeOk)
        {
            errors.Add($"'{p.Name}' must be of type {p.Type}");
            return;
        }

        if (p.Enum is { } allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
            errors.Add($"'{p.Name}' must be one of {allowed.ToJsonString()}");

        if (kind == JsonValueKind.String)
        {
            var s = value.GetValue<string>();
            if (p.MinLength is { } min && s.Length < min) errors.Add($"'{p.Name}' must be at least {min} characters");
            if (p.MaxLength is { } max && s.Length > max) errors.Add($"'{p.Name}' must be at most {max} characters");
            if (p.Pattern is { } pattern && !Regex.IsMatch(s, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
                errors.Add($"'{p.Name}' must match {pattern}");
        }
        else if (kind == JsonValueKind.Number)
        {
            var n = value.GetValue<double>();
            if (p.Minimum is { } min && n < min) errors.Add($"'{p.Name}' must be >= {min}");
            if (p.Maximum is { } max && n > max) errors.Add($"'{p.Name}' must be <= {max}");
            if (p.ExclusiveMinimum is { } emin && n <= emin) errors.Add($"'{p.Name}' must be > {emin}");
            if (p.ExclusiveMaximum is { } emax && n >= emax) errors.Add($"'{p.Name}' must be < {emax}");
            if (p.MultipleOf is { } mult && mult > 0 && Math.Abs(n / mult - Math.Round(n / mult)) > 1e-9)
                errors.Add($"'{p.Name}' must be a multiple of {mult}");
        }
        else if (value is JsonArray array)
        {
            if (p.MinItems is { } min && array.Count < min) errors.Add($"'{p.Name}' needs at least {min} items");
            if (p.MaxItems is { } max && array.Count > max) errors.Add($"'{p.Name}' allows at most {max} items");
            if (p.UniqueItems == true && array.Select(a => a?.ToJsonString()).Distinct().Count() != array.Count)
                errors.Add($"'{p.Name}' items must be unique");
            if (p.Items?["enum"] is JsonArray itemEnum)
                foreach (var item in array)
                    if (!itemEnum.Any(a => JsonNode.DeepEquals(a, item)))
                        errors.Add($"'{p.Name}' items must be one of {itemEnum.ToJsonString()}");
        }
    }

    public static PreparedRequest Build(ServiceRuntime runtime, ToolDefinition tool, JsonObject args, IReadOnlyDictionary<string, JsonNode?>? extraQuery = null)
    {
        var settings = tool.Settings;
        var request = settings.Request;
        var endpoint = tool.Endpoint;

        var query = new List<KeyValuePair<string, string>>();
        var headers = new List<KeyValuePair<string, string>>();
        var cookies = new List<KeyValuePair<string, string>>();

        foreach (var (k, v) in settings.Http.Query) query.Add(new(k, Interpolator.ResolveBuiltIns(v)));
        foreach (var (k, v) in settings.Http.Headers) headers.Add(new(k, Interpolator.ResolveBuiltIns(v)));
        headers.Add(new("User-Agent", settings.Http.UserAgent ?? "http-mcp-proxy/1.0"));

        var path = endpoint.Path;
        var consumed = new HashSet<string>();
        foreach (var p in endpoint.Params)
        {
            consumed.Add(p.Name);
            JsonNode? value = p.Const ?? p.Value ?? args[p.Name];
            if (value is JsonValue sv && sv.TryGetValue<string>(out var str))
                value = JsonValue.Create(Interpolator.ResolveBuiltIns(str));

            if (value is null && request.DropNullParams) continue;
            if (value is JsonValue ev && ev.TryGetValue<string>(out var empty) && empty.Length == 0 && request.DropEmptyStrings) continue;

            switch (p.In)
            {
                case "path":
                    var raw = Scalar(value);
                    var encoded = request.PathParamEncoding == "allow_slash"
                        ? string.Join('/', raw.Split('/').Select(Uri.EscapeDataString))
                        : Uri.EscapeDataString(raw);
                    path = path.Replace("{" + p.WireName + "}", encoded, StringComparison.Ordinal);
                    break;
                case "query":
                    AddQueryValue(query, p.WireName, value, p.ArrayStyle ?? request.QueryArrayStyle);
                    break;
                case "header":
                    headers.Add(new(p.WireName, value is JsonArray ha ? string.Join(',', ha.Select(Scalar)) : Scalar(value)));
                    break;
                case "cookie":
                    cookies.Add(new(p.WireName, Scalar(value)));
                    break;
            }
        }

        if (extraQuery is not null)
            foreach (var (k, v) in extraQuery)
                if (v is not null) AddQueryValue(query, k, v, request.QueryArrayStyle);

        runtime.Auth.AddQuery(query);
        runtime.Auth.AddCookies(cookies);
        if (cookies.Count > 0)
            headers.Add(new("Cookie", string.Join("; ", cookies.Select(c => $"{c.Key}={Uri.EscapeDataString(c.Value)}"))));

        var url = runtime.Definition.BaseUrl + (path.StartsWith('/') ? path : "/" + path);
        if (query.Count > 0)
            url += (url.Contains('?') ? "&" : "?") + string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));

        Func<HttpContent>? content = null;
        string? bodyText = null;
        if (BodyMethods.Contains(endpoint.Method) && endpoint.Body is { } body)
        {
            var bodyNode = BuildBody(body, args, consumed);
            var contentType = body.ContentType ?? request.ContentType;
            (content, bodyText) = CreateContent(bodyNode, contentType);
        }

        return new PreparedRequest
        {
            Method = new HttpMethod(endpoint.Method),
            Url = new Uri(url),
            Headers = headers,
            CreateContent = content,
            BodyText = bodyText,
        };
    }

    private static JsonNode? BuildBody(BodyConfig body, JsonObject args, HashSet<string> consumedByParams)
    {
        JsonNode? node;
        if (body.IsSpread)
        {
            var obj = new JsonObject();
            var known = (body.Schema?["properties"] as JsonObject)?.Select(kv => kv.Key).ToHashSet();
            foreach (var (key, value) in args)
            {
                if (consumedByParams.Contains(key)) continue;
                if (known is not null && !known.Contains(key)) continue;
                obj[key] = value?.DeepClone();
            }

            node = obj;
        }
        else
        {
            node = args[body.ArgName]?.DeepClone();
        }

        if (body.Template is not null)
            node = JsonHelpers.Merge(node ?? new JsonObject(), ResolveBuiltIns(body.Template));
        if (body.WrapIn is { Length: > 0 } wrap)
            node = new JsonObject { [wrap] = node };
        return node;
    }

    private static JsonNode ResolveBuiltIns(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, kv.Value is null ? null : ResolveBuiltIns(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(v => v is null ? null : ResolveBuiltIns(v)).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Interpolator.ResolveBuiltIns(s)),
        _ => node.DeepClone(),
    };

    private static (Func<HttpContent>, string) CreateContent(JsonNode? body, string contentType)
    {
        switch (contentType)
        {
            case "form":
            {
                var fields = Flatten(body).ToList();
                return (() => new FormUrlEncodedContent(fields), string.Join('&', fields.Select(f => $"{f.Key}={f.Value}")));
            }
            case "multipart":
            {
                var fields = Flatten(body).ToList();
                return (() =>
                {
                    var multipart = new MultipartFormDataContent();
                    foreach (var (k, v) in fields) multipart.Add(new StringContent(v), k);
                    return multipart;
                }, string.Join('&', fields.Select(f => $"{f.Key}={f.Value}")));
            }
            case "text":
            {
                var text = body is JsonValue v && v.TryGetValue<string>(out var s) ? s : body?.ToJsonString() ?? "";
                return (() => new StringContent(text, Encoding.UTF8, "text/plain"), text);
            }
            default:
            {
                var json = body?.ToJsonString() ?? "null";
                return (() =>
                {
                    var c = new StringContent(json, Encoding.UTF8);
                    c.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                    return c;
                }, json);
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> Flatten(JsonNode? body)
    {
        if (body is not JsonObject obj) yield break;
        foreach (var (key, value) in obj)
        {
            if (value is null) continue;
            if (value is JsonArray arr)
                foreach (var item in arr) yield return new(key, Scalar(item));
            else
                yield return new(key, value is JsonObject ? value.ToJsonString() : Scalar(value));
        }
    }

    private static void AddQueryValue(List<KeyValuePair<string, string>> query, string name, JsonNode? value, string style)
    {
        if (value is JsonArray array)
        {
            switch (style)
            {
                case "comma":
                    query.Add(new(name, string.Join(',', array.Select(Scalar))));
                    break;
                case "brackets":
                    foreach (var item in array) query.Add(new(name + "[]", Scalar(item)));
                    break;
                default:
                    foreach (var item in array) query.Add(new(name, Scalar(item)));
                    break;
            }
        }
        else
        {
            query.Add(new(name, Scalar(value)));
        }
    }

    public static string Scalar(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "true",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "false",
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };
}
