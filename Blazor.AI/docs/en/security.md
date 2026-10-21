# Security and guardrails

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [Bahasa Indonesia](../id/security.md)

Every chat client Blazor.AI creates is wrapped in a `GuardrailChatClient`. Before a request leaves your app it can redact personal data, block unsafe content and check the user's role. After the reply arrives it moderates the output and writes an audit entry. The rules are the same for all components and providers.

![Security page in the gallery](../screenshots/security.jpg)

## Turning guardrails on

```json
{
  "BlazorAI": {
    "Guardrails": {
      "RedactPii": true,
      "ModerateContent": true,
      "Audit": true,
      "AuditCapacity": 2000,
      "EnforceRbac": true
    }
  }
}
```

Or in code:

```csharp
builder.Services.AddBlazorAI(builder.Configuration)
    .UseGuardrails(g =>
    {
        g.RedactPii = true;
        g.BlockedTerms.Add("project-falcon");               // internal code names
        g.FeatureRoles["code-review"] = ["Engineering"];      // feature → roles
        g.ProviderRoles["openai"] = ["Admin", "Analyst"];     // provider → roles
    });
```

| Option | Default | Effect |
|---|---|---|
| `RedactPii` | `false` | Replaces emails, phone numbers, Luhn-valid card numbers, Indonesian NIK, IBANs and IPv4 addresses with placeholders such as `[EMAIL]` before the prompt is sent |
| `ModerateContent` | `false` | Blocks prompts and replies that match hate, violence, self-harm, sexual, harassment or prompt-injection patterns, plus your `BlockedTerms`; throws `AiGuardrailException` |
| `Audit` | `true` | Records requests, responses, tool calls, blocks and errors, with token usage and latency |
| `AuditCapacity` | `2000` | Ring-buffer size of the in-memory audit trail |
| `EnforceRbac` | `false` | Checks `FeatureRoles` and `ProviderRoles` against the current user |
| `EncryptionKey` | random per process | Base64 256-bit key for the prompt store and key vault. Without it, encrypted data cannot be read after a restart, so set it from a secret store in production (`AesGcmProtector.GenerateKey()` makes one) |

Components show a blocked request as a friendly error, so your pages need no extra handling. In your own code, catch `AiGuardrailException` and read its `Category`.

## Who is the user?

RBAC and audit entries use `IAiUserContext`. The default is anonymous. Replace it with one backed by your authentication:

```csharp
public sealed class AuthUserContext(AuthenticationStateProvider auth) : IAiUserContext
{
    private ClaimsPrincipal User => auth.GetAuthenticationStateAsync().GetAwaiter().GetResult().User;
    public string? UserName => User.Identity?.Name;
    public IReadOnlyCollection<string> Roles => User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
}

builder.Services.AddScoped<IAiUserContext, AuthUserContext>();
```

## Audit trail

`IAuditTrail` keeps entries in memory and raises `Recorded` for each one, so you can forward them to your logging or SIEM:

```csharp
var audit = app.Services.GetRequiredService<IAuditTrail>();
audit.Recorded += e => logger.LogInformation("{Kind} {Task} {Provider} {User}", e.Kind, e.Task, e.Provider, e.User);
```

Prompt text in audit entries is truncated and, with `RedactPii`, already redacted.

## Encrypted prompts and API keys

`EncryptedPromptStore` (`IPromptStore`) and `EncryptedApiKeyVault` (`IApiKeyVault`) encrypt with AES-GCM through `AesGcmProtector`. Each value gets a fresh nonce and an authentication tag, so tampered data fails to decrypt. Storage is pluggable through `ISecureBlobStorage`: in-memory by default, `FileSecureBlobStorage` for a directory, or your own implementation for a database or Key Vault.

```csharp
builder.Services.AddSingleton<ISecureBlobStorage>(new FileSecureBlobStorage(Path.Combine(builder.Environment.ContentRootPath, "secure")));
```

## Keys and configuration

- Keep provider keys in user-secrets during development and in environment variables or a secret manager in production (`BlazorAI__Providers__0__ApiKey`).
- Never ship cloud keys in a **Blazor WebAssembly** app; everything in the browser can be read. Use the simulated model, a local endpoint, or a server proxy you control. The WASM sample shows this setup.
- Cloud providers with no key are skipped at startup, so a missing secret degrades to the offline model instead of crashing.

## What the AI sees

Analytics, decision, RAG and biology components compute results locally, then send only those results to the model for explanation. Raw datasets, sequences or documents are sent only when a feature needs them (for example chat over a document's retrieved passages). The "Explain with AI" buttons are always explicit user actions.

## Dependency advisories

`Blazor.AI.LocalModels` uses Gravicode HF.Net for local vision and OCR. HF.Net depends on **SixLabors.ImageSharp 3.1.12**, the last release under the Apache-2.0 licence. ImageSharp 4.x is under a commercial licence and needs a licence key for Release builds. ImageSharp 3.1.12 has published advisories (GHSA-gwg2-r3hj-4w44, GHSA-wmxv-xphr-5c9g, GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q, GHSA-jjfr-hcj7-qf5w) about crafted image files. The package suppresses those NuGet audit warnings so builds stay clean. That leaves a decision with you:

- Images from **trusted sources** (your own uploads pipeline, internal tools): no action needed.
- Images from **untrusted users**: validate size and type before passing them to `ImageClassificationPanel` or `OCRPanel`, or reference `SixLabors.ImageSharp` 4.x directly in your app (with a licence) to override the version.

`System.Security.Cryptography.Pkcs` is pinned to a patched 10.0.x release through central package management.

## Reporting a vulnerability

Please open a private security advisory on the [GitHub repository](https://github.com/DotNetVibeCoderz/Vibe_Components/security/advisories) instead of a public issue.
