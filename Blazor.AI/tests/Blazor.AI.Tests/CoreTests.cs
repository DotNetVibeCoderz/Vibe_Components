using Blazor.AI.Abstractions.Security;
using Blazor.AI.Abstractions.Vectors;
using Blazor.AI.Core;
using Blazor.AI.Core.Embeddings;
using Blazor.AI.Core.Security;
using Blazor.AI.Core.Simulated;
using Blazor.AI.Core.Text;
using Blazor.AI.Core.Vectors;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Tests;

public class CoreTests
{
    [Fact]
    public void Chunker_covers_text_in_order()
    {
        var text = string.Join(' ', Enumerable.Range(0, 2000).Select(i => $"word{i}"));
        var chunks = TextChunker.Chunk(text);
        Assert.True(chunks.Count > 1);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
        Assert.Contains("word0", chunks[0].Text);
        Assert.Contains("word1999", chunks[^1].Text);
    }

    [Theory]
    [InlineData("Sure! ```json\n{\"a\": 1}\n``` done", "{\"a\": 1}")]
    [InlineData("prefix {\"name\":\"x\",\"n\":[1,2]} suffix", "{\"name\":\"x\",\"n\":[1,2]}")]
    public void LlmJson_extracts_json_from_chatter(string input, string expected) =>
        Assert.Equal(expected, LlmJson.ExtractJson(input)?.Trim());

    [Fact]
    public async Task Vector_index_ranks_similar_text_first()
    {
        var embedder = new HashingEmbeddingGenerator();
        var index = new InMemoryVectorIndex();
        string[] docs = ["refund policy for damaged items", "shipping times to Jakarta", "how to reset your password"];
        await index.UpsertAsync(docs.Select((d, i) => new VectorRecord(i.ToString(), embedder.Embed(d), d)));

        var hits = await index.SearchAsync(embedder.Embed("reset password"), new VectorSearchRequest { Top = 2 });

        Assert.Equal("2", hits[0].Record.Id);
        Assert.True(hits[0].Score >= hits[^1].Score);
    }

    [Fact]
    public void Pii_detector_redacts_email_and_luhn_card()
    {
        var detector = new RegexPiiDetector();
        var redacted = detector.Redact("Mail budi@example.com, card 4111 1111 1111 1111.");
        Assert.DoesNotContain("budi@example.com", redacted);
        Assert.DoesNotContain("4111 1111 1111 1111", redacted);
        Assert.Contains(detector.Detect("budi@example.com"), m => m.Kind == PiiKind.Email);
    }

    [Fact]
    public void AesGcm_round_trips_and_detects_tampering()
    {
        var protector = new AesGcmProtector(AesGcmProtector.GenerateKey());
        var secret = protector.Protect("sk-test-123");
        Assert.NotEqual("sk-test-123", secret);
        Assert.Equal("sk-test-123", protector.Unprotect(secret));

        var bytes = Convert.FromBase64String(secret);
        bytes[^1] ^= 0xFF;
        Assert.ThrowsAny<Exception>(() => protector.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Prompt_template_renders_variables()
    {
        Assert.Equal(["name", "city"], PromptTemplating.Variables("Hi {{name}} from {{city}}"));
        Assert.Equal("Hi Sari from Bandung", PromptTemplating.Render("Hi {{name}} from {{city}}", new Dictionary<string, string?> { ["name"] = "Sari", ["city"] = "Bandung" }));
    }

    [Fact]
    public async Task Simulated_client_answers_offline()
    {
        IChatClient client = new SimulatedChatClient(tokenDelay: TimeSpan.Zero);
        var response = await client.GetResponseAsync("Summarise: Blazor.AI adds AI components to Blazor apps.");
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }
}
