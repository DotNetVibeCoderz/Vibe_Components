using Blazor.AI.Abstractions;

namespace Blazor.AI.Components;

/// <summary>The built-in generator definitions behind the content and coding components. Reuse or copy them with <c>with { ... }</c>.</summary>
public static class GeneratorSpecs
{
    private static readonly string[] Tones = ["Professional", "Friendly", "Persuasive", "Playful", "Authoritative", "Inspirational"];
    private static readonly string[] Languages = ["English", "Bahasa Indonesia"];

    public static GeneratorSpec Content { get; } = new()
    {
        Title = "Content generator",
        Subtitle = "Any format, any audience",
        Icon = "bi-pen",
        SystemPrompt = "You are a versatile content writer. Write original, accurate, well-structured markdown. Never invent statistics.",
        Template = "Write a {{format}} about: {{topic}}\nAudience: {{audience}}\nTone: {{tone}}\nLength: about {{words}} words\nLanguage: {{language}}\nExtra notes: {{notes}}",
        Fields =
        [
            new("topic", "Topic", GeneratorFieldKind.Text, "e.g. How small farms can use soil sensors", Required: true, Wide: true),
            new("format", "Format", GeneratorFieldKind.Select, Options: ["article", "landing page section", "newsletter", "press release", "FAQ", "product description", "speech"]),
            new("audience", "Audience", GeneratorFieldKind.Text, "e.g. first-time founders"),
            new("tone", "Tone", GeneratorFieldKind.Select, Options: Tones),
            new("words", "Length (words)", GeneratorFieldKind.Number, Default: "300"),
            new("language", "Language", GeneratorFieldKind.Select, Options: Languages),
            new("notes", "Extra notes", GeneratorFieldKind.TextArea, "Facts to include, things to avoid…"),
        ],
        DownloadName = "content.md",
        Refinements = ["Make it shorter", "Make it more engaging", "Simplify the language", "Add a strong conclusion"],
    };

    public static GeneratorSpec Blog { get; } = new()
    {
        Title = "Blog writer",
        Subtitle = "Outline, draft and SEO metadata",
        Icon = "bi-journal-richtext",
        SystemPrompt = "You are an expert blog writer and SEO editor. Produce markdown with: an H1 title, a 2-sentence intro, " +
                       "H2 sections with practical detail, a conclusion with a call to action, then a '---' line followed by " +
                       "'Meta description:' (max 155 chars) and 'Keywords:' (5-8).",
        Template = "Blog topic: {{topic}}\nPrimary keyword: {{keyword}}\nReader: {{audience}}\nTone: {{tone}}\nTarget length: {{words}} words\nLanguage: {{language}}\nOutline hints: {{outline}}",
        Fields =
        [
            new("topic", "Topic", GeneratorFieldKind.Text, "e.g. Building offline-first Blazor apps", Required: true, Wide: true),
            new("keyword", "Primary keyword", GeneratorFieldKind.Text, "e.g. blazor offline"),
            new("audience", "Reader", GeneratorFieldKind.Text, "e.g. .NET developers"),
            new("tone", "Tone", GeneratorFieldKind.Select, Options: Tones),
            new("words", "Length (words)", GeneratorFieldKind.Number, Default: "800"),
            new("language", "Language", GeneratorFieldKind.Select, Options: Languages),
            new("outline", "Outline hints", GeneratorFieldKind.TextArea, "Optional: sections or points you want covered"),
        ],
        DownloadName = "blog-post.md",
        Refinements = ["Add code examples", "Make the intro punchier", "Add an FAQ section", "Shorten by 30%"],
    };

    public static GeneratorSpec Story { get; } = new()
    {
        Title = "Story generator",
        Subtitle = "Short fiction with characters and an arc",
        Icon = "bi-book",
        SystemPrompt = "You are an award-winning fiction author. Write a vivid, original story with a clear beginning, conflict and resolution. Use markdown with a title.",
        Template = "Genre: {{genre}}\nSetting: {{setting}}\nMain character: {{character}}\nTheme: {{theme}}\nReader age: {{age}}\nLength: about {{words}} words\nLanguage: {{language}}",
        Fields =
        [
            new("genre", "Genre", GeneratorFieldKind.Select, Options: ["Fantasy", "Science fiction", "Mystery", "Folk tale", "Adventure", "Slice of life", "Horror"]),
            new("setting", "Setting", GeneratorFieldKind.Text, "e.g. a floating market in Banjarmasin, 2080", Required: true),
            new("character", "Main character", GeneratorFieldKind.Text, "e.g. a shy drone repair girl named Laras"),
            new("theme", "Theme", GeneratorFieldKind.Text, "e.g. courage, friendship"),
            new("age", "Reader age", GeneratorFieldKind.Select, Options: ["Adults", "Teens", "Children (6-10)"]),
            new("words", "Length (words)", GeneratorFieldKind.Number, Default: "600"),
            new("language", "Language", GeneratorFieldKind.Select, Options: Languages),
        ],
        Temperature = 0.95f,
        DownloadName = "story.md",
        Refinements = ["Add a plot twist", "More dialogue", "Darker tone", "Write a sequel paragraph"],
    };

    public static GeneratorSpec Marketing { get; } = new()
    {
        Title = "Marketing generator",
        Subtitle = "Campaign copy across channels",
        Icon = "bi-megaphone",
        SystemPrompt = "You are a senior performance marketer and copywriter. Produce markdown sections: " +
                       "Value proposition, 3 headline variants, Ad copy (short and long), Email subject lines (5), Landing page hero copy, and a CTA. Be concrete and benefit-led.",
        Template = "Product: {{product}}\nTarget customer: {{audience}}\nKey benefits: {{benefits}}\nOffer: {{offer}}\nBrand voice: {{tone}}\nLanguage: {{language}}",
        Fields =
        [
            new("product", "Product or service", GeneratorFieldKind.Text, "e.g. Kopi Nusantara subscription box", Required: true, Wide: true),
            new("audience", "Target customer", GeneratorFieldKind.Text, "e.g. remote workers aged 25-40"),
            new("offer", "Offer", GeneratorFieldKind.Text, "e.g. first box 50% off"),
            new("tone", "Brand voice", GeneratorFieldKind.Select, Options: Tones),
            new("language", "Language", GeneratorFieldKind.Select, Options: Languages),
            new("benefits", "Key benefits", GeneratorFieldKind.TextArea, "One per line"),
        ],
        DownloadName = "campaign.md",
        Refinements = ["More urgency", "More premium", "Shorter headlines", "Add A/B test ideas"],
    };

    public static GeneratorSpec SocialMedia { get; } = new()
    {
        Title = "Social media generator",
        Subtitle = "Platform-native posts with hashtags",
        Icon = "bi-share",
        SystemPrompt = "You are a social media strategist. Write platform-native posts (respect each platform's length and style), " +
                       "with a hook in the first line, emojis only where natural, and relevant hashtags. Produce {{count}} variants separated by '---'.",
        Template = "Platform: {{platform}}\nMessage: {{message}}\nGoal: {{goal}}\nTone: {{tone}}\nLanguage: {{language}}\nNumber of variants: {{count}}",
        Fields =
        [
            new("message", "What do you want to say?", GeneratorFieldKind.TextArea, "e.g. We just launched Blazor.AI 1.0 with 100+ AI components", Required: true),
            new("platform", "Platform", GeneratorFieldKind.Select, Options: ["LinkedIn", "X (Twitter)", "Instagram", "TikTok caption", "Facebook", "Threads"]),
            new("goal", "Goal", GeneratorFieldKind.Select, Options: ["Awareness", "Engagement", "Clicks", "Sign-ups", "Hiring"]),
            new("tone", "Tone", GeneratorFieldKind.Select, Options: Tones),
            new("count", "Variants", GeneratorFieldKind.Number, Default: "3"),
            new("language", "Language", GeneratorFieldKind.Select, Options: Languages),
        ],
        DownloadName = "social-posts.md",
        Refinements = ["Make them shorter", "More playful", "Add a question to drive comments"],
    };

    public static GeneratorSpec Architecture { get; } = new()
    {
        Title = "Architecture advisor",
        Subtitle = "Trade-offs, not buzzwords",
        Icon = "bi-diagram-3",
        Task = AiTasks.Code,
        SystemPrompt = "You are a principal software architect. Recommend an architecture for the requirements. Markdown sections: " +
                       "Summary, Recommended architecture (components and responsibilities), Data and integration, Scaling and reliability, " +
                       "Security, Cost considerations, Alternatives considered (with why not), Risks and mitigations, First 3 milestones. " +
                       "Include a mermaid diagram in a ```mermaid block. Be specific to the stack given.",
        Template = "System: {{system}}\nKey requirements: {{requirements}}\nScale: {{scale}}\nTeam: {{team}}\nPreferred stack: {{stack}}\nConstraints: {{constraints}}",
        Fields =
        [
            new("system", "What are you building?", GeneratorFieldKind.Text, "e.g. Telemedicine platform for rural clinics", Required: true, Wide: true),
            new("scale", "Expected scale", GeneratorFieldKind.Text, "e.g. 50k users, 200 req/s peak"),
            new("team", "Team", GeneratorFieldKind.Text, "e.g. 4 .NET developers"),
            new("stack", "Preferred stack", GeneratorFieldKind.Text, "e.g. .NET 10, Blazor, Azure, PostgreSQL", Wide: true),
            new("requirements", "Key requirements", GeneratorFieldKind.TextArea, "Functional and non-functional, one per line"),
            new("constraints", "Constraints", GeneratorFieldKind.TextArea, "Budget, compliance (HIPAA, UU PDP), offline needs…"),
        ],
        Temperature = 0.3f,
        DownloadName = "architecture.md",
        Refinements = ["Make it cheaper to run", "Optimise for a small team", "Add an event-driven variant"],
    };

    public static GeneratorSpec Documentation { get; } = new()
    {
        Title = "Documentation generator",
        Subtitle = "README, API docs or XML comments from code",
        Icon = "bi-file-earmark-code",
        Task = AiTasks.Code,
        SystemPrompt = "You are a technical writer for developers. Document only what the code really does. Use markdown. " +
                       "For XML docs, return the code with /// comments added and nothing else changed.",
        Template = "Produce: {{kind}}\nAudience: {{audience}}\nLanguage of the docs: {{language}}\nCode ({{codeLanguage}}):\n```\n{{code}}\n```",
        Fields =
        [
            new("kind", "Produce", GeneratorFieldKind.Select, Options: ["README section", "API reference", "XML doc comments", "Usage tutorial", "Changelog entry"]),
            new("codeLanguage", "Code language", GeneratorFieldKind.Select, Options: ["C#", "Razor", "TypeScript", "Python", "SQL", "Go", "Java"]),
            new("audience", "Audience", GeneratorFieldKind.Text, "e.g. library consumers", Default: "Developers using this code"),
            new("language", "Docs language", GeneratorFieldKind.Select, Options: Languages),
            new("code", "Code", GeneratorFieldKind.TextArea, "Paste a class, method or module", Required: true),
        ],
        Temperature = 0.2f,
        DownloadName = "docs.md",
        Refinements = ["Add usage examples", "Make it more concise", "Translate to Bahasa Indonesia"],
    };
}
