using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HttpMcpProxy.Configuration;

internal static class JsonHelpers
{
    /// <summary>Deep-merges <paramref name="overlay"/> onto a clone of <paramref name="baseNode"/>; objects merge, everything else replaces.</summary>
    public static JsonNode? Merge(JsonNode? baseNode, JsonNode? overlay)
    {
        if (overlay is null)
            return baseNode?.DeepClone();
        if (baseNode is not JsonObject baseObj || overlay is not JsonObject overlayObj)
            return overlay.DeepClone();

        var result = (JsonObject)baseObj.DeepClone();
        foreach (var (key, value) in overlayObj)
            result[key] = result.TryGetPropertyValue(key, out var existing) ? Merge(existing, value) : value?.DeepClone();
        return result;
    }

    public static T Bind<T>(JsonNode? node, string context) where T : new()
    {
        if (node is null)
            return new T();
        try
        {
            return node.Deserialize<T>(ConfigJson.Options) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{context}: {ex.Message}", ex);
        }
    }

    /// <summary>Simple glob match supporting * and ?.</summary>
    public static bool GlobMatch(string pattern, string value) =>
        Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
