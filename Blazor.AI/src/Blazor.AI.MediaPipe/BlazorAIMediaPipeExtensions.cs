using Blazor.AI.MediaPipe;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public static class BlazorAIMediaPipeExtensions
{
    /// <summary>
    /// Enables the vision components (MediaPipe.NET on ONNX Runtime, server-side). Frames travel from the browser as
    /// JPEG, so the Blazor Server hub message limit is raised to <see cref="MediaPipeVisionOptions.MaxFrameMessageBytes"/>.
    /// Add <c>_content/Blazor.AI.MediaPipe/blazor-ai-vision.css</c> to the page.
    /// </summary>
    public static IServiceCollection AddBlazorAIMediaPipe(this IServiceCollection services, Action<MediaPipeVisionOptions>? configure = null)
    {
        var options = services.AddOptions<MediaPipeVisionOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddSingleton<VisionModelManager>();
        services.AddScoped<FaceGallery>();
        services.AddScoped<VisionEventStream>();
        services.AddSingleton<IFaceEmbedder, MeshFaceEmbedder>();
        services.AddSingleton<IConfigureOptions<HubOptions>, VisionHubOptionsSetup>();
        services.AddHostedService<VisionModelPreloader>();
        return services;
    }
}
