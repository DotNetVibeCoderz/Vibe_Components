namespace Blazor.AI.Abstractions;

/// <summary>Well-known task names. Components tag requests with one, so middleware, audit and the simulated provider can tell them apart.</summary>
public static class AiTasks
{
    /// <summary>The <c>ChatOptions.AdditionalProperties</c> key that carries the task name.</summary>
    public const string PropertyKey = "blazorai.task";

    public const string Chat = "chat";
    public const string SmartPaste = "smart-paste";
    public const string SmartTextArea = "smart-textarea";
    public const string Summarize = "summarize";
    public const string Translate = "translate";
    public const string Classify = "classify";
    public const string Generate = "generate";
    public const string Rewrite = "rewrite";
    public const string Extract = "extract";
    public const string Code = "code";
    public const string Rag = "rag";
    public const string Agent = "agent";
    public const string Plan = "plan";
    public const string Insight = "insight";
    public const string Decision = "decision";
    public const string Json = "json";
}
