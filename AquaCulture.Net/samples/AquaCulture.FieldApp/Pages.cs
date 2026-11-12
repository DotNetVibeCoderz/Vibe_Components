using System.Globalization;
using AquaCulture.WaterQuality;

namespace AquaCulture.FieldApp;

/// <summary>Enter a pond visit, see the assessment at once, save it to the offline journal.</summary>
public sealed class SamplePage : ContentPage
{
    private readonly FieldState _s;
    private readonly Picker _pond = new();
    private readonly Entry _temp = Ui.Number("29.5"), _do = Ui.Number("4.8"), _ph = Ui.Number("7.9"), _sal = Ui.Number("20"), _tan = Ui.Number("0.5"),
        _no2 = Ui.Number("0.2"), _alk = Ui.Number("120"), _secchi = Ui.Number("35"), _count = Ui.Number("100"), _kg = Ui.Number("1.2"), _dead = Ui.Number("0");
    private readonly Editor _note = new() { AutoSize = EditorAutoSizeOption.TextChanges, Placeholder = "…" };
    private readonly VerticalStackLayout _result = new() { Spacing = 4 };

    public SamplePage(FieldState s)
    {
        _s = s;
        Title = s.T("Pond sampling", "Sampling kolam");
        var ponds = s.Ponds;
        _pond.ItemsSource = ponds.Select(p => $"{p.Id} · {p.Name} ({s.T(p.Species.Name)})").ToList();
        if (ponds.Count > 0) _pond.SelectedIndex = 0;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16, Spacing = 6,
                Children =
                {
                    Ui.Row(s.T("Pond", "Kolam"), _pond),
                    Ui.H(s.T("Water", "Air")),
                    Ui.Row(s.T("Temperature (°C)", "Suhu (°C)"), _temp),
                    Ui.Row(s.T("DO (mg/L)", "DO (mg/L)"), _do),
                    Ui.Row("pH", _ph),
                    Ui.Row(s.T("Salinity (ppt)", "Salinitas (ppt)"), _sal),
                    Ui.Row("TAN (mg/L)", _tan),
                    Ui.Row(s.T("Nitrite (mg/L)", "Nitrit (mg/L)"), _no2),
                    Ui.Row(s.T("Alkalinity (mg/L)", "Alkalinitas (mg/L)"), _alk),
                    Ui.Row(s.T("Secchi (cm)", "Kecerahan (cm)"), _secchi),
                    Ui.H(s.T("Cast-net sampling", "Sampling jala")),
                    Ui.Row(s.T("Animals in sample", "Jumlah ekor sampel"), _count),
                    Ui.Row(s.T("Sample weight (kg)", "Berat sampel (kg)"), _kg),
                    Ui.Row(s.T("Dead counted", "Jumlah mati"), _dead),
                    Ui.Row(s.T("Note", "Catatan"), _note),
                    Ui.Action(s.T("Assess", "Nilai"), (_, _) => Assess()),
                    Ui.Action(s.T("Save", "Simpan"), async (_, _) => await SaveAsync(), Ui.Good),
                    _result,
                    Ui.Credit(),
                },
            },
        };
    }

    private FieldPond? Pond => _pond.SelectedIndex >= 0 && _pond.SelectedIndex < _s.Ponds.Count ? _s.Ponds[_pond.SelectedIndex] : null;

    private FieldSample? Read()
    {
        if (Pond is not { } p) return null;
        return new FieldSample
        {
            PondId = p.Id, Sampler = _s.Sampler, Time = DateTime.UtcNow, TemperatureC = Ui.Parse(_temp), DissolvedOxygen = Ui.Parse(_do), Ph = Ui.Parse(_ph),
            SalinityPpt = Ui.Parse(_sal), Ammonia = Ui.Parse(_tan), Nitrite = Ui.Parse(_no2), Alkalinity = Ui.Parse(_alk), TransparencyCm = Ui.Parse(_secchi),
            SampleCount = Ui.Parse(_count) is { } c ? (int)c : null, SampleWeightKg = Ui.Parse(_kg), Dead = Ui.Parse(_dead) is { } d ? (int)d : null,
            Note = _note.Text ?? "",
        };
    }

    private FieldReport? Assess()
    {
        _result.Children.Clear();
        if (Read() is not { } sample || Pond is not { } pond) return null;
        var report = FieldAnalysis.Evaluate(sample, pond, _s.Journal.History(pond.Id, 1).FirstOrDefault());
        var ci = CultureInfo.InvariantCulture;
        var w = report.Water;
        Color c = w.Class switch { WaterQualityClass.Excellent or WaterQualityClass.Good => Ui.Good, WaterQualityClass.Fair => Ui.Warn, _ => Ui.Bad };
        foreach (var warn in report.EntryWarnings) _result.Add(Ui.P("⚠ " + _s.T(warn), Ui.Warn));
        _result.Add(Ui.H($"WQI {w.Wqi.ToString("0", ci)} · {_s.T(WaterQualityAssessment.ClassName(w.Class))}"));
        _result.Children[^1].SetValue(Label.TextColorProperty, c);
        if (w.UnionizedAmmonia is { } nh3) _result.Add(Ui.P($"NH₃-N {nh3.ToString("0.000", ci)} mg/L"));
        foreach (var issue in w.Issues) _result.Add(Ui.P("• " + _s.T(issue)));
        if (report.Actions.Count > 0) _result.Add(Ui.H(_s.T("Do now", "Tindakan")));
        foreach (var a in report.Actions.OrderBy(a => a.Priority))
            _result.Add(Ui.P($"{a.Priority}. {_s.T(a.Action)}" + (a.Quantity is { } q ? $" — {q.ToString("0.#", ci)} {a.Unit}" : "")));
        if (report.AbwG is { } abw)
        {
            _result.Add(Ui.H(_s.T("Stock", "Stok")));
            _result.Add(Ui.P(_s.T($"Average weight {abw.ToString("0.0", ci)} g · biomass ≈ {report.BiomassKg!.Value.ToString("N0", ci)} kg",
                                  $"Berat rata-rata {abw.ToString("0.0", ci)} g · biomassa ≈ {report.BiomassKg!.Value.ToString("N0", ci)} kg")));
        }
        if (report.Feed is { } f)
        {
            _result.Add(Ui.P(_s.T($"Feed today {f.DailyKg.ToString("0.0", ci)} kg in {f.MealsPerDay} meals ({f.RatePercentBw.ToString("0.0", ci)} % BW)",
                                  $"Pakan hari ini {f.DailyKg.ToString("0.0", ci)} kg dalam {f.MealsPerDay} kali ({f.RatePercentBw.ToString("0.0", ci)} % biomassa)")));
            foreach (var adj in f.Adjustments) _result.Add(Ui.P("• " + _s.T(adj)));
        }
        foreach (var n in report.Notes) _result.Add(Ui.P("• " + _s.T(n)));
        return report;
    }

    private async Task SaveAsync()
    {
        if (Read() is not { } sample) return;
        Assess();
        int n = _s.Journal.Save(sample);
        foreach (var e in new[] { _temp, _do, _ph, _sal, _tan, _no2, _alk, _secchi, _count, _kg, _dead }) e.Text = "";
        _note.Text = "";
        var sync = await _s.SyncAsync();
        string synced = sync is null ? _s.T("Cloud not configured; kept on the phone.", "Cloud belum diatur; disimpan di ponsel.")
            : sync.Error is null ? _s.T("Uploaded.", "Terkirim.") : _s.T($"Offline, {sync.Pending} waiting to upload.", $"Offline, {sync.Pending} menunggu dikirim.");
        await DisplayAlertAsync(_s.T("Saved", "Tersimpan"), _s.T($"{n} values saved. ", $"{n} nilai disimpan. ") + synced, "OK");
    }
}

/// <summary>Past visits per pond and the CSV export.</summary>
public sealed class HistoryPage : ContentPage
{
    private readonly FieldState _s;
    private readonly VerticalStackLayout _list = new() { Spacing = 4 };

    public HistoryPage(FieldState s)
    {
        _s = s;
        Title = s.T("History", "Riwayat");
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Children = { Ui.Action(s.T("Export CSV", "Ekspor CSV"), async (_, _) => await ExportAsync()), _list },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _list.Children.Clear();
        var ci = CultureInfo.InvariantCulture;
        foreach (var x in _s.Journal.History(max: 200))
        {
            string Fmt(string k, double? v, string f = "0.##") => v is { } d ? $"{k} {d.ToString(f, ci)}  " : "";
            _list.Add(Ui.H($"{x.PondId} · {x.Time.ToLocalTime():yyyy-MM-dd HH:mm}"));
            _list.Add(Ui.P(Fmt("T", x.TemperatureC) + Fmt("DO", x.DissolvedOxygen) + Fmt("pH", x.Ph) + Fmt("TAN", x.Ammonia) + Fmt("Sal", x.SalinityPpt)
                + Fmt("ABW", x.AbwG, "0.0") + (x.Dead is { } d ? $"{_s.T("dead", "mati")} {d}" : "")));
            if (x.Note.Length > 0) _list.Add(Ui.P(x.Note, Colors.Gray));
        }
        if (_list.Children.Count == 0) _list.Add(Ui.P(_s.T("No samples yet.", "Belum ada sampling.")));
    }

    private async Task ExportAsync()
    {
        string path = Path.Combine(FileSystem.CacheDirectory, $"aqua-field-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        using (var w = new StreamWriter(path)) _s.Journal.ExportCsv(w);
        await Share.Default.RequestAsync(new ShareFileRequest { Title = "AquaCulture Field CSV", File = new ShareFile(path) });
    }
}

/// <summary>Sampler, language, ponds and the cloud connection.</summary>
public sealed class SettingsPage : ContentPage
{
    private readonly FieldState _s;
    private readonly Label _status = Ui.P("");

    public SettingsPage(FieldState s)
    {
        _s = s;
        Title = s.T("Settings", "Pengaturan");
        var sampler = new Entry { Text = s.Sampler, Placeholder = s.T("Your name", "Nama Anda") };
        sampler.TextChanged += (_, e) => s.Sampler = e.NewTextValue ?? "";
        var lang = new Picker { ItemsSource = new[] { "Bahasa Indonesia", "English" }, SelectedIndex = s.Language == "en" ? 1 : 0 };
        lang.SelectedIndexChanged += (_, _) => s.Language = lang.SelectedIndex == 1 ? "en" : "id";
        var cloud = new Entry { Text = s.CloudUrl, Placeholder = "https://cloud.contoh.id", Keyboard = Keyboard.Url };
        var tenant = new Entry { Text = s.Tenant, Placeholder = "koperasi-a" };
        var key = new Entry { IsPassword = true, Placeholder = s.T("API key (stored encrypted)", "API key (disimpan terenkripsi)") };
        var ponds = new Editor { AutoSize = EditorAutoSizeOption.TextChanges, FontFamily = "monospace", FontSize = 12, Text = PondsText() };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16, Spacing = 6,
                Children =
                {
                    Ui.Row(s.T("Sampler", "Petugas"), sampler),
                    Ui.Row(s.T("Language", "Bahasa"), lang),
                    Ui.H(s.T("Ponds", "Kolam")),
                    Ui.P(s.T("One per line: id, name, species, area m², depth m, population, aerator kW", "Satu per baris: id, nama, spesies, luas m², kedalaman m, populasi, kincir kW"), Colors.Gray),
                    ponds,
                    Ui.Action(s.T("Save ponds", "Simpan kolam"), async (_, _) => await SavePondsAsync(ponds.Text ?? "")),
                    Ui.H("Cloud (aqua-cloud)"),
                    Ui.Row("URL", cloud), Ui.Row("Tenant", tenant), Ui.Row("API key", key),
                    Ui.Action(s.T("Save and sync now", "Simpan dan sinkron sekarang"), async (_, _) =>
                    {
                        s.CloudUrl = cloud.Text?.Trim() ?? ""; s.Tenant = tenant.Text?.Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(key.Text)) { await s.SetApiKeyAsync(key.Text.Trim()); key.Text = ""; }
                        var r = await s.SyncAsync();
                        _status.Text = r is null ? s.T("Fill in URL, tenant and API key.", "Isi URL, tenant, dan API key.")
                            : r.Error is null ? s.T($"Synced: {r.Sent} sent, {r.Pending} waiting.", $"Tersinkron: {r.Sent} terkirim, {r.Pending} menunggu.")
                            : s.T($"Not synced ({r.Error}); {r.Pending} waiting.", $"Belum tersinkron ({r.Error}); {r.Pending} menunggu.");
                    }),
                    _status,
                    Ui.P(s.T($"Device id {s.Journal.DeviceId} · {s.Journal.Outbox.Pending} values waiting to upload",
                             $"Id perangkat {s.Journal.DeviceId} · {s.Journal.Outbox.Pending} nilai menunggu dikirim"), Colors.Gray),
                    Ui.Credit(),
                },
            },
        };
    }

    private string PondsText() => string.Join('\n', _s.Ponds.Select(p => string.Join(", ", p.Id, p.Name, p.SpeciesId,
        p.AreaM2.ToString(CultureInfo.InvariantCulture), p.DepthM.ToString(CultureInfo.InvariantCulture), p.Population.ToString(CultureInfo.InvariantCulture),
        p.AerationKw.ToString(CultureInfo.InvariantCulture))));

    private async Task SavePondsAsync(string text)
    {
        var list = new List<FieldPond>();
        foreach (var (line, i) in text.Split('\n').Select((l, i) => (l.Trim(), i + 1)).Where(x => x.Item1.Length > 0))
        {
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            static double D(string x) => double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture);
            try
            {
                if (f.Length < 6) throw new FormatException();
                var p = new FieldPond(f[0], f[1], f[2], D(f[3]), D(f[4]), (long)D(f[5]), f.Length > 6 ? D(f[6]) : 0);
                _ = p.Species;                                                          // unknown species → error
                list.Add(p);
            }
            catch (Exception e) when (e is FormatException or KeyNotFoundException)
            {
                await DisplayAlertAsync(_s.T("Ponds", "Kolam"), _s.T($"Line {i} is not valid: {line}", $"Baris {i} tidak valid: {line}"), "OK");
                return;
            }
        }
        _s.Journal.SavePonds(list);
        _s.Language = _s.Language;                                                      // rebuild the pages with the new pond list
    }
}
