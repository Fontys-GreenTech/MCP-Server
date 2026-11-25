using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace HttpMcpProxy.Configuration;

/// <summary>
/// Converts YAML documents into <see cref="JsonNode"/> trees so config files and JSON schemas
/// written as YAML can be handled with System.Text.Json.
/// </summary>
public static partial class YamlJson
{
    public static JsonNode? ParseFile(string path) => Parse(File.ReadAllText(path), path);

    public static JsonNode? Parse(string yaml, string? sourceName = null)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new ConfigException($"{sourceName ?? "yaml"}: invalid YAML at line {ex.Start.Line}: {ex.Message}", ex);
        }

        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => ConvertMapping(map),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(Convert).ToArray()),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => null,
    };

    private static JsonObject ConvertMapping(YamlMappingNode map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map.Children)
        {
            var name = key is YamlScalarNode s ? s.Value ?? "" : key.ToString();
            obj[name] = Convert(value);
        }

        return obj;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (scalar.Style is not ScalarStyle.Plain || value is null)
            return value is null ? null : JsonValue.Create(value);

        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (IntegerRegex().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (FloatRegex().IsMatch(value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);

        return JsonValue.Create(value);
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();
}

public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);
