using System.Security.Cryptography;
using System.Text;
using Medical.Core;
using Microsoft.EntityFrameworkCore;

namespace Medical.Hospital;

/// <summary>A ward-round device enrolled with the hospital. Only a hash of its token is stored.</summary>
public sealed class DeviceRegistration : TenantEntity
{
    /// <summary>The device's sync node id (e.g. "tablet-melati-01").</summary>
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The ward (department id) this device may read and write.</summary>
    public string WardId { get; set; } = "";
    /// <summary>First characters of the token, to find the row without scanning hashes; not secret on its own.</summary>
    public string TokenPrefix { get; set; } = "";
    /// <summary>SHA-256 of the token (hex).</summary>
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? EnrolledBy { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public bool Active => RevokedAt is null;
}

/// <summary>The outcome of enrolling a device: the token is shown once and never stored in clear text.</summary>
public sealed record DeviceEnrollment(DeviceRegistration Device, string Token, string WardCode);

/// <summary>
/// Per-device credentials for the sync API. Each device gets its own random token bound to one ward; the server keeps only
/// a SHA-256 hash, compares in constant time, records when the device was last seen and can revoke a lost tablet without
/// affecting the others.
/// </summary>
public sealed class DeviceRegistry(HospitalDbContext db, TimeProvider? clock = null)
{
    private const int PrefixLength = 8;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<DeviceEnrollment> EnrollAsync(string deviceId, string name, string wardCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Device id is required.", nameof(deviceId));
        var ward = await db.Departments.FirstOrDefaultAsync(d => d.Code == wardCode && (d.Kind == DepartmentKind.Ward || d.Kind == DepartmentKind.Icu), ct)
                   ?? throw new ArgumentException($"Unknown ward '{wardCode}'.", nameof(wardCode));
        foreach (var old in await db.Set<DeviceRegistration>().Where(d => d.DeviceId == deviceId && d.RevokedAt == null).ToListAsync(ct))
            old.RevokedAt = _clock.GetUtcNow().UtcDateTime;            // re-enrolment replaces the previous token
        string token = "mdn_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var reg = new DeviceRegistration
        {
            DeviceId = deviceId.Trim(), Name = name.Trim(), WardId = ward.Id, TokenPrefix = token[..(4 + PrefixLength)], TokenHash = Hash(token),
            CreatedAt = _clock.GetUtcNow().UtcDateTime, EnrolledBy = db.Tenant.UserName,
        };
        db.Set<DeviceRegistration>().Add(reg);
        db.AuditEvent(AuditAction.Create, "Device", null, $"{deviceId} → {ward.Code}");
        await db.SaveChangesAsync(ct);
        return new DeviceEnrollment(reg, token, ward.Code);
    }

    /// <summary>The active registration for a token, or null. Updates <see cref="DeviceRegistration.LastSeenAt"/>.</summary>
    public async Task<DeviceRegistration?> AuthenticateAsync(string? token, CancellationToken ct = default)
    {
        if (token is null || token.Length < 4 + PrefixLength + 10 || !token.StartsWith("mdn_", StringComparison.Ordinal)) return null;
        string prefix = token[..(4 + PrefixLength)];
        var candidates = await db.Set<DeviceRegistration>().Where(d => d.TokenPrefix == prefix && d.RevokedAt == null).ToListAsync(ct);
        byte[] given = Convert.FromHexString(Hash(token));
        var match = candidates.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(c.TokenHash), given));
        if (match is not null)
        {
            match.LastSeenAt = _clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        return match;
    }

    public async Task RevokeAsync(string registrationId, CancellationToken ct = default)
    {
        var reg = await db.Set<DeviceRegistration>().FirstOrDefaultAsync(d => d.Id == registrationId, ct) ?? throw new KeyNotFoundException(registrationId);
        reg.RevokedAt ??= _clock.GetUtcNow().UtcDateTime;
        db.AuditEvent(AuditAction.Delete, "Device", null, reg.DeviceId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<(DeviceRegistration Device, string Ward)>> ListAsync(CancellationToken ct = default)
    {
        var wards = await db.Departments.ToDictionaryAsync(d => d.Id, d => d.Code, ct);
        return (await db.Set<DeviceRegistration>().ToListAsync(ct)).OrderBy(d => d.RevokedAt is not null).ThenBy(d => d.DeviceId)
            .Select(d => (d, wards.GetValueOrDefault(d.WardId, "?"))).ToList();
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
