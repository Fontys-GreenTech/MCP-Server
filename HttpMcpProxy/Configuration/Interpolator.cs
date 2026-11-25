using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HttpMcpProxy.Configuration;

/// <summary>
/// Resolves <c>${VAR}</c>, <c>${VAR:-fallback}</c> and <c>${file:/path}</c> in every string value of a
/// parsed config. Runtime built-ins (<c>${uuid}</c>, <c>${now}</c>, <c>${today}</c>) are left untouched
/// and resolved per request by <see cref="ResolveBuiltIns"/>.
/// </summary>
public sealed partial class Interpolator(IReadOnlyDictionary<string, string> dotEnv, string baseDirectory)
{
    private static readonly HashSet<string> BuiltIns = ["uuid", "now", "today"];

    public JsonNode? Apply(JsonNode? node, string path = "$")
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    var replaced = Apply(child, $"{path}.{key}");
                    if (!ReferenceEquals(replaced, child))
                        obj[key] = replaced;
                }

                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    var replaced = Apply(child, $"{path}[{i}]");
                    if (!ReferenceEquals(replaced, child))
                        arr[i] = replaced;
                }

                return arr;
            case JsonValue value when value.TryGetValue<string>(out var s) && s.Contains("${"):
                return JsonValue.Create(Resolve(s, path));
            default:
                return node;
        }
    }

    public string Resolve(string input, string path = "value") =>
        PlaceholderRegex().Replace(input, m =>
        {
            var expr = m.Groups[1].Value;
            if (BuiltIns.Contains(expr))
                return m.Value;

            if (expr.StartsWith("file:", StringComparison.Ordinal))
            {
                var file = expr[5..];
                if (!Path.IsPathRooted(file))
                    file = Path.Combine(baseDirectory, file);
                if (!File.Exists(file))
                    throw new ConfigException($"{path}: file '{file}' referenced by ${{file:...}} does not exist");
                return File.ReadAllText(file).Trim();
            }

            string name = expr;
            string? fallback = null;
            var sep = expr.IndexOf(":-", StringComparison.Ordinal);
            if (sep >= 0)
            {
                name = expr[..sep];
                fallback = expr[(sep + 2)..];
            }

            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value) && dotEnv.TryGetValue(name, out var fromFile))
                value = fromFile;
            if (!string.IsNullOrEmpty(value))
                return value;
            if (fallback is not null)
                return fallback;
            throw new ConfigException($"{path}: environment variable '{name}' is not set");
        });

    /// <summary>Resolves the per-request built-ins ${uuid}, ${now} and ${today}.</summary>
    public static string ResolveBuiltIns(string input)
    {
        if (!input.Contains("${", StringComparison.Ordinal))
            return input;
        return input
            .Replace("${uuid}", Guid.NewGuid().ToString(), StringComparison.Ordinal)
            .Replace("${now}", DateTimeOffset.UtcNow.ToString("O"), StringComparison.Ordinal)
            .Replace("${today}", DateTime.UtcNow.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
    }

    /// <summary>Parses a .env file (KEY=VALUE lines, # comments, optional quotes, optional "export ").</summary>
    public static Dictionary<string, string> ReadDotEnv(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0)
                value = value[..hash].TrimEnd();

            result[key] = value;
        }

        return result;
    }

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex PlaceholderRegex();
}
