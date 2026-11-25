using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HttpMcpProxy.Configuration;

/// <summary>
/// Minimal JSONPath subset: <c>$</c>, <c>.name</c>, <c>['name']</c>, <c>[0]</c>, <c>[-1]</c> and <c>[*]</c> / <c>.*</c>.
/// Wildcards fan out and return a <see cref="JsonArray"/>.
/// </summary>
public static partial class JsonPath
{
    public static bool IsValid(string path) => TryTokenize(path, out _);

    public static JsonNode? Select(JsonNode? root, string path)
    {
        if (!TryTokenize(path, out var tokens))
            throw new ArgumentException($"Unsupported JSONPath '{path}'");

        IEnumerable<JsonNode?> current = [root];
        var fannedOut = false;
        foreach (var token in tokens)
        {
            if (token == "*")
            {
                fannedOut = true;
                current = current.SelectMany(n => n switch
                {
                    JsonArray a => a.AsEnumerable(),
                    JsonObject o => o.Select(kv => kv.Value),
                    _ => [],
                }).ToList();
            }
            else if (token.StartsWith('#'))
            {
                var index = int.Parse(token[1..]);
                current = current.Select(n => n is JsonArray a && (index < 0 ? a.Count + index : index) is var i && i >= 0 && i < a.Count ? a[i] : null).ToList();
            }
            else
            {
                current = current.Select(n => n is JsonObject o && o.TryGetPropertyValue(token, out var v) ? v : null).ToList();
            }
        }

        var results = current.ToList();
        if (fannedOut)
            return new JsonArray(results.Where(r => r is not null).Select(r => r!.DeepClone()).ToArray());
        return results.FirstOrDefault()?.DeepClone();
    }

    private static bool TryTokenize(string path, out List<string> tokens)
    {
        tokens = [];
        path = path.Trim();
        if (!path.StartsWith('$'))
            return false;

        var pos = 1;
        while (pos < path.Length)
        {
            var m = TokenRegex().Match(path, pos);
            if (!m.Success || m.Index != pos)
                return false;

            if (m.Groups["name"].Success) tokens.Add(m.Groups["name"].Value);
            else if (m.Groups["quoted"].Success) tokens.Add(m.Groups["quoted"].Value);
            else if (m.Groups["index"].Success) tokens.Add("#" + m.Groups["index"].Value);
            else tokens.Add("*");
            pos += m.Length;
        }

        return true;
    }

    [GeneratedRegex(@"\.(?<name>[A-Za-z0-9_\-$@]+)|\[['""](?<quoted>[^'""]+)['""]\]|\[(?<index>-?\d+)\]|\[\*\]|\.\*")]
    private static partial Regex TokenRegex();
}
