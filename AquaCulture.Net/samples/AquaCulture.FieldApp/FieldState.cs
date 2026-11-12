using AquaCulture.Core;
using AquaCulture.IoT;

namespace AquaCulture.FieldApp;

/// <summary>App-wide state: the offline journal, ponds, language and the cloud connection (API key in SecureStorage).</summary>
public sealed class FieldState
{
    public FieldState()
    {
        string device = Preferences.Get("device", "");
        if (device.Length == 0) Preferences.Set("device", device = "field-" + Guid.NewGuid().ToString("N")[..8]);
        Journal = new FieldJournal(Path.Combine(FileSystem.AppDataDirectory, "aqua-field"), device);
        if (Journal.LoadPonds().Count == 0)
            Journal.SavePonds(
            [
                new FieldPond("P-01", "Petak 1", "vaname", 2500, 1.3, 200_000, 4),
                new FieldPond("P-02", "Petak 2", "vaname", 2500, 1.3, 180_000, 4),
                new FieldPond("K-01", "Kolam nila 1", "nila", 1000, 1.2, 15_000),
            ]);
    }

    public FieldJournal Journal { get; }
    public IReadOnlyList<FieldPond> Ponds => Journal.LoadPonds();

    public string Language { get => Preferences.Get("lang", "id"); set { Preferences.Set("lang", value); LanguageChanged?.Invoke(); } }
    public event Action? LanguageChanged;
    public string T(Text2 t) => Language == "en" ? t.En : t.Id;
    public string T(string en, string id) => Language == "en" ? en : id;

    public string Sampler { get => Preferences.Get("sampler", ""); set => Preferences.Set("sampler", value); }
    public string CloudUrl { get => Preferences.Get("cloud", ""); set => Preferences.Set("cloud", value); }
    public string Tenant { get => Preferences.Get("tenant", ""); set => Preferences.Set("tenant", value); }
    public Task<string?> GetApiKeyAsync() => SecureStorage.GetAsync("apiKey");
    public Task SetApiKeyAsync(string key) => SecureStorage.SetAsync("apiKey", key);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Uploads queued readings when a cloud is configured and the phone is online.</summary>
    public async Task<SyncResult?> SyncAsync()
    {
        if (CloudUrl.Length == 0 || Tenant.Length == 0 || await GetApiKeyAsync() is not { Length: > 0 } key) return null;
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            return new SyncResult(0, Journal.Outbox.Acked, Journal.Outbox.Pending, false, T("No internet connection.", "Tidak ada koneksi internet."));
        var client = new CloudSyncClient(Http, new Uri(CloudUrl.TrimEnd('/') + "/"), Tenant, key, Journal.DeviceId);
        return await client.SyncOnceAsync(Journal.Outbox);
    }
}
