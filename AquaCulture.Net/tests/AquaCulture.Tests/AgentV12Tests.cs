using System.Net;
using System.Text;
using AquaCulture.AI.Agent;
using AquaCulture.Core.Species;
using AquaCulture.WaterQuality;

namespace AquaCulture.Tests;

public class AgentV12Tests
{
    [Fact]
    public void Memory_recalls_relevant_notes_bilingually_and_persists()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aqua-mem-{Guid.NewGuid():N}.json");
        try
        {
            var m = new AgentMemory(path);
            m.Remember("Kolam P-03 ada kotoran putih (white feces) pada tanggal 2 November", "P-03");
            m.Remember("Farmer prefers feeding 4 times a day");
            m.Remember("Kincir di P-01 rusak, sudah diganti tanggal 5 November", "P-01");
            var hits = m.Recall("kotoran putih udang");
            Assert.Contains("white feces", hits[0].Note.Text);
            Assert.Equal("P-01", m.Recall("kincirnya rusak")[0].Note.Pond);             // -nya stripped
            Assert.DoesNotContain(m.Recall("kotoran putih", pond: "P-01"), h => h.Note.Pond == "P-03");
            Assert.Empty(m.Recall("yang dan di"));                                       // only stop words

            var again = new AgentMemory(path);
            Assert.Equal(3, again.Notes.Count);
            Assert.True(again.Forget(again.Notes[1].Id));
            Assert.Equal(2, new AgentMemory(path).Notes.Count);
        }
        finally { File.Delete(path); }
    }

    private static AquaContext Pond(string name, double dox, double tan) => new()
    {
        PondName = name, Species = SpeciesCatalog.Vaname,
        Water = new WaterSample { TemperatureC = 29.5, DissolvedOxygen = dox, Ph = 8.1, SalinityPpt = 18, Ammonia = tan, Nitrite = 0.3, Alkalinity = 130 },
    };

    [Fact]
    public async Task Agent_handles_several_ponds_remembers_and_recalls_offline()
    {
        var agent = new AquaAgent([Pond("P-01", 5.5, 0.3), Pond("P-02", 2.6, 2.4), Pond("P-03", 4.8, 0.6)], new RuleBasedChatModel());
        var all = await agent.AskAsync("Bagaimana kondisi semua kolam?");
        Assert.Contains("list_ponds", all.ToolsUsed);
        Assert.True(all.Text.IndexOf("P-02", StringComparison.Ordinal) < all.Text.IndexOf("P-01", StringComparison.Ordinal), "worst pond first");

        var q = await agent.AskAsync("Berapa pakan untuk kolam 3 hari ini?");
        Assert.Equal("P-03", agent.Context.PondName);
        Assert.Contains("feeding_plan", q.ToolsUsed);

        var r = await agent.AskAsync("Tolong ingat bahwa udang di kolam 3 mulai lemas pagi ini");
        Assert.Equal(["remember"], r.ToolsUsed);
        Assert.Equal("P-03", Assert.Single(agent.Memory.Notes).Pond);

        var back = await agent.AskAsync("Apa catatan sebelumnya tentang udang lemas?");
        Assert.Contains("recall", back.ToolsUsed);
        Assert.Contains("lemas", back.Text);
        Assert.Contains("lemas", agent.History[0].Content);                          // relevant note injected into the system prompt
    }

    [Fact]
    public async Task History_is_trimmed_by_whole_turns()
    {
        var agent = new AquaAgent(new AquaContext(), new RuleBasedChatModel()) { MaxHistoryMessages = 10 };
        for (int i = 0; i < 12; i++) await agent.AskAsync("How much feed today?");
        Assert.True(agent.History.Count <= 10 + 1 + 4, $"{agent.History.Count} messages");
        Assert.Equal(ChatRole.System, agent.History[0].Role);
        Assert.Equal(ChatRole.User, agent.History[1].Role);                          // never starts with an orphaned tool result
        foreach (var (m, i) in agent.History.Select((m, i) => (m, i)).Where(x => x.m.Role == ChatRole.Tool))
            Assert.Contains(agent.History.Take(i), p => p.ToolCalls?.Any(c => c.Id == m.ToolCallId) == true);
    }

    private sealed class FakeAudioApi : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public string? TranscriptionForm { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            Assert.Equal("test-key", request.Headers.GetValues("api-key").Single());
            if (request.RequestUri.AbsolutePath.EndsWith("/transcriptions", StringComparison.Ordinal))
            {
                TranscriptionForm = await request.Content!.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"Berapa pakan hari ini?"}""", Encoding.UTF8, "application/json") };
            }
            var body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("\"input\"", body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x49, 0x44, 0x33, 1, 2, 3]) };
        }
    }

    [Fact]
    public async Task Voice_round_trip_transcribes_answers_and_speaks()
    {
        var api = new FakeAudioApi();
        var voice = new OpenAiVoiceService(LlmOptions.AzureOpenAI("https://farm.openai.azure.com", "test-key", "gpt-5-mini"), http: new HttpClient(api));
        var agent = new AquaAgent(new AquaContext(), new RuleBasedChatModel());
        var v = await agent.AskVoiceAsync(voice, [1, 2, 3, 4], "pertanyaan.wav");
        Assert.Equal("Berapa pakan hari ini?", v.Transcript);
        Assert.Equal("id", v.Answer.Language);
        Assert.Contains("feeding_plan", v.Answer.ToolsUsed);
        Assert.Equal((byte)0x49, v.Speech[0]);
        Assert.Equal("https://farm.openai.azure.com/openai/v1/audio/transcriptions", api.Urls[0]);
        Assert.Equal("https://farm.openai.azure.com/openai/v1/audio/speech", api.Urls[1]);
        Assert.Contains("name=language", api.TranscriptionForm);
        Assert.Contains("id", api.TranscriptionForm);
    }
}
