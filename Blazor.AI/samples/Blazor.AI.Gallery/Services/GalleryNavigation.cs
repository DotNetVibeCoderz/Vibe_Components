namespace Blazor.AI.Gallery.Services;

public sealed record NavItem(string Title, string Href, string Icon, string Package, int Components, string Summary);

/// <summary>The gallery's sections, in sidebar order.</summary>
public static class GalleryNavigation
{
    public static IReadOnlyList<NavItem> Sections { get; } =
    [
        new("Smart components", "smart", "bi-magic", "Blazor.AI.Components", 9, "SmartPaste, SmartTextArea, SmartComboBox, semantic search, recommendations, summariser, translator, classifier, email composer"),
        new("Conversational", "chat", "bi-chat-square-dots", "Blazor.AI.Components", 3, "AiChat, AiAssistant and a dockable AiCopilot that sees the record on screen"),
        new("Productivity", "productivity", "bi-briefcase", "Blazor.AI.Components", 5, "Prompt builder and library, structured output, JSON Schema generator, workflow assistant"),
        new("Content", "content", "bi-pen", "Blazor.AI.Components", 5, "Content, blog, story, marketing and social media generators"),
        new("Coding", "coding", "bi-code-slash", "Blazor.AI.Components", 4, "Code assistant, code review, architecture advisor, documentation generator"),
        new("RAG & knowledge", "rag", "bi-journal-text", "Blazor.AI.RAG", 9, "Ingestion, vector search, cited document chat, PDF and spreadsheet chat, knowledge graph"),
        new("Agents", "agents", "bi-robot", "Blazor.AI.Agents", 9, "Agent studio, multi-agent teams, task board, memory, debugger, tools, MCP, workflow designer"),
        new("Decision intelligence", "decision", "bi-diagram-2", "Blazor.AI.Decision", 8, "Rules, DMN tables, decision trees, policies, risk, eligibility, approvals, pricing"),
        new("Machine learning", "ml", "bi-cpu", "Blazor.AI.Analytics", 10, "Dataset explorer, feature engineering, AutoML, trainer and six workbenches"),
        new("Data science", "data-science", "bi-bar-chart-line", "Blazor.AI.Analytics", 8, "Correlation, statistics, Bayesian A/B, hypothesis tests, PCA, dimension reduction, time series, signals"),
        new("Reinforcement learning", "rl", "bi-joystick", "Blazor.AI.Analytics", 7, "Training studio, environment and reward design, policy maps, replays, bandits"),
        new("Hugging Face", "huggingface", "bi-emoji-smile", "Blazor.AI.LocalModels", 11, "Hub browser, downloader, playground, embeddings, classification, sentiment, NER, vision, OCR, speech"),
        new("Biology", "bio", "bi-virus", "Blazor.AI.Bio", 11, "DNA, restriction maps, alignment, phylogeny, proteins, molecules and ADMET, CRISPR, primers, codon optimisation, RNA-seq, bio copilot"),
        new("Visual analytics", "visualization", "bi-graph-up", "Blazor.AI.Visualization", 10, "AI dashboard, realtime, forecast and prediction charts, network, heatmap, Sankey, treemap, map, knowledge graph"),
        new("Security", "security", "bi-shield-lock", "Blazor.AI.Core", 0, "PII redaction, moderation, audit trail, encrypted prompts and key vault, RBAC"),
        new("Industry showcases", "industries", "bi-buildings", "All packages", 0, "Healthcare, agritech, finance, education, manufacturing, government, smart city, gaming, biotech"),
    ];

    /// <summary>Blazor.AI.ThreeD pages.</summary>
    public static IReadOnlyList<NavItem> ThreeDSections { get; } =
    [
        new("Avatars & 3D charts", "3d", "bi-person-bounding-box", "Blazor.AI.ThreeD", 3, "Talking avatar with lip sync, 3D charts, glTF model viewer"),
        new("Assistants, data & agents", "3d/assistants-data", "bi-diagram-3", "Blazor.AI.ThreeD", 8, "Companion, tour guide, knowledge graph, vector space, clusters, agent studio, arena, mind map"),
        new("Decision & RL", "3d/decision-rl", "bi-signpost-split", "Blazor.AI.ThreeD", 8, "Decision trees, rule graphs, strategy simulator, RL training, policy, battle, evolution and resource arenas"),
        new("Digital twins & health", "3d/twins-health", "bi-buildings", "Blazor.AI.ThreeD", 10, "Factory, building, farm, aquaculture, city; anatomy, molecules, proteins, gene networks, medical training"),
        new("Engineering, education & games", "3d/engineering-education-games", "bi-controller", "Blazor.AI.ThreeD", 13, "CAD and BIM copilots, simulation, mesh optimisation, classroom, solar system, history, science lab, NPCs, quests, worlds"),
        new("Enterprise command", "3d/enterprise", "bi-globe2", "Blazor.AI.ThreeD", 6, "Digital twin platform, enterprise knowledge graph, AgentOps, KPI sphere, mission control, autonomy"),
    ];

    public static int TotalComponents => Sections.Sum(s => s.Components) + ThreeDSections.Sum(s => s.Components);
}
