using Blazor.AI.Abstractions.Smart;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blazor.AI.Components.Infrastructure;

/// <summary>Typed wrapper over <c>blazor-ai.js</c>. The module is imported once per circuit, on first use.</summary>
public sealed class BlazorAIJs(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "./_content/Blazor.AI.Components/blazor-ai.js";
    private Task<IJSObjectReference>? _module;

    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    public async ValueTask<string> GetThemeAsync() => await (await Module).InvokeAsync<string>("getTheme");

    public async ValueTask SetThemeAsync(string theme) => await (await Module).InvokeVoidAsync("setTheme", theme);

    public async ValueTask<string?> ReadClipboardAsync() => await (await Module).InvokeAsync<string?>("readClipboard");

    public async ValueTask<bool> CopyTextAsync(string text) => await (await Module).InvokeAsync<bool>("copyText", text);

    public async ValueTask DownloadTextAsync(string fileName, string content, string mime = "text/plain") =>
        await (await Module).InvokeVoidAsync("downloadText", fileName, content, mime);

    public async ValueTask ScrollToBottomAsync(ElementReference element) => await (await Module).InvokeVoidAsync("scrollToBottom", element);

    public async ValueTask FocusAsync(ElementReference element) => await (await Module).InvokeVoidAsync("focus", element);

    public async ValueTask AutosizeAsync(ElementReference element, int maxPx = 160) => await (await Module).InvokeVoidAsync("autosize", element, maxPx);

    public async ValueTask AttachComposerAsync<T>(ElementReference element, DotNetObjectReference<T> dotnet)
        where T : class => await (await Module).InvokeVoidAsync("attachComposer", element, dotnet);

    public async ValueTask DetachComposerAsync(ElementReference element) => await (await Module).InvokeVoidAsync("detachComposer", element);

    public async ValueTask<string> GetValueAsync(ElementReference element) => await (await Module).InvokeAsync<string>("getValue", element);

    public async ValueTask ClearComposerAsync(ElementReference element) => await (await Module).InvokeVoidAsync("clearComposer", element);

    public async ValueTask<IReadOnlyList<SmartPasteField>> CollectFormFieldsAsync(ElementReference element)
    {
        var fields = await (await Module).InvokeAsync<List<JsField>>("collectFormFields", element);
        return fields.Select(f => new SmartPasteField(f.Name, f.Description, f.Type ?? "string", f.AllowedValues)).ToArray();
    }

    public async ValueTask<int> ApplyFormValuesAsync(ElementReference element, IReadOnlyDictionary<string, string> values) =>
        await (await Module).InvokeAsync<int>("applyFormValues", element, values);

    public async ValueTask AttachSmartTextAreaAsync<T>(ElementReference element, DotNetObjectReference<T> dotnet)
        where T : class => await (await Module).InvokeVoidAsync("attachSmartTextArea", element, dotnet);

    public async ValueTask DetachSmartTextAreaAsync(ElementReference element) => await (await Module).InvokeVoidAsync("detachSmartTextArea", element);

    public async ValueTask<Caret> GetCaretAsync(ElementReference element) => await (await Module).InvokeAsync<Caret>("getCaret", element);

    public async ValueTask SetValueAndCaretAsync(ElementReference element, string value, int caret) =>
        await (await Module).InvokeVoidAsync("setValueAndCaret", element, value, caret);

    public async ValueTask SyncScrollAsync(ElementReference source, ElementReference target) => await (await Module).InvokeVoidAsync("syncScroll", source, target);

    public async ValueTask<SpeechSupport> SpeechSupportedAsync() => await (await Module).InvokeAsync<SpeechSupport>("speechSupported");

    public async ValueTask<bool> StartRecognitionAsync<T>(DotNetObjectReference<T> dotnet, string language)
        where T : class => await (await Module).InvokeAsync<bool>("startRecognition", dotnet, language);

    public async ValueTask StopRecognitionAsync() => await (await Module).InvokeVoidAsync("stopRecognition");

    public async ValueTask<IReadOnlyList<SpeechVoice>> ListVoicesAsync() => await (await Module).InvokeAsync<List<SpeechVoice>>("listVoices");

    public async ValueTask<bool> SpeakAsync(string text, string? voice, double rate = 1, double pitch = 1) =>
        await (await Module).InvokeAsync<bool>("speak", text, voice, rate, pitch);

    public async ValueTask StopSpeakingAsync() => await (await Module).InvokeVoidAsync("stopSpeaking");

    public async ValueTask PlayAudioAsync(string base64, string mime) => await (await Module).InvokeVoidAsync("playAudio", base64, mime);

    public async ValueTask<Rect> BoundingRectAsync(ElementReference element) => await (await Module).InvokeAsync<Rect>("boundingRect", element);

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true })
        {
            try
            {
                await _module.Result.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already gone.
            }
        }
    }

    public readonly record struct Caret(int Start, int End, int Length);

    public readonly record struct SpeechSupport(bool Recognition, bool Synthesis);

    public sealed record SpeechVoice(string Name, string Lang, bool IsDefault);

    public readonly record struct Rect(double X, double Y, double Width, double Height);

    private sealed record JsField(string Name, string? Description, string? Type, List<string>? AllowedValues);
}
