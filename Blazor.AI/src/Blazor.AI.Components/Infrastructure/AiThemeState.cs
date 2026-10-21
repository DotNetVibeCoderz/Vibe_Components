namespace Blazor.AI.Components.Infrastructure;

/// <summary>Per-user light/dark theme state, persisted in localStorage by <c>AiThemeToggle</c>.</summary>
public sealed class AiThemeState(BlazorAIJs js)
{
    public string Theme { get; private set; } = "light";

    public bool IsDark => Theme == "dark";

    public event Action? Changed;

    public async Task InitializeAsync()
    {
        Theme = await js.GetThemeAsync() == "dark" ? "dark" : "light";
        await js.SetThemeAsync(Theme);
        Changed?.Invoke();
    }

    public async Task SetAsync(string theme)
    {
        Theme = theme == "dark" ? "dark" : "light";
        await js.SetThemeAsync(Theme);
        Changed?.Invoke();
    }

    public Task ToggleAsync() => SetAsync(IsDark ? "light" : "dark");
}
