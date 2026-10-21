# Keamanan dan guardrail

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/security.md)

Setiap chat client yang dibuat Blazor.AI dibungkus dengan `GuardrailChatClient`. Sebelum request keluar dari aplikasimu, guardrail bisa menyamarkan (redact) data pribadi, memblokir konten berbahaya, dan memeriksa role user. Setelah balasan diterima, guardrail memoderasi output dan menulis entri audit. Aturannya sama untuk semua component dan provider.

![Security page in the gallery](../screenshots/security.jpg)

## Mengaktifkan guardrail

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

Atau lewat kode:

```csharp
builder.Services.AddBlazorAI(builder.Configuration)
    .UseGuardrails(g =>
    {
        g.RedactPii = true;
        g.BlockedTerms.Add("project-falcon");               // nama kode internal
        g.FeatureRoles["code-review"] = ["Engineering"];      // fitur → role
        g.ProviderRoles["openai"] = ["Admin", "Analyst"];     // provider → role
    });
```

| Opsi | Default | Efek |
|---|---|---|
| `RedactPii` | `false` | Mengganti email, nomor telepon, nomor kartu yang valid menurut Luhn, NIK Indonesia, IBAN, dan alamat IPv4 dengan placeholder seperti `[EMAIL]` sebelum prompt dikirim |
| `ModerateContent` | `false` | Memblokir prompt dan balasan yang cocok dengan pola ujaran kebencian, kekerasan, menyakiti diri sendiri, seksual, pelecehan, atau prompt injection, serta `BlockedTerms` milikmu; melempar `AiGuardrailException` |
| `Audit` | `true` | Mencatat request, response, tool call, blokir, dan error, lengkap dengan pemakaian token dan latensi |
| `AuditCapacity` | `2000` | Ukuran ring buffer untuk audit trail di memori |
| `EnforceRbac` | `false` | Memeriksa `FeatureRoles` dan `ProviderRoles` terhadap user saat ini |
| `EncryptionKey` | acak per proses | Key Base64 256-bit untuk prompt store dan key vault. Tanpa key ini, data terenkripsi tidak bisa dibaca lagi setelah restart, jadi di production set dari secret store (`AesGcmProtector.GenerateKey()` bisa membuatkannya) |

Component menampilkan request yang diblokir sebagai pesan error yang ramah, jadi halamanmu tidak perlu penanganan tambahan. Di kodemu sendiri, tangkap `AiGuardrailException` dan baca `Category`-nya.

## Siapa user-nya?

RBAC dan entri audit memakai `IAiUserContext`. Default-nya anonim. Ganti dengan implementasi yang terhubung ke sistem autentikasimu:

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

`IAuditTrail` menyimpan entri di memori dan memicu event `Recorded` untuk setiap entri, sehingga kamu bisa meneruskannya ke sistem logging atau SIEM:

```csharp
var audit = app.Services.GetRequiredService<IAuditTrail>();
audit.Recorded += e => logger.LogInformation("{Kind} {Task} {Provider} {User}", e.Kind, e.Task, e.Provider, e.User);
```

Teks prompt di entri audit dipotong, dan jika `RedactPii` aktif, sudah disamarkan.

## Prompt dan API key terenkripsi

`EncryptedPromptStore` (`IPromptStore`) dan `EncryptedApiKeyVault` (`IApiKeyVault`) mengenkripsi dengan AES-GCM melalui `AesGcmProtector`. Setiap nilai mendapat nonce baru dan authentication tag, sehingga data yang dimanipulasi akan gagal didekripsi. Storage bisa diganti lewat `ISecureBlobStorage`: in-memory secara default, `FileSecureBlobStorage` untuk direktori, atau implementasimu sendiri untuk database atau Key Vault.

```csharp
builder.Services.AddSingleton<ISecureBlobStorage>(new FileSecureBlobStorage(Path.Combine(builder.Environment.ContentRootPath, "secure")));
```

## Key dan konfigurasi

- Simpan key provider di user-secrets selama development, dan di environment variable atau secret manager di production (`BlazorAI__Providers__0__ApiKey`).
- Jangan pernah menyertakan key cloud di aplikasi **Blazor WebAssembly**; semua yang ada di browser bisa dibaca. Gunakan model simulasi, endpoint lokal, atau server proxy yang kamu kendalikan. Sample WASM menunjukkan setup ini.
- Provider cloud tanpa key dilewati saat startup, jadi secret yang hilang hanya membuat aplikasi turun ke model offline, bukan crash.

## Apa yang dilihat AI

Component analytics, decision, RAG, dan biologi menghitung hasilnya secara lokal, lalu hanya mengirim hasil tersebut ke model untuk dijelaskan. Dataset mentah, sekuens, atau dokumen hanya dikirim jika suatu fitur memang membutuhkannya (misalnya chat atas potongan dokumen hasil retrieval). Tombol "Explain with AI" selalu berupa aksi eksplisit dari user.

## Peringatan keamanan dependency

`Blazor.AI.LocalModels` memakai Gravicode HF.Net untuk vision dan OCR lokal. HF.Net bergantung pada **SixLabors.ImageSharp 3.1.12**, rilis terakhir dengan lisensi Apache-2.0. ImageSharp 4.x memakai lisensi komersial dan butuh license key untuk build Release. ImageSharp 3.1.12 punya advisory yang sudah dipublikasikan (GHSA-gwg2-r3hj-4w44, GHSA-wmxv-xphr-5c9g, GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q, GHSA-jjfr-hcj7-qf5w) terkait file gambar yang sengaja direkayasa. Package ini menyembunyikan warning NuGet audit tersebut agar build tetap bersih. Artinya, keputusan ada di tanganmu:

- Gambar dari **sumber tepercaya** (pipeline upload milikmu sendiri, tool internal): tidak perlu tindakan apa pun.
- Gambar dari **user yang tidak tepercaya**: validasi ukuran dan tipenya sebelum diteruskan ke `ImageClassificationPanel` atau `OCRPanel`, atau referensikan `SixLabors.ImageSharp` 4.x langsung di aplikasimu (dengan lisensi) untuk meng-override versinya.

`System.Security.Cryptography.Pkcs` dikunci ke rilis 10.0.x yang sudah di-patch melalui central package management.

## Melaporkan kerentanan

Silakan buka private security advisory di [repository GitHub](https://github.com/DotNetVibeCoderz/Vibe_Components/security/advisories), jangan lewat issue publik.
