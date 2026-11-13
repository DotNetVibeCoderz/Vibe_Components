using Medical.AI.Assistant;
using Medical.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Medical.AI;

public static class AiBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="ClinicalTools"/>, an <see cref="IClinicalLlm"/> (from <paramref name="llm"/> or the environment),
    /// a transient <see cref="ClinicalAssistant"/>, the risk models (trained lazily) and, when <paramref name="imagingServiceUrl"/>
    /// is set, the <see cref="ImagingAiClient"/>.
    /// </summary>
    public static MedicalBuilder AddAI(this MedicalBuilder builder, LlmOptions? llm = null, string? imagingServiceUrl = null) =>
        builder.TryAddModule("ai", s =>
        {
            s.TryAddSingleton(new ClinicalTools());
            s.TryAddSingleton(sp => (llm ?? LlmOptions.FromEnvironment()).Create());
            s.TryAddTransient(sp => new ClinicalAssistant(sp.GetRequiredService<IClinicalLlm>(), sp.GetRequiredService<ClinicalTools>(), sp.GetService<IAuditLog>()));
            s.TryAddSingleton(_ => { var m = new DeteriorationRiskModel(); m.Train(); return m; });
            s.TryAddSingleton(_ => { var m = new DiabetesRiskModel(); m.Train(); return m; });
            if (imagingServiceUrl is not null)
            {
                s.AddHttpClient("imaging-ai", c => c.Timeout = TimeSpan.FromMinutes(5));
                s.TryAddSingleton(sp => new ImagingAiClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("imaging-ai"), imagingServiceUrl));
            }
        });
}
