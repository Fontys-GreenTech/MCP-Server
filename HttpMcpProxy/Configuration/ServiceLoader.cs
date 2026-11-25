using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HttpMcpProxy.Configuration;

/// <summary>A fully loaded, validated service folder.</summary>
public sealed class ServiceDefinition
{
    public required string Directory { get; init; }
    public required ServiceConfig Config { get; init; }
    public required string BaseUrl { get; init; }
    public required IReadOnlySet<string> AllowedHosts { get; init; }
    public required EffectiveSettings Settings { get; init; }
    public required IReadOnlyList<ToolDefinition> Tools { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>One endpoint file turned into an MCP tool definition.</summary>
public sealed class ToolDefinition
{
    public required string Name { get; init; }
    public required string SourceFile { get; init; }
    public required EndpointConfig Endpoint { get; init; }
    public required EffectiveSettings Settings { get; init; }
    public required JsonObject InputSchema { get; init; }
    public JsonObject? OutputSchema { get; init; }
    public required string Description { get; init; }
    public required AnnotationsConfig Annotations { get; init; }
    public required bool RequiresConfirmation { get; init; }
    public string Method => Endpoint.Method;
}

public static partial class ServiceLoader
{
    public const string ConfigFileName = "config.yml";
    private static readonly string[] EndpointFolders = ["tools", "endpoints"];
    private static readonly string[] OverridableSections =
        ["http", "retry", "rate_limit", "concurrency", "circuit_breaker", "cache", "request", "response", "errors", "pagination", "annotations", "logging"];
    private static readonly HashSet<string> KnownMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];
    private static readonly HashSet<string> Separators = [".", "_", "-"];

    /// <summary>Hash of every file in a service folder; used to skip reloads when nothing changed.</summary>
    public static string Fingerprint(string directory)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar).Contains(".cache"))
                continue; // OAuth token cache etc.
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file)));
            try { sha.AppendData(File.ReadAllBytes(file)); }
            catch (IOException) { sha.AppendData(Guid.NewGuid().ToByteArray()); } // file mid-write: force reload later
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <returns>The service, or null when <c>enabled: false</c>.</returns>
    public static ServiceDefinition? Load(string directory)
    {
        var configPath = Path.Combine(directory, ConfigFileName);
        if (!File.Exists(configPath))
            throw new ConfigException($"{configPath} not found");

        var warnings = new List<string>();
        var dotEnv = Interpolator.ReadDotEnv(Path.Combine(directory, ".env"));
        var interpolator = new Interpolator(dotEnv, directory);

        var raw = YamlJson.ParseFile(configPath) as JsonObject
                  ?? throw new ConfigException($"{configPath}: expected a mapping at the top level");

        // Bail out before interpolation so a disabled service never complains about missing env vars.
        if (raw["enabled"] is JsonValue enabledValue && enabledValue.TryGetValue<bool>(out var isEnabled) && !isEnabled)
            return null;

        var configJson = (JsonObject)interpolator.Apply(raw, "config.yml")!;
        var config = JsonHelpers.Bind<ServiceConfig>(configJson, configPath);
        Validate(config, configPath);

        var settings = BuildSettings(configJson, null, configPath);
        var baseUrl = ResolveBaseUrl(config, dotEnv, configPath);
        var allowedHosts = ResolveAllowedHosts(config, baseUrl, dotEnv);

        var tools = new List<ToolDefinition>();
        var prefix = config.Tools.Prefix ?? config.Name;
        foreach (var file in EnumerateEndpointFiles(directory, config.Tools))
        {
            try
            {
                var tool = LoadEndpoint(file, configJson, config, prefix, interpolator);
                if (tool is not null)
                    tools.Add(tool);
            }
            catch (ConfigException ex)
            {
                // One broken endpoint should not take the whole service down.
                warnings.Add(ex.Message);
            }
        }

        var duplicates = tools.GroupBy(t => t.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ConfigException($"{configPath}: duplicate tool names: {string.Join(", ", duplicates)}");

        return new ServiceDefinition
        {
            Directory = directory,
            Config = config,
            BaseUrl = baseUrl,
            AllowedHosts = allowedHosts,
            Settings = settings,
            Tools = tools,
            Warnings = warnings,
        };
    }

    private static void Validate(ServiceConfig config, string path)
    {
        if (config.ConfigVersion != 1)
            throw new ConfigException($"{path}: unsupported config_version '{config.ConfigVersion}' (expected 1)");
        if (string.IsNullOrWhiteSpace(config.Name) || !ServiceNameRegex().IsMatch(config.Name))
            throw new ConfigException($"{path}: 'name' is required and may only contain a-z, 0-9, '_' and '-'");
        if (string.IsNullOrWhiteSpace(config.Description))
            throw new ConfigException($"{path}: 'description' is required");
        if (!Separators.Contains(config.Tools.Separator))
            throw new ConfigException($"{path}: tools.separator must be one of . _ -");
        if (config.BaseUrl is null && (config.Environments is null || config.Environments.Count == 0))
            throw new ConfigException($"{path}: 'base_url' is required unless 'environments' is set");

        var authTypes = new[] { "none", "api_key", "bearer", "basic", "oauth2", "custom" };
        if (!authTypes.Contains(config.Auth.Type))
            throw new ConfigException($"{path}: unknown auth.type '{config.Auth.Type}'");
        if (config.Auth.Type == "oauth2" && string.IsNullOrEmpty(config.Auth.TokenUrl))
            throw new ConfigException($"{path}: auth.token_url is required for oauth2");
        if (config.Auth.Type == "api_key" && (string.IsNullOrEmpty(config.Auth.Name) || config.Auth.Value is null))
            throw new ConfigException($"{path}: auth.name and auth.value are required for api_key");
    }

    private static EffectiveSettings BuildSettings(JsonObject configJson, JsonObject? endpointJson, string context)
    {
        var merged = new JsonObject();
        foreach (var section in OverridableSections)
        {
            var node = configJson[section]?.DeepClone();
            if (endpointJson is not null)
            {
                node = JsonHelpers.Merge(node, endpointJson["overrides"]?[section]);
                // These sections may also appear at the top level of an endpoint file.
                if (section is "response" or "errors" or "pagination" or "annotations")
                    node = JsonHelpers.Merge(node, endpointJson[section]);
            }

            if (node is not null)
                merged[section] = node;
        }

        // Pagination-mode keys live next to the pagination block.
        foreach (var key in new[] { "pagination_mode", "max_pages", "items_path" })
        {
            var value = endpointJson?[key] ?? configJson[key];
            if (value is not null)
                merged[key] = value.DeepClone();
        }

        return JsonHelpers.Bind<EffectiveSettings>(merged, context);
    }

    private static string ResolveBaseUrl(ServiceConfig config, IReadOnlyDictionary<string, string> dotEnv, string path)
    {
        var template = config.BaseUrl;
        if (config.Environments is { Count: > 0 } envs)
        {
            var active = string.IsNullOrEmpty(config.ActiveEnvironment) ? envs.Keys.First() : config.ActiveEnvironment;
            if (!envs.TryGetValue(active, out var env))
                throw new ConfigException($"{path}: active_environment '{active}' is not defined in 'environments'");
            template = env.BaseUrl ?? template;
        }

        if (string.IsNullOrEmpty(template))
            throw new ConfigException($"{path}: no base_url for the active environment");

        var url = SubstituteUrlVariables(template, config, dotEnv, path, strict: true).TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ConfigException($"{path}: base_url '{url}' is not an absolute http(s) URL");
        return url;
    }

    private static string SubstituteUrlVariables(string template, ServiceConfig config, IReadOnlyDictionary<string, string> dotEnv, string path, bool strict) =>
        UrlVariableRegex().Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            if (!config.UrlVariables.TryGetValue(name, out var variable))
                return strict ? throw new ConfigException($"{path}: base_url uses {{{name}}} but url_variables.{name} is not defined") : m.Value;

            string? value = null;
            if (variable.FromEnv is { } envName)
                value = Environment.GetEnvironmentVariable(envName) is { Length: > 0 } v ? v : dotEnv.GetValueOrDefault(envName);
            value = string.IsNullOrEmpty(value) ? variable.Default : value;

            if (value is null)
                throw new ConfigException($"{path}: url_variables.{name} has no value");
            if (variable.Enum is { Count: > 0 } allowed && !allowed.Contains(value))
                throw new ConfigException($"{path}: url_variables.{name} = '{value}' is not one of [{string.Join(", ", allowed)}]");
            return value;
        });

    private static HashSet<string> ResolveAllowedHosts(ServiceConfig config, string baseUrl, IReadOnlyDictionary<string, string> dotEnv)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config.Security.AllowedHosts is { } explicitHosts)
        {
            hosts.UnionWith(explicitHosts);
            return hosts;
        }

        hosts.Add(new Uri(baseUrl).Host);
        foreach (var candidate in (config.Environments?.Values.Select(e => e.BaseUrl) ?? []).Append(config.BaseUrl))
        {
            if (candidate is null) continue;
            var url = SubstituteUrlVariables(candidate, config, dotEnv, "", strict: false);
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                hosts.Add(uri.Host);
        }

        return hosts;
    }

    private static IEnumerable<string> EnumerateEndpointFiles(string directory, ToolsConfig tools)
    {
        foreach (var folder in EndpointFolders)
        {
            var dir = Path.Combine(directory, folder);
            if (!System.IO.Directory.Exists(dir))
                continue;

            foreach (var file in System.IO.Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
            {
                var ext = Path.GetExtension(file);
                if (ext is not (".yml" or ".yaml"))
                    continue;

                var fileName = Path.GetFileName(file);
                var stem = Path.GetFileNameWithoutExtension(file);
                bool Matches(string glob) => JsonHelpers.GlobMatch(glob, stem) || JsonHelpers.GlobMatch(glob, fileName);

                if (tools.Include.Any(Matches) && !tools.Exclude.Any(Matches))
                    yield return file;
            }
        }
    }

    private static ToolDefinition? LoadEndpoint(string file, JsonObject configJson, ServiceConfig config, string prefix, Interpolator interpolator)
    {
        var raw = YamlJson.ParseFile(file) as JsonObject
                  ?? throw new ConfigException($"{file}: expected a mapping at the top level");
        if (raw["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled) && !enabled)
            return null;

        var endpointJson = (JsonObject)interpolator.Apply(raw, Path.GetFileName(file))!;
        if (endpointJson["overrides"] is JsonObject ov && (ov.ContainsKey("auth") || ov.ContainsKey("base_url")))
            throw new ConfigException($"{file}: 'auth' and 'base_url' cannot be overridden per endpoint");

        var endpoint = JsonHelpers.Bind<EndpointConfig>(endpointJson, file);
        endpoint.Method = endpoint.Method.ToUpperInvariant();
        if (!KnownMethods.Contains(endpoint.Method))
            throw new ConfigException($"{file}: 'method' is required and must be one of {string.Join(", ", KnownMethods)}");
        if (string.IsNullOrWhiteSpace(endpoint.Path))
            throw new ConfigException($"{file}: 'path' is required");
        if (string.IsNullOrWhiteSpace(endpoint.Description))
            throw new ConfigException($"{file}: 'description' is required");

        var security = config.Security;
        if (security.ReadOnly && endpoint.Method is not ("GET" or "HEAD"))
            return null;
        if (security.AllowedMethods is { } allowedMethods && !allowedMethods.Contains(endpoint.Method, StringComparer.OrdinalIgnoreCase))
            throw new ConfigException($"{file}: method {endpoint.Method} is not in security.allowed_methods");

        var stem = Path.GetFileNameWithoutExtension(file);
        var name = $"{prefix}{config.Tools.Separator}{stem}";
        if (!ToolNameRegex().IsMatch(name))
            throw new ConfigException($"{file}: tool name '{name}' is invalid (allowed: A-Z a-z 0-9 _ - . and max 128 chars)");

        var settings = BuildSettings(configJson, endpointJson, file);
        ValidateParams(endpoint, file);

        if (endpoint.Body is { Schema: { } bodySchema } body)
            body.Schema = (JsonObject)ResolveRefs(bodySchema, Path.GetDirectoryName(file)!, 0);
        if (settings.Response.OutputSchema is JsonObject output)
            settings.Response.OutputSchema = ResolveRefs(output, Path.GetDirectoryName(file)!, 0);

        foreach (var path in new[] { settings.Response.Select, settings.ItemsPath, settings.Pagination.NextCursorPath, settings.Pagination.HasMorePath, settings.Pagination.TotalPath, settings.Pagination.TotalPagesPath })
            if (path is not null && !JsonPath.IsValid(path))
                throw new ConfigException($"{file}: unsupported JSONPath '{path}'");

        return new ToolDefinition
        {
            Name = name,
            SourceFile = file,
            Endpoint = endpoint,
            Settings = settings,
            InputSchema = SchemaBuilder.BuildInputSchema(endpoint, settings),
            OutputSchema = settings.Response.OutputSchema as JsonObject,
            Description = BuildDescription(endpoint),
            Annotations = BuildAnnotations(endpoint, settings),
            RequiresConfirmation = endpoint.Confirm ?? security.RequireConfirmationFor.Contains(endpoint.Method, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static void ValidateParams(EndpointConfig endpoint, string file)
    {
        var seen = new HashSet<string>();
        foreach (var p in endpoint.Params)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
                throw new ConfigException($"{file}: every param needs a 'name'");
            if (!seen.Add(p.Name))
                throw new ConfigException($"{file}: duplicate param '{p.Name}'");
            if (p.In is not ("path" or "query" or "header" or "cookie"))
                throw new ConfigException($"{file}: param '{p.Name}' has invalid 'in: {p.In}'");
            if (p.In == "path")
            {
                p.Required = true;
                if (!endpoint.Path.Contains("{" + p.WireName + "}", StringComparison.Ordinal))
                    throw new ConfigException($"{file}: path param '{p.Name}' does not appear in path '{endpoint.Path}'");
            }

            if (p.Pattern is not null)
            {
                try { _ = new Regex(p.Pattern); }
                catch (ArgumentException) { throw new ConfigException($"{file}: param '{p.Name}' has an invalid pattern"); }
            }
        }

        foreach (Match m in UrlVariableRegex().Matches(endpoint.Path))
            if (!endpoint.Params.Any(p => p.In == "path" && p.WireName == m.Groups[1].Value))
                throw new ConfigException($"{file}: path placeholder {{{m.Groups[1].Value}}} has no matching 'in: path' param");
    }

    private static string BuildDescription(EndpointConfig endpoint)
    {
        var sb = new StringBuilder();
        if (endpoint.Deprecated)
            sb.AppendLine("DEPRECATED: avoid using this tool unless no alternative exists.").AppendLine();
        sb.Append(endpoint.Description.Trim());

        if (endpoint.Examples.Count > 0)
        {
            sb.AppendLine().AppendLine().AppendLine("Examples:");
            foreach (var example in endpoint.Examples)
            {
                sb.Append("- ");
                if (!string.IsNullOrEmpty(example.Description))
                    sb.Append(example.Description).Append(": ");
                sb.AppendLine(example.Arguments?.ToJsonString() ?? "{}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static AnnotationsConfig BuildAnnotations(EndpointConfig endpoint, EffectiveSettings settings)
    {
        var configured = settings.Annotations;
        var method = endpoint.Method;
        return new AnnotationsConfig
        {
            Title = endpoint.Title ?? configured.Title,
            ReadOnlyHint = configured.ReadOnlyHint ?? method is "GET" or "HEAD",
            IdempotentHint = configured.IdempotentHint ?? method is "GET" or "HEAD" or "PUT" or "DELETE",
            DestructiveHint = configured.DestructiveHint ?? method is "DELETE",
            OpenWorldHint = configured.OpenWorldHint ?? true,
        };
    }

    /// <summary>Inlines <c>$ref</c>s that point at other files (YAML or JSON), relative to the referencing file.</summary>
    private static JsonNode ResolveRefs(JsonNode node, string baseDir, int depth)
    {
        if (depth > 32)
            throw new ConfigException($"$ref nesting too deep (cycle?) under {baseDir}");

        switch (node)
        {
            case JsonObject obj when obj["$ref"] is JsonValue refValue && refValue.TryGetValue<string>(out var reference) && !reference.StartsWith('#'):
            {
                var hash = reference.IndexOf('#');
                var filePart = hash >= 0 ? reference[..hash] : reference;
                var pointer = hash >= 0 ? reference[(hash + 1)..] : "";
                var fullPath = Path.GetFullPath(Path.Combine(baseDir, filePart));
                if (!File.Exists(fullPath))
                    throw new ConfigException($"$ref '{reference}' not found (resolved to {fullPath})");

                JsonNode? target = fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? JsonNode.Parse(File.ReadAllText(fullPath))
                    : YamlJson.ParseFile(fullPath);
                foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    target = target?[segment.Replace("~1", "/").Replace("~0", "~")];
                if (target is not JsonObject targetObj)
                    throw new ConfigException($"$ref '{reference}' does not point at an object");

                targetObj = (JsonObject)ResolveRefs(targetObj.DeepClone(), Path.GetDirectoryName(fullPath)!, depth + 1);
                targetObj.Remove("$schema");
                // Sibling keywords next to $ref (e.g. description) win over the referenced schema.
                foreach (var (key, value) in obj)
                    if (key != "$ref")
                        targetObj[key] = value?.DeepClone();
                return targetObj;
            }
            case JsonObject obj:
            {
                var clone = new JsonObject();
                foreach (var (key, value) in obj)
                    clone[key] = value is null ? null : ResolveRefs(value, baseDir, depth);
                return clone;
            }
            case JsonArray arr:
                return new JsonArray(arr.Select(v => v is null ? null : ResolveRefs(v, baseDir, depth)).ToArray());
            default:
                return node.DeepClone();
        }
    }

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex ServiceNameRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$")]
    private static partial Regex ToolNameRegex();

    [GeneratedRegex(@"\{([A-Za-z0-9_\-]+)\}")]
    private static partial Regex UrlVariableRegex();
}
