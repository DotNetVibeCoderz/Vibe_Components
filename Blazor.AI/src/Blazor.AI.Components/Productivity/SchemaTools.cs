using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Blazor.AI.Components;

/// <summary>Local (no-AI) JSON Schema inference and code generation used by <c>JsonSchemaGenerator</c> and <c>StructuredOutputForm</c>.</summary>
public static class SchemaTools
{
    public static readonly JsonSerializerOptions Lenient = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    /// <summary>Infers a JSON Schema (draft 2020-12) from a sample document. Arrays merge the shapes of all their items.</summary>
    public static JsonObject InferSchema(JsonNode? sample, string title = "Root")
    {
        var schema = Infer(sample);
        schema.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
        schema.Insert(1, "title", title);
        return schema;
    }

    private static JsonObject Infer(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var properties = new JsonObject();
                var required = new JsonArray();
                foreach (var (key, value) in obj)
                {
                    properties[key] = Infer(value);
                    if (value is not null)
                    {
                        required.Add(key);
                    }
                }

                return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };

            case JsonArray array:
                var items = array.Count == 0 ? new JsonObject() : array.Select(Infer).Aggregate(Merge);
                return new JsonObject { ["type"] = "array", ["items"] = items };

            case JsonValue value:
                var element = value.GetValue<JsonElement>();
                return element.ValueKind switch
                {
                    JsonValueKind.True or JsonValueKind.False => new JsonObject { ["type"] = "boolean" },
                    JsonValueKind.Number => new JsonObject { ["type"] = element.TryGetInt64(out _) ? "integer" : "number" },
                    JsonValueKind.String => StringSchema(element.GetString()!),
                    _ => new JsonObject(),
                };

            default:
                return new JsonObject { ["type"] = new JsonArray("null", "string") };
        }
    }

    private static JsonObject StringSchema(string value)
    {
        var schema = new JsonObject { ["type"] = "string" };
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && value.Length >= 10 && value.Contains('-'))
        {
            schema["format"] = value.Length <= 10 ? "date" : "date-time";
        }
        else if (value.Contains('@') && value.Contains('.'))
        {
            schema["format"] = "email";
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            schema["format"] = "uri";
        }

        return schema;
    }

    private static JsonObject Merge(JsonObject a, JsonObject b)
    {
        if (a["type"]?.ToString() == "object" && b["type"]?.ToString() == "object")
        {
            var props = (JsonObject)a["properties"]!.DeepClone();
            foreach (var (key, value) in (JsonObject)b["properties"]!)
            {
                props[key] = props[key] is JsonObject existing ? Merge(existing, (JsonObject)value!) : value!.DeepClone();
            }

            var requiredA = ((JsonArray)a["required"]!).Select(x => x!.ToString()).ToHashSet();
            var required = new JsonArray(((JsonArray)b["required"]!).Select(x => x!.ToString()).Where(requiredA.Contains).Select(x => (JsonNode?)x).ToArray());
            return new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required };
        }

        if (a["type"]?.ToString() == "integer" && b["type"]?.ToString() == "number")
        {
            return b;
        }

        return a;
    }

    /// <summary>Generates C# records (one per object in the schema).</summary>
    public static string ToCSharp(JsonObject schema, string rootName = "Root", string? @namespace = "Generated")
    {
        var types = new List<string>();
        Emit(schema, Pascal(rootName), types);
        var builder = new StringBuilder();
        builder.AppendLine("using System.Text.Json.Serialization;").AppendLine();
        if (!string.IsNullOrWhiteSpace(@namespace))
        {
            builder.Append("namespace ").Append(@namespace).AppendLine(";").AppendLine();
        }

        builder.AppendJoin("\n\n", types.AsEnumerable().Reverse());
        return builder.ToString().TrimEnd() + "\n";

        static string Emit(JsonObject node, string name, List<string> types)
        {
            var type = node["type"]?.ToString();
            if (type == "object" && node["properties"] is JsonObject props)
            {
                var required = (node["required"] as JsonArray)?.Select(r => r!.ToString()).ToHashSet() ?? [];
                var lines = new List<string>();
                foreach (var (key, value) in props)
                {
                    var csType = Emit((JsonObject)value!, Pascal(key), types);
                    var nullable = required.Contains(key) || csType.EndsWith('?') ? string.Empty : "?";
                    lines.Add($"    [property: JsonPropertyName(\"{key}\")] {csType}{nullable} {Pascal(key)}");
                }

                types.Add($"public sealed record {name}(\n{string.Join(",\n", lines)});");
                return name;
            }

            return type switch
            {
                "array" => $"IReadOnlyList<{Emit(node["items"] as JsonObject ?? [], Singular(name), types)}>",
                "integer" => "long",
                "number" => "double",
                "boolean" => "bool",
                "string" => node["format"]?.ToString() switch
                {
                    "date" => "DateOnly",
                    "date-time" => "DateTimeOffset",
                    "uri" => "Uri",
                    _ => "string",
                },
                _ => "JsonElement",
            };
        }
    }

    /// <summary>Generates TypeScript interfaces.</summary>
    public static string ToTypeScript(JsonObject schema, string rootName = "Root")
    {
        var types = new List<string>();
        Emit(schema, Pascal(rootName), types);
        return string.Join("\n\n", types.AsEnumerable().Reverse()) + "\n";

        static string Emit(JsonObject node, string name, List<string> types)
        {
            var type = node["type"]?.ToString();
            if (type == "object" && node["properties"] is JsonObject props)
            {
                var required = (node["required"] as JsonArray)?.Select(r => r!.ToString()).ToHashSet() ?? [];
                var lines = props.Select(p => $"  {p.Key}{(required.Contains(p.Key) ? string.Empty : "?")}: {Emit((JsonObject)p.Value!, Pascal(p.Key), types)};");
                types.Add($"export interface {name} {{\n{string.Join('\n', lines)}\n}}");
                return name;
            }

            return type switch
            {
                "array" => $"{Emit(node["items"] as JsonObject ?? [], Singular(name), types)}[]",
                "integer" or "number" => "number",
                "boolean" => "boolean",
                "string" => "string",
                _ => "unknown",
            };
        }
    }

    /// <summary>Builds a sample instance of <paramref name="type"/>'s JSON shape (used as a fill-in template for the model).</summary>
    public static JsonNode SampleOf(Type type, int depth = 0)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (depth > 4)
        {
            return JsonValue.Create(string.Empty)!;
        }

        if (type == typeof(string) || type == typeof(Guid) || type == typeof(Uri)) return JsonValue.Create(string.Empty)!;
        if (type == typeof(bool)) return JsonValue.Create(false);
        if (type.IsEnum) return JsonValue.Create(Enum.GetNames(type).FirstOrDefault() ?? string.Empty)!;
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly)) return JsonValue.Create("2026-01-01")!;
        if (type.IsPrimitive || type == typeof(decimal)) return JsonValue.Create(0);

        var element = type.IsArray ? type.GetElementType() : type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type) ? type.GetGenericArguments()[0] : null;
        if (element is not null)
        {
            return new JsonArray(SampleOf(element, depth + 1));
        }

        var obj = new JsonObject();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite || p.GetSetMethod(true) is not null))
        {
            obj[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = SampleOf(property.PropertyType, depth + 1);
        }

        return obj;
    }

    private static string Pascal(string name)
    {
        var parts = name.Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        var result = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return result.Length == 0 ? "Item" : char.IsDigit(result[0]) ? "_" + result : result;
    }

    private static string Singular(string name) =>
        name.EndsWith("ies", StringComparison.Ordinal) ? name[..^3] + "y"
        : name.EndsWith('s') && name.Length > 3 ? name[..^1]
        : name + "Item";
}
