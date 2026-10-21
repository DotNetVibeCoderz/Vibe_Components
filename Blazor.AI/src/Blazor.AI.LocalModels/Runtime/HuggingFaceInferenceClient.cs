using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blazor.AI.LocalModels;

/// <summary>A label with a score (classification, sentiment, zero-shot).</summary>
public readonly record struct LabelScore(string Label, double Score);

/// <summary>A named entity span.</summary>
public readonly record struct EntitySpan(string Text, string Label, double Score, int Start, int End);

/// <summary>Thrown when the hosted Inference API refuses a call; the message tells the user what to do.</summary>
public sealed class HuggingFaceApiException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Thin client for the hosted Hugging Face Inference API (hf-inference provider).</summary>
public sealed class HuggingFaceInferenceClient(HttpClient http, LocalModelOptions options)
{
    public bool HasToken => !string.IsNullOrWhiteSpace(options.HuggingFaceToken);

    public async Task<IReadOnlyList<LabelScore>> ClassifyAsync(string model, string text, CancellationToken cancellationToken = default)
    {
        var node = await PostJsonAsync(model, new JsonObject { ["inputs"] = text }, cancellationToken).ConfigureAwait(false);
        var array = node is JsonArray { Count: > 0 } outer && outer[0] is JsonArray inner ? inner : node as JsonArray ?? [];
        return array.OfType<JsonObject>().Select(o => new LabelScore(o["label"]!.ToString(), o["score"]!.GetValue<double>())).OrderByDescending(l => l.Score).ToArray();
    }

    public async Task<IReadOnlyList<LabelScore>> ZeroShotAsync(string model, string text, IReadOnlyList<string> labels, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["inputs"] = text, ["parameters"] = new JsonObject { ["candidate_labels"] = new JsonArray(labels.Select(l => (JsonNode?)l).ToArray()) } };
        var node = await PostJsonAsync(model, body, cancellationToken).ConfigureAwait(false);
        if (node is JsonObject { } obj && obj["labels"] is JsonArray l && obj["scores"] is JsonArray s)
        {
            return l.Select((x, i) => new LabelScore(x!.ToString(), s[i]!.GetValue<double>())).ToArray();
        }

        return (node as JsonArray ?? []).OfType<JsonObject>().Select(o => new LabelScore(o["label"]!.ToString(), o["score"]!.GetValue<double>())).ToArray();
    }

    public async Task<IReadOnlyList<EntitySpan>> EntitiesAsync(string model, string text, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["inputs"] = text, ["parameters"] = new JsonObject { ["aggregation_strategy"] = "simple" } };
        var node = await PostJsonAsync(model, body, cancellationToken).ConfigureAwait(false);
        return (node as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => new EntitySpan(o["word"]?.ToString() ?? string.Empty, o["entity_group"]?.ToString() ?? o["entity"]?.ToString() ?? "?", o["score"]?.GetValue<double>() ?? 0, o["start"]?.GetValue<int>() ?? 0, o["end"]?.GetValue<int>() ?? 0))
            .ToArray();
    }

    public async Task<float[]> EmbedAsync(string model, string text, CancellationToken cancellationToken = default)
    {
        var node = await PostJsonAsync(model + "/pipeline/feature-extraction", new JsonObject { ["inputs"] = text }, cancellationToken).ConfigureAwait(false);
        // Either a pooled vector [d] or token vectors [[d]...]; mean-pool the latter.
        if (node is JsonArray arr && arr.Count > 0 && arr[0] is JsonArray)
        {
            var rows = arr.OfType<JsonArray>().Select(r => r.Select(v => v!.GetValue<float>()).ToArray()).ToList();
            if (rows.Count > 0 && rows[0].Length > 0 && rows.All(r => r.Length == rows[0].Length) && arr[0]![0] is JsonValue)
            {
                return Enumerable.Range(0, rows[0].Length).Select(d => rows.Average(r => r[d])).ToArray();
            }
        }

        return (node as JsonArray ?? []).Select(v => v!.GetValue<float>()).ToArray();
    }

    public async Task<IReadOnlyList<LabelScore>> ClassifyImageAsync(string model, byte[] image, string contentType, CancellationToken cancellationToken = default)
    {
        var node = await PostBytesAsync(model, image, contentType, cancellationToken).ConfigureAwait(false);
        return (node as JsonArray ?? []).OfType<JsonObject>().Select(o => new LabelScore(o["label"]!.ToString(), o["score"]!.GetValue<double>())).ToArray();
    }

    public async Task<string> ImageToTextAsync(string model, byte[] image, string contentType, CancellationToken cancellationToken = default)
    {
        var node = await PostBytesAsync(model, image, contentType, cancellationToken).ConfigureAwait(false);
        return node is JsonArray { Count: > 0 } a ? a[0]?["generated_text"]?.ToString() ?? string.Empty : node?["generated_text"]?.ToString() ?? string.Empty;
    }

    public async Task<string> TranscribeAsync(string model, byte[] audio, string contentType, CancellationToken cancellationToken = default)
    {
        var node = await PostBytesAsync(model, audio, contentType, cancellationToken).ConfigureAwait(false);
        return node?["text"]?.ToString() ?? string.Empty;
    }

    /// <summary>Returns audio bytes (usually FLAC) and their content type.</summary>
    public async Task<(byte[] Audio, string ContentType)> SpeakAsync(string model, string text, CancellationToken cancellationToken = default)
    {
        using var request = Request(model);
        request.Content = JsonContent.Create(new JsonObject { ["inputs"] = text });
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), response.Content.Headers.ContentType?.MediaType ?? "audio/flac");
    }

    private async Task<JsonNode?> PostJsonAsync(string model, JsonObject body, CancellationToken cancellationToken)
    {
        using var request = Request(model);
        request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<JsonNode?> PostBytesAsync(string model, byte[] data, string contentType, CancellationToken cancellationToken)
    {
        using var request = Request(model);
        request.Content = new ByteArrayContent(data);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private HttpRequestMessage Request(string model)
    {
        if (!HasToken)
        {
            throw new HuggingFaceApiException("The hosted Hugging Face API needs an access token. Set LocalModelOptions.HuggingFaceToken, or switch the engine to Local.", 401);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.InferenceEndpoint.TrimEnd('/') + "/" + model));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.HuggingFaceToken);
        return request;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? detail = null;
        try
        {
            detail = JsonNode.Parse(body)?["error"]?.ToString();
        }
        catch (JsonException)
        {
        }

        var status = (int)response.StatusCode;
        var message = status switch
        {
            402 => "Your Hugging Face account has no Inference credits left. Switch the engine to Local (free, runs on this server) or add credits.",
            401 or 403 => "Hugging Face rejected the token. Check that it is valid and has Inference permission.",
            404 => "This model is not served by the hosted Inference API. Try another model or run it locally.",
            503 => "The model is loading on Hugging Face. Try again in a moment.",
            _ => $"Hugging Face API error {status}: {detail ?? body[..Math.Min(200, body.Length)]}",
        };
        if (detail is not null && detail.Contains("credits", StringComparison.OrdinalIgnoreCase))
        {
            message = "Your Hugging Face account has no Inference credits left. Switch the engine to Local (free, runs on this server) or add credits.";
        }

        throw new HuggingFaceApiException(message, status);
    }
}

/// <summary>Post-processing for token-classification output.</summary>
public static class EntityMerger
{
    /// <summary>
    /// Merges word pieces the model split (e.g. "Jo" + "ko Widodo") into one span when they are the same type and
    /// adjacent or separated only by whitespace, and re-reads the span text from the source.
    /// </summary>
    public static List<EntitySpan> Merge(string text, IEnumerable<EntitySpan> entities)
    {
        var merged = new List<EntitySpan>();
        foreach (var e in entities.Where(e => e.End > e.Start && e.End <= text.Length).OrderBy(e => e.Start))
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                var gap = e.Start - last.End;
                if (last.Label == e.Label && gap >= 0 && gap <= 1 && text.AsSpan(last.End, gap).IsWhiteSpace())
                {
                    merged[^1] = new EntitySpan(text[last.Start..e.End], last.Label, Math.Min(last.Score, e.Score), last.Start, e.End);
                    continue;
                }
            }

            merged.Add(e with { Text = text[e.Start..e.End] });
        }

        return merged;
    }
}
