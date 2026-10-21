using System.Security.Cryptography;
using System.Text;
using Blazor.AI.Abstractions.Providers;
using Microsoft.Extensions.Options;

namespace Blazor.AI.Core.Security;

/// <summary>AES-256-GCM authenticated encryption for prompts and secrets at rest. Layout: nonce(12) | tag(16) | ciphertext.</summary>
public sealed class AesGcmProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public AesGcmProtector(IOptions<BlazorAIOptions> options)
        : this(options.Value.Guardrails.EncryptionKey)
    {
    }

    public AesGcmProtector(string? base64Key)
    {
        _key = string.IsNullOrWhiteSpace(base64Key) ? RandomNumberGenerator.GetBytes(32) : Convert.FromBase64String(base64Key);
        if (_key.Length != 32)
        {
            throw new ArgumentException("The encryption key must be 32 bytes (base64 of a 256-bit key).", nameof(base64Key));
        }
    }

    /// <summary>Creates a new random key suitable for <c>BlazorAI:Guardrails:EncryptionKey</c>.</summary>
    public static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
        CryptographicOperations.ZeroMemory(plain);
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string protectedText)
    {
        var input = Convert.FromBase64String(protectedText);
        if (input.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Ciphertext is too short.");
        }

        var plain = new byte[input.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
