using System.Text.Json.Serialization;
using Blazor.AI.Abstractions.Prompts;

namespace Blazor.AI.Core;

/// <summary>Source-generated JSON metadata (trimming- and AOT-friendly, no reflection at runtime).</summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<PromptTemplate>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;
