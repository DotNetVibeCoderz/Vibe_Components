using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Blazor.AI.Abstractions;
using Blazor.AI.Core.Services;
using Microsoft.Extensions.AI;

namespace Blazor.AI.Core.Simulated;

/// <summary>Produces the simulated provider's replies, task by task.</summary>
internal static partial class SimulatedResponder
{
    public static ChatMessage Respond(IList<ChatMessage> messages, ChatOptions? options)
    {
        var system = string.Join("\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var lastUserIndex = LastIndex(messages, ChatRole.User);
        var user = lastUserIndex >= 0 ? messages[lastUserIndex].Text ?? string.Empty : string.Empty;

        // Tool calling: call at most one tool per user turn, then answer from its result.
        if (options?.Tools is { Count: > 0 } tools)
        {
            var results = messages.Skip(lastUserIndex + 1).SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
            if (results.Count > 0)
            {
                return Text(AnswerFromTools(user, results, messages));
            }

            var call = PlanToolCall(user, tools.OfType<AIFunctionDeclaration>().ToList());
            if (call is not null)
            {
                return new ChatMessage(ChatRole.Assistant, [call]);
            }
        }

        var input = PromptTags.Read(user, PromptTags.Input) ?? user;
        var task = options.GetTask() ?? AiTasks.Chat;
        return Text(task switch
        {
            AiTasks.SmartPaste => SmartPaste(user, input),
            AiTasks.SmartTextArea => Continue(input, system),
            AiTasks.Summarize => Summarize(input, system),
            AiTasks.Translate => Translate(input, PromptTags.Read(user, PromptTags.Target) ?? "the target language"),
            AiTasks.Classify => Classify(input, PromptTags.Read(user, PromptTags.Labels)),
            AiTasks.Rewrite => Rewrite(input, system),
            AiTasks.Rag => Rag(input, PromptTags.Read(user, PromptTags.Context) ?? system),
            AiTasks.Plan => Plan(input),
            AiTasks.Insight => Insight(input),
            AiTasks.Code => Code(input, system),
            AiTasks.Decision => Decision(input),
            AiTasks.Json or AiTasks.Extract => Json(user, input, system),
            AiTasks.Generate => Generate(input, system),
            _ => Chat(input, messages),
        });
    }

    private static ChatMessage Text(string text) => new(ChatRole.Assistant, text);

    private static int LastIndex(IList<ChatMessage> messages, ChatRole role)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == role)
            {
                return i;
            }
        }

        return -1;
    }

    private static string SmartPaste(string user, string input)
    {
        var fields = PromptTags.Read(user, PromptTags.Fields);
        var array = fields is null ? null : JsonNode.Parse(fields) as JsonArray;
        return array is null ? "{}" : FieldHeuristics.Extract(input, array).ToJsonString();
    }

    private static readonly string[] CommonContinuations =
    [
        "Please let me know if you have any questions.",
        "Thank you for your patience.",
        "I look forward to hearing from you.",
        "we will get back to you within 24 hours.",
        "Terima kasih atas perhatiannya.",
    ];

    private static string Continue(string before, string system)
    {
        var phrasesStart = system.IndexOf("Prefer these phrases when relevant:", StringComparison.Ordinal);
        var phrases = phrasesStart < 0
            ? []
            : system[(phrasesStart + 35)..].TrimEnd('.').Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        var tail = before.TrimEnd();
        var lastWord = TextHeuristics.Words(tail).LastOrDefault() ?? string.Empty;

        // Complete a phrase whose beginning the user is typing.
        foreach (var phrase in phrases.Concat(CommonContinuations))
        {
            for (var k = Math.Min(phrase.Length - 1, tail.Length); k >= 3; k--)
            {
                if (tail.EndsWith(phrase[..k], StringComparison.OrdinalIgnoreCase))
                {
                    return phrase[k..];
                }
            }
        }

        if (tail.EndsWith('.') || tail.EndsWith('!') || tail.EndsWith('?'))
        {
            return phrases.Length > 0 ? " " + phrases[0] : " " + CommonContinuations[0];
        }

        return lastWord.ToLowerInvariant() switch
        {
            "thank" => " you for reaching out.",
            "best" => " regards,",
            "please" => " find the details below.",
            "kind" => " regards,",
            "we" => " appreciate your feedback.",
            "terima" => " kasih atas pesan Anda.",
            _ => string.Empty,
        };
    }

    private static string Summarize(string input, string system)
    {
        var lower = system.ToLowerInvariant();
        if (lower.Contains("bullet"))
        {
            var sentences = TextHeuristics.Summarize(input, 4);
            return string.Join('\n', TextHeuristics.Sentences(sentences).Select(s => "- " + s));
        }

        if (lower.Contains("tl;dr"))
        {
            return "TL;DR: " + TextHeuristics.Summarize(input, 1);
        }

        if (lower.Contains("executive"))
        {
            var keywords = TextHeuristics.Keywords(input, 3);
            return $"**{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(string.Join(", ", keywords))}**\n\n" +
                   string.Join('\n', TextHeuristics.Sentences(TextHeuristics.Summarize(input, 3)).Select(s => "- " + s)) +
                   "\n\n**Next steps:** review the points above with the owners of " + string.Join(" and ", keywords.Take(2)) + ".";
        }

        return TextHeuristics.Summarize(input, 3);
    }

    private static readonly Dictionary<string, string> EnToId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hello"] = "halo", ["thank"] = "terima kasih", ["thanks"] = "terima kasih", ["you"] = "Anda", ["good"] = "baik",
        ["morning"] = "pagi", ["please"] = "silakan", ["order"] = "pesanan", ["delivery"] = "pengiriman", ["customer"] = "pelanggan",
        ["price"] = "harga", ["today"] = "hari ini", ["tomorrow"] = "besok", ["meeting"] = "rapat", ["report"] = "laporan",
        ["data"] = "data", ["help"] = "bantuan", ["yes"] = "ya", ["no"] = "tidak", ["and"] = "dan", ["the"] = string.Empty,
        ["is"] = "adalah", ["we"] = "kami", ["are"] = string.Empty, ["your"] = "Anda", ["new"] = "baru", ["team"] = "tim",
    };

    private static string Translate(string input, string target)
    {
        if (target.Contains("indones", StringComparison.OrdinalIgnoreCase) || target.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            var words = Regex.Replace(input, @"[A-Za-z]+", m => EnToId.TryGetValue(m.Value, out var t) ? t : m.Value);
            return Regex.Replace(words, @"\s{2,}", " ").Trim() + "\n\n_(simulated translation — connect a real provider for fluent output)_";
        }

        return $"[{target}] {input}\n\n_(simulated translation — connect a real provider for fluent output)_";
    }

    private static string Classify(string input, string? labels)
    {
        var list = (labels ?? "positive | negative | neutral").Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var scores = TextHeuristics.ScoreLabels(input, list);
        var best = scores.MaxBy(s => s.Value);
        var obj = new JsonObject
        {
            ["label"] = best.Key,
            ["confidence"] = best.Value,
            ["scores"] = new JsonObject(scores.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))),
            ["reasoning"] = $"Keyword and sentiment cues ({string.Join(", ", TextHeuristics.Keywords(input, 3))}) point to '{best.Key}'.",
        };
        return obj.ToJsonString();
    }

    private static string Rewrite(string input, string system)
    {
        var lower = system.ToLowerInvariant();
        var sentences = TextHeuristics.Sentences(input);
        if (lower.Contains("shorter") || lower.Contains("concise"))
        {
            return TextHeuristics.Summarize(input, Math.Max(1, sentences.Count / 2));
        }

        if (lower.Contains("formal") || lower.Contains("professional"))
        {
            return "Dear reader,\n\n" + string.Join(' ', sentences.Select(s => char.ToUpperInvariant(s[0]) + s[1..])) + "\n\nKind regards.";
        }

        if (lower.Contains("friendly") || lower.Contains("casual"))
        {
            return "Hey there! " + input.Trim() + " 😊";
        }

        if (lower.Contains("bullet"))
        {
            return string.Join('\n', sentences.Select(s => "- " + s));
        }

        return input.Trim();
    }

    private static string Rag(string question, string context)
    {
        var sources = Regex.Split(context, @"(?=\[\d+\])").Where(s => s.Trim().Length > 0).ToList();
        var questionWords = TextHeuristics.ContentWords(question).ToHashSet();
        var best = sources
            .SelectMany(source =>
            {
                var tag = Regex.Match(source, @"^\[(\d+)\]") is { Success: true } m ? m.Groups[1].Value : "1";
                return TextHeuristics.Sentences(Regex.Replace(source, @"^\[\d+\][^\n]*\n?", string.Empty)).Select(s => (Tag: tag, Sentence: s));
            })
            .Select(x => (x.Tag, x.Sentence, Score: TextHeuristics.ContentWords(x.Sentence).Count(questionWords.Contains)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(3)
            .ToList();

        if (best.Count == 0)
        {
            return "I could not find this in the provided documents.";
        }

        return string.Join(' ', best.Select(b => $"{b.Sentence} [{b.Tag}]"));
    }

    private static string Plan(string goal)
    {
        var keywords = TextHeuristics.Keywords(goal, 3);
        var topic = keywords.Count == 0 ? "the goal" : string.Join(" ", keywords);
        string[] steps = ["Research", "Analyze", "Draft", "Review", "Deliver"];
        string[] agents = ["Researcher", "Analyst", "Writer", "Reviewer", "Writer"];
        var tasks = new JsonArray();
        for (var i = 0; i < steps.Length; i++)
        {
            tasks.Add(new JsonObject
            {
                ["title"] = $"{steps[i]} {topic}",
                ["description"] = $"{steps[i]} everything needed for: {goal.Trim()}",
                ["agent"] = agents[i],
                ["dependsOn"] = i == 0 ? new JsonArray() : new JsonArray(i - 1),
            });
        }

        return new JsonObject { ["tasks"] = tasks }.ToJsonString();
    }

    private static string Insight(string input)
    {
        var lines = input.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var numeric = lines.Where(l => Regex.IsMatch(l, @"\d")).Take(4).ToList();
        var builder = new StringBuilder("**Key insights**\n");
        foreach (var line in numeric)
        {
            builder.Append("- ").AppendLine(line.TrimStart('-', ' '));
        }

        builder.AppendLine().Append("**Suggested action:** investigate the largest movement above and validate it against recent changes.");
        return builder.ToString();
    }

    private static string Code(string input, string system)
    {
        var language = DetectLanguage(input + " " + system);
        var lower = (input + system).ToLowerInvariant();
        if (lower.Contains("review"))
        {
            var issues = new List<string>();
            if (input.Contains("catch (Exception", StringComparison.Ordinal) || input.Contains("catch {", StringComparison.Ordinal))
            {
                issues.Add("**Broad exception catch** — catch specific exceptions and log them.");
            }

            if (input.Contains(".Result", StringComparison.Ordinal) || input.Contains(".Wait()", StringComparison.Ordinal))
            {
                issues.Add("**Sync-over-async** — `.Result`/`.Wait()` can deadlock; use `await`.");
            }

            if (Regex.IsMatch(input, @"(password|apikey|secret)\s*=\s*""", RegexOptions.IgnoreCase))
            {
                issues.Add("**Hard-coded secret** — move it to configuration or a key vault.");
            }

            if (Regex.IsMatch(input, @"""\s*\+\s*\w+\s*\+\s*""") && lower.Contains("select"))
            {
                issues.Add("**SQL injection risk** — use parameterized queries.");
            }

            if (issues.Count == 0)
            {
                issues.Add("No critical issues found. Consider adding tests and XML docs for public members.");
            }

            return "### Code review\n\n" + string.Join('\n', issues.Select(i => "- " + i));
        }

        var name = TextHeuristics.Keywords(input, 2) is { Count: > 0 } k ? string.Concat(k.Select(w => char.ToUpperInvariant(w[0]) + w[1..])) : "Example";
        var code = language switch
        {
            "python" => $"def {name.ToLowerInvariant()}(data):\n    \"\"\"{input.Trim()}\"\"\"\n    result = [item for item in data if item]\n    return result\n",
            "javascript" or "typescript" => $"export function {char.ToLowerInvariant(name[0]) + name[1..]}(data) {{\n  // {input.Trim()}\n  return data.filter(Boolean);\n}}\n",
            "sql" => $"-- {input.Trim()}\nSELECT *\nFROM {name.ToLowerInvariant()}\nWHERE created_at >= CURRENT_DATE - INTERVAL '30 days';\n",
            _ => $"public static class {name}\n{{\n    /// <summary>{input.Trim()}</summary>\n    public static IReadOnlyList<T> Run<T>(IEnumerable<T> items) =>\n        items.Where(item => item is not null).ToList();\n}}\n",
        };
        return $"Here is a starting point:\n\n```{language}\n{code}```\n\nThis is simulated output. Connect a real provider for production-quality code.";
    }

    private static string DetectLanguage(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("python") ? "python"
            : lower.Contains("typescript") ? "typescript"
            : lower.Contains("javascript") || lower.Contains(" js ") ? "javascript"
            : lower.Contains("sql") ? "sql"
            : "csharp";
    }

    private static string Decision(string input)
    {
        var sentiment = TextHeuristics.Sentiment(input);
        var decision = sentiment >= 0 ? "approve" : "review";
        return new JsonObject
        {
            ["decision"] = decision,
            ["confidence"] = Math.Round(0.6 + Math.Abs(sentiment) * 0.3, 2),
            ["rationale"] = "Simulated rationale based on the facts provided: " + TextHeuristics.Summarize(input, 1),
        }.ToJsonString();
    }

    private static string Json(string user, string input, string system)
    {
        var schema = PromptTags.Read(user, PromptTags.Schema) ?? PromptTags.Read(system, PromptTags.Schema) ?? LlmJson.ExtractJson(system);
        if (schema is not null && LlmJson.ParseNode(schema) is { } template)
        {
            return Fill(template, input).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        var keywords = TextHeuristics.Keywords(input, 5);
        return new JsonObject
        {
            ["summary"] = TextHeuristics.Summarize(input, 1),
            ["keywords"] = new JsonArray(keywords.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            ["sentiment"] = Math.Round(TextHeuristics.Sentiment(input), 2),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode Fill(JsonNode template, string input)
    {
        var keywords = TextHeuristics.Keywords(input, 8);
        var numbers = Regex.Matches(input, @"-?\d+(?:\.\d+)?").Select(m => m.Value).ToList();
        var k = 0;
        var n = 0;

        JsonNode? Walk(JsonNode? node, string? name)
        {
            switch (node)
            {
                case JsonObject obj:
                    var copy = new JsonObject();
                    foreach (var (key, value) in obj)
                    {
                        copy[key] = Walk(value, key);
                    }

                    return copy;
                case JsonArray array:
                    var items = new JsonArray();
                    var sample = array.Count > 0 ? array[0] : JsonValue.Create("item");
                    for (var i = 0; i < Math.Clamp(keywords.Count / 2, 1, 3); i++)
                    {
                        items.Add(Walk(sample, name));
                    }

                    return items;
                case JsonValue value when value.TryGetValue<double>(out _):
                    return n < numbers.Count && double.TryParse(numbers[n++], CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : JsonValue.Create(Math.Round(Random.Shared.NextDouble() * 100, 1));
                case JsonValue value when value.TryGetValue<bool>(out _):
                    return JsonValue.Create(TextHeuristics.Sentiment(input) >= 0);
                default:
                    var lowerName = name?.ToLowerInvariant() ?? string.Empty;
                    if (lowerName.Contains("summary") || lowerName.Contains("description"))
                    {
                        return JsonValue.Create(TextHeuristics.Summarize(input, 1));
                    }

                    return JsonValue.Create(keywords.Count == 0 ? "value" : keywords[k++ % keywords.Count]);
            }
        }

        return Walk(template, null)!;
    }

    private static string Generate(string input, string system)
    {
        var keywords = TextHeuristics.Keywords(input + " " + system, 4);
        var topic = keywords.Count > 0 ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(string.Join(" ", keywords.Take(3))) : "Your Topic";
        var lower = system.ToLowerInvariant();

        if (lower.Contains("social"))
        {
            return $"🚀 {topic}: here is why it matters now.\n\n{TextHeuristics.Summarize(input, 1)}\n\n👉 What do you think? Share below!\n\n#" +
                   string.Join(" #", keywords.Select(w => w.Replace(" ", string.Empty, StringComparison.Ordinal)));
        }

        if (lower.Contains("story"))
        {
            return $"## {topic}\n\nOnce upon a time, in a world shaped by {keywords.FirstOrDefault() ?? "wonder"}, a curious hero set out to understand {topic.ToLowerInvariant()}. " +
                   "Every step revealed a new challenge, and every challenge a new ally.\n\nIn the end, the hero learned that the journey mattered as much as the destination.";
        }

        if (lower.Contains("email"))
        {
            return $"Subject: {topic}\n\nHi,\n\nI hope you are well. {TextHeuristics.Summarize(input, 2)}\n\nPlease let me know if you have any questions.\n\nBest regards,";
        }

        return $"# {topic}\n\n{TextHeuristics.Summarize(input, 2)}\n\n## Key points\n\n" +
               string.Join('\n', keywords.Select(w => $"- **{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w)}** — why it matters and how to act on it.")) +
               "\n\n## Conclusion\n\nStart small, measure results, and iterate.\n\n_Generated offline by the Blazor.AI simulated provider._";
    }

    private static string Chat(string input, IList<ChatMessage> messages)
    {
        var lower = input.ToLowerInvariant();
        if (lower.Length < 40 && (lower.StartsWith("hi", StringComparison.Ordinal) || lower.StartsWith("hello", StringComparison.Ordinal) || lower.StartsWith("halo", StringComparison.Ordinal)))
        {
            return "Hello! 👋 I'm the **Blazor.AI simulated assistant**. I run fully offline, so you can try every component without an API key. " +
                   "Connect OpenAI, Azure OpenAI, Anthropic, Gemini, Ollama or any other provider in settings to get real model answers.";
        }

        var keywords = TextHeuristics.Keywords(input, 4);
        var turns = messages.Count(m => m.Role == ChatRole.User);
        var builder = new StringBuilder();
        builder.Append("Here is what I understood");
        if (keywords.Count > 0)
        {
            builder.Append(" about **").Append(string.Join(", ", keywords)).Append("**");
        }

        builder.AppendLine(":").AppendLine();
        builder.AppendLine("1. **Clarify the goal.** Write down the outcome you need and how you will measure it.");
        builder.AppendLine($"2. **Break it down.** Split {keywords.FirstOrDefault() ?? "the task"} into small, testable steps.");
        builder.AppendLine("3. **Validate early.** Prototype the riskiest step first and gather feedback.");
        builder.AppendLine();
        if (turns > 1)
        {
            builder.AppendLine($"_(This is message {turns} of our conversation; I keep the full history as context.)_").AppendLine();
        }

        builder.Append("> Simulated response. Configure a real provider for model-quality answers.");
        return builder.ToString();
    }

    [GeneratedRegex(@"[-+]?\(?\s*\d+(?:\.\d+)?(?:\s*[-+*/^%]\s*\(?\s*\d+(?:\.\d+)?\s*\)?)+", RegexOptions.CultureInvariant)]
    private static partial Regex Arithmetic();

    private static FunctionCallContent? PlanToolCall(string user, IReadOnlyList<AIFunctionDeclaration> tools)
    {
        var words = TextHeuristics.ContentWords(user).ToHashSet();
        var expression = Arithmetic().Match(user);

        AIFunctionDeclaration? best = null;
        var bestScore = 0.0;
        foreach (var tool in tools)
        {
            var name = tool.Name.ToLowerInvariant();
            var score = TextHeuristics.ContentWords(tool.Name.Replace('_', ' ') + " " + tool.Description).Count(words.Contains) * 1.0;
            if (expression.Success && (name.Contains("calc") || name.Contains("math")))
            {
                score += 3;
            }

            if ((name.Contains("time") || name.Contains("date")) && (words.Contains("time") || words.Contains("date") || words.Contains("today") || words.Contains("jam")))
            {
                score += 3;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = tool;
            }
        }

        if (best is null || bestScore < 1)
        {
            return null;
        }

        var arguments = new Dictionary<string, object?>();
        if (best.JsonSchema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            var numbers = Regex.Matches(user, @"-?\d+(?:\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            var numberIndex = 0;
            foreach (var property in properties.EnumerateObject())
            {
                var type = property.Value.TryGetProperty("type", out var t) ? t.GetString() : "string";
                var lowerName = property.Name.ToLowerInvariant();
                if (lowerName.Contains("expr") && expression.Success)
                {
                    arguments[property.Name] = expression.Value.Trim();
                }
                else if (type is "number" or "integer")
                {
                    arguments[property.Name] = numberIndex < numbers.Count ? numbers[numberIndex++] : 0;
                }
                else if (type == "boolean")
                {
                    arguments[property.Name] = true;
                }
                else
                {
                    arguments[property.Name] = lowerName.Contains("query") || lowerName.Contains("text") || lowerName.Contains("input")
                        ? user.Trim()
                        : TextHeuristics.Keywords(user, 1).FirstOrDefault() ?? user.Trim();
                }
            }
        }

        return new FunctionCallContent("call_" + Guid.NewGuid().ToString("N")[..12], best.Name, arguments);
    }

    private static string AnswerFromTools(string user, List<FunctionResultContent> results, IList<ChatMessage> messages)
    {
        var calls = messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToDictionary(c => c.CallId, c => c.Name);
        var builder = new StringBuilder();
        foreach (var result in results)
        {
            var name = calls.GetValueOrDefault(result.CallId, "tool");
            var value = result.Result switch
            {
                JsonElement e => e.ToString(),
                null => "(no result)",
                var other => other.ToString(),
            };
            builder.Append("I used **").Append(name).Append("** and got: `").Append(value).AppendLine("`.");
        }

        builder.AppendLine().Append("So, for \"").Append(user.Trim()).Append("\", the answer is based on the tool result above.");
        return builder.ToString();
    }
}
