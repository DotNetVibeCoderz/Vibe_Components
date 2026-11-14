using System.Globalization;
using Medical.Core;
using Medical.Sync.Rounds;
using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;
using static Medical.Rounds.App;

namespace Medical.Rounds;

/// <summary>Colours and small view builders shared by the pages (same identity as the HIS and the Gallery).</summary>
internal static class Ui
{
    public static readonly Color Paper = Color.FromArgb("#F3F6F7"), Ink = Color.FromArgb("#14233A"), Muted = Color.FromArgb("#5B6B7E"), Rule = Color.FromArgb("#D5DDE3"),
        Teal = Color.FromArgb("#0F6E6E"), TealSoft = Color.FromArgb("#CFE5E3"), Amber = Color.FromArgb("#E8A33D"), Red = Color.FromArgb("#C8372D"), Score2 = Color.FromArgb("#F4B36B");

    public static Label L(string text, double size = 15, Color? color = null, string font = "Body") => new() { Text = text, FontSize = size, TextColor = color ?? Ink, FontFamily = font };

    public static Border Card(View content) => new()
    {
        Content = content, Padding = new Thickness(14, 12), BackgroundColor = Colors.White, Stroke = Rule, StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = 10 }, Margin = new Thickness(0, 0, 0, 10),
    };

    public static Border News(int? score)
    {
        var (bg, fg) = score switch { >= 7 => (Red, Colors.White), >= 5 => (Score2, Color.FromArgb("#5A2F00")), null => (Rule, Muted), _ => (TealSoft, Teal) };
        return new Border
        {
            BackgroundColor = bg, StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 12 }, Padding = new Thickness(9, 2), VerticalOptions = LayoutOptions.Center,
            Content = new Label { Text = score is { } s ? $"NEWS2 {s}" : "NEWS2 —", TextColor = fg, FontFamily = "Mono", FontSize = 13 },
        };
    }

    public static string Local(DateTime utc) => utc.AddHours(7).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    public static Button Primary(string text, Func<Task> click)
    {
        var b = new Button { Text = text, BackgroundColor = Ink, TextColor = Colors.White, FontFamily = "BodyBold", CornerRadius = 8 };
        b.Clicked += async (_, _) => await click();
        return b;
    }

    public static Entry Num(double value, string placeholder) => new() { Text = value.ToString(CultureInfo.InvariantCulture), Keyboard = Keyboard.Numeric, Placeholder = placeholder, FontFamily = "Mono", WidthRequest = 140, HorizontalOptions = LayoutOptions.Start, TextColor = Ink, BackgroundColor = Colors.White };

    public static double Parse(Entry e, double fallback) => double.TryParse(e.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}

/// <summary>The ward list, sickest first, with sync status.</summary>
public sealed class WardPage : ContentPage
{
    private readonly VerticalStackLayout _list = new() { Spacing = 0 };
    private readonly Label _status = Ui.L("", 13, Ui.Muted);

    public WardPage()
    {
        BackgroundColor = Ui.Paper;
        ToolbarItems.Add(new ToolbarItem(T("Settings", "Pengaturan"), null, async () => await Navigation.PushAsync(new SettingsPage())));
        var sync = Ui.Primary(T("Sync now", "Sinkronkan"), async () => { await Model.SyncAsync(); Refresh(); });
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16), Spacing = 10,
                Children =
                {
                    Ui.L(T("Ward round", "Visite bangsal"), 13, Ui.Teal, "BodyBold"),
                    Ui.L(Model.Settings.Ward, 30, font: "Display"),
                    new HorizontalStackLayout { Spacing = 12, Children = { sync, _status } },
                    _list,
                },
            },
        };
        Model.PropertyChanged += (_, _) => MainThread.BeginInvokeOnMainThread(Refresh);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Title = $"{Model.Settings.Ward} · {Model.Settings.Nurse}";
        Refresh();
        if (Capture is null) { await Model.SyncAsync(); Refresh(); }
    }

    public void Refresh()
    {
        _status.Text = (Model.Online ? T("online", "daring") : T("offline — changes are kept", "luring — perubahan disimpan")) +
                       $" · {T("pending", "antrean")} {Model.PendingChanges}" + (Model.LastSync is { } t ? $" · {T("synced", "sinkron")} {Ui.Local(t.UtcDateTime)}" : "");
        _status.TextColor = Model.Online ? Ui.Muted : Ui.Red;
        _list.Children.Clear();
        var rows = Model.Ward();
        if (rows.Count == 0) _list.Children.Add(Ui.L(T("No patients yet — sync with the HIS.", "Belum ada pasien — sinkronkan dengan SIMRS."), 15, Ui.Muted));
        foreach (var r in rows)
        {
            var p = r.Patient;
            var card = Ui.Card(new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                ColumnSpacing = 12,
                Children =
                {
                    Ui.L(p.Bed, 15, Ui.Teal, "Mono"),
                    new VerticalStackLayout
                    {
                        Children =
                        {
                            Ui.L(p.Name, 17, font: "BodyBold"),
                            Ui.L($"{p.Age} {T("y", "th")} · {p.Sex} · RM {p.Mrn}", 13, Ui.Muted),
                            Ui.L(p.Diagnosis ?? "—", 13),
                            Ui.L((r.OpenTasks > 0 ? $"{r.OpenTasks} {T("open task(s)", "tugas terbuka")}" : T("no open tasks", "tidak ada tugas")) + (r.PendingSync ? " · ⟳ " + T("not yet synced", "belum tersinkron") : ""), 12.5, r.PendingSync ? Ui.Amber : Ui.Muted),
                        },
                    }.Also(v => Grid.SetColumn(v, 1)),
                    Ui.News(r.News2).Also(b => Grid.SetColumn(b, 2)),
                },
            });
            if (r.News2 >= 7) card.Stroke = Ui.Red;
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await Navigation.PushAsync(new PatientPage(p.Id));
            card.GestureRecognizers.Add(tap);
            _list.Children.Add(card);
        }
    }
}

/// <summary>A patient's chart: NEWS2 trend, latest vitals, tasks and notes.</summary>
public sealed class PatientPage : ContentPage
{
    private readonly string _visitId;
    private readonly VerticalStackLayout _body = new() { Padding = new Thickness(16), Spacing = 10 };

    public PatientPage(string visitId)
    {
        _visitId = visitId;
        BackgroundColor = Ui.Paper;
        Content = new ScrollView { Content = _body };
    }

    protected override void OnAppearing() { base.OnAppearing(); Render(); }

    public void Render()
    {
        var c = Model.Chart(_visitId);
        _body.Children.Clear();
        if (c is null) { _body.Children.Add(Ui.L(T("The patient is no longer on this ward.", "Pasien sudah tidak di bangsal ini."))); return; }
        var p = c.Patient;
        Title = $"{p.Bed} · {p.Name}";
        _body.Children.Add(Ui.L($"{p.Bed} · RM {p.Mrn} · {p.Payer}", 13, Ui.Teal, "BodyBold"));
        _body.Children.Add(Ui.L(p.Name, 28, font: "Display"));
        _body.Children.Add(Ui.L($"{p.Age} {T("y", "th")} · {p.Sex} · {p.Diagnosis}" + (p.Allergies is { Length: > 0 } a ? $" · {T("allergy", "alergi")}: {a}" : ""), 14, Ui.Muted));

        var points = c.Vitals.OrderBy(v => v.Time).Select(v => (v.Time, v.News2)).ToList();
        if (p.News2 is { } sn && p.News2At is { } st && !points.Any(x => x.Time == st)) points.Insert(0, (st, sn));
        _body.Children.Add(Ui.Card(new VerticalStackLayout
        {
            Spacing = 6,
            Children = { Ui.L(T("NEWS2 trend", "Tren NEWS2"), 13, Ui.Muted, "BodyBold"), new GraphicsView { Drawable = new NewsTrend(points.OrderBy(x => x.Time).ToList()), HeightRequest = 120 } },
        }));
        if (c.Vitals.FirstOrDefault() is { } last)
            _body.Children.Add(Ui.Card(new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new HorizontalStackLayout { Spacing = 10, Children = { Ui.L(T("Latest on this device", "Terakhir di perangkat ini"), 13, Ui.Muted, "BodyBold"), Ui.News(last.News2) } },
                    Ui.L($"RR {last.RespiratoryRate:0} · SpO₂ {last.SpO2:0}%{(last.OnOxygen ? " O₂" : "")} · TD {last.SystolicBp:0}/{last.DiastolicBp:0} · {T("HR", "Nadi")} {last.HeartRate:0} · {last.TemperatureC:0.0} °C · {last.Avpu}", 15, font: "Mono"),
                    Ui.L($"{Ui.Local(last.Time)} · {last.RecordedBy}", 12.5, Ui.Muted),
                },
            }));
        _body.Children.Add(Ui.Primary(T("Record vital signs", "Catat tanda vital"), async () => await Navigation.PushAsync(new VitalsPage(_visitId))));

        var tasks = new VerticalStackLayout { Spacing = 4, Children = { Ui.L(T("Tasks", "Tugas"), 13, Ui.Muted, "BodyBold") } };
        foreach (var t in c.Tasks)
        {
            var cb = new CheckBox { IsChecked = t.Done, Color = Ui.Teal };
            cb.CheckedChanged += (_, e) => { Model.CompleteTask(t.Id, e.Value); Render(); };
            tasks.Children.Add(new HorizontalStackLayout { Spacing = 6, Children = { cb, new VerticalStackLayout { Children = { Ui.L(t.Title, 15), Ui.L(t.Done ? $"✓ {t.DoneBy} {Ui.Local(t.DoneAt ?? t.Due)}" : $"{T("due", "jatuh tempo")} {Ui.Local(t.Due)}", 12.5, Ui.Muted) } } } });
        }
        if (c.Tasks.Count == 0) tasks.Children.Add(Ui.L(T("No tasks.", "Tidak ada tugas."), 14, Ui.Muted));
        _body.Children.Add(Ui.Card(tasks));

        var note = new Editor { Placeholder = T("Nursing note…", "Catatan keperawatan…"), HeightRequest = 80, FontFamily = "Body" };
        var notes = new VerticalStackLayout { Spacing = 6, Children = { Ui.L(T("Notes", "Catatan"), 13, Ui.Muted, "BodyBold"), note,
            Ui.Primary(T("Add note", "Tambah catatan"), () => { if (!string.IsNullOrWhiteSpace(note.Text)) { Model.AddNote(_visitId, note.Text); Render(); } return Task.CompletedTask; }) } };
        foreach (var n in c.Notes) notes.Children.Add(new VerticalStackLayout { Children = { Ui.L(n.Text, 14.5), Ui.L($"{n.Author} · {Ui.Local(n.Time)}", 12.5, Ui.Muted) } });
        _body.Children.Add(Ui.Card(notes));
    }
}

/// <summary>Vital-sign entry with a live NEWS2 preview and the RCP clinical response.</summary>
public sealed class VitalsPage : ContentPage
{
    private readonly Entry _rr = Ui.Num(18, "RR"), _spo2 = Ui.Num(96, "SpO₂"), _sbp = Ui.Num(120, "TD sis"), _dbp = Ui.Num(80, "TD dia"), _hr = Ui.Num(84, "Nadi"), _t = Ui.Num(37.0, "Suhu");
    private readonly Switch _o2 = new() { OnColor = Ui.Teal };
    private readonly Picker _avpu = new() { ItemsSource = new[] { "Alert", "Confusion", "Voice", "Pain", "Unresponsive" }, SelectedIndex = 0, WidthRequest = 200, HorizontalOptions = LayoutOptions.Start, TextColor = Ui.Ink };
    private readonly Label _score = Ui.L("", 34, font: "Mono"), _advice = Ui.L("", 14.5);
    private readonly Border _box = new() { StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 10 }, Padding = new Thickness(14, 10) };

    public VitalsPage(string visitId, VitalsInput? sample = null)
    {
        BackgroundColor = Ui.Paper;
        Title = T("Vital signs", "Tanda vital");
        if (sample is { } s)
        {
            (_rr.Text, _spo2.Text, _sbp.Text, _dbp.Text, _hr.Text, _t.Text) = ($"{s.RespiratoryRate}", $"{s.SpO2}", $"{s.SystolicBp}", $"{s.DiastolicBp}", $"{s.HeartRate}", s.TemperatureC.ToString(CultureInfo.InvariantCulture));
            _o2.IsToggled = s.OnOxygen;
        }
        foreach (var e in new[] { _rr, _spo2, _sbp, _dbp, _hr, _t }) e.TextChanged += (_, _) => Update();
        _o2.Toggled += (_, _) => Update();
        _avpu.SelectedIndexChanged += (_, _) => Update();
        View Row(string label, View v) => new Grid { ColumnDefinitions = { new ColumnDefinition(new GridLength(150)), new ColumnDefinition(GridLength.Star) }, Children = { Ui.L(label, 15, Ui.Muted).Also(l => l.VerticalOptions = LayoutOptions.Center), v.Also(x => Grid.SetColumn(x, 1)) } };
        _box.Content = new VerticalStackLayout { Children = { _score, _advice } };
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16), Spacing = 8,
                Children =
                {
                    Ui.Card(new VerticalStackLayout
                    {
                        Spacing = 6,
                        Children =
                        {
                            Row(T("Respiratory rate /min", "Frekuensi napas /mnt"), _rr), Row("SpO₂ %", _spo2), Row(T("Supplemental O₂", "Oksigen tambahan"), _o2),
                            Row(T("Systolic BP", "TD sistolik"), _sbp), Row(T("Diastolic BP", "TD diastolik"), _dbp), Row(T("Pulse /min", "Nadi /mnt"), _hr),
                            Row(T("Temperature °C", "Suhu °C"), _t), Row(T("Consciousness (ACVPU)", "Kesadaran (ACVPU)"), _avpu),
                        },
                    }),
                    _box,
                    Ui.Primary(T("Save (works offline)", "Simpan (bisa luring)"), async () =>
                    {
                        Model.RecordVitals(visitId, Input());
                        if (Capture is null) _ = Model.SyncAsync();
                        await Navigation.PopAsync();
                    }),
                },
            },
        };
        Update();
    }

    private VitalsInput Input() => new(Ui.Parse(_rr, 18), Ui.Parse(_spo2, 96), _o2.IsToggled, Ui.Parse(_sbp, 120), Ui.Parse(_dbp, 80), Ui.Parse(_hr, 80), Ui.Parse(_t, 37),
        (Consciousness)Math.Max(0, _avpu.SelectedIndex));

    private void Update()
    {
        var r = RoundsApp.Preview(Input());
        _score.Text = $"NEWS2 {r.Score}";
        _advice.Text = r.Interpretation[Lang];
        (_box.BackgroundColor, _score.TextColor, _advice.TextColor) = r.Score >= 7 ? (Ui.Red, Colors.White, Colors.White)
            : r.Score >= 5 || r.Components.Any(c => c.Points == 3) ? (Ui.Score2, Ui.Ink, Ui.Ink) : (Ui.TealSoft, Ui.Teal, Ui.Ink);
    }
}

/// <summary>Server, tenant, ward and nurse of this device.</summary>
public sealed class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        BackgroundColor = Ui.Paper;
        Title = T("Settings", "Pengaturan");
        var s = Model.Settings;
        var server = new Entry { Text = s.ServerUrl }; var tenant = new Entry { Text = s.Tenant }; var ward = new Entry { Text = s.Ward };
        var nurse = new Entry { Text = s.Nurse }; var key = new Entry { Text = s.DeviceKey, IsPassword = true };
        var lang = new Picker { ItemsSource = new[] { "id", "en" }, SelectedItem = Lang };
        View Row(string l, View v) => new VerticalStackLayout { Children = { Ui.L(l, 13, Ui.Muted), v } };
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16), Spacing = 10,
                Children =
                {
                    Ui.Card(new VerticalStackLayout { Spacing = 8, Children = { Row(T("HIS server", "Server SIMRS"), server), Row(T("Hospital (tenant)", "Rumah sakit (tenant)"), tenant),
                        Row(T("Ward code", "Kode bangsal"), ward), Row(T("Nurse", "Perawat"), nurse), Row(T("Device key", "Kunci perangkat"), key), Row(T("Language", "Bahasa"), lang) } }),
                    Ui.L(T($"Device {s.DeviceId}. Data is stored on the device and synchronised with the HIS; vital signs and notes are append-only, the ward list is read-only.",
                           $"Perangkat {s.DeviceId}. Data disimpan di perangkat dan disinkronkan dengan SIMRS; tanda vital dan catatan tidak bisa diubah, daftar pasien hanya-baca."), 13, Ui.Muted),
                    Ui.Primary(T("Save", "Simpan"), async () =>
                    {
                        Lang = lang.SelectedItem as string ?? "id";
                        Preferences.Default.Set("lang", Lang);
                        SaveSettings(new RoundsSettings(server.Text.Trim(), tenant.Text.Trim(), ward.Text.Trim().ToUpperInvariant(), nurse.Text.Trim(), key.Text, s.DeviceId));
                        await Model.SyncAsync();
                        Application.Current!.Windows[0].Page = new NavigationPage(new WardPage()) { BarBackgroundColor = Ui.Ink, BarTextColor = Colors.White };
                    }),
                },
            },
        };
    }
}

/// <summary>NEWS2 over time with the 5 and 7 thresholds.</summary>
public sealed class NewsTrend(IReadOnlyList<(DateTime Time, int News2)> points) : IDrawable
{
    public void Draw(ICanvas canvas, RectF r)
    {
        const float max = 20, padL = 26, padB = 16;
        float W = r.Width - padL - 6, H = r.Height - padB - 6;
        float Y(double v) => r.Top + 6 + H * (1 - (float)v / max);
        canvas.FontSize = 10;
        canvas.FontColor = Ui.Muted;
        foreach (var (v, c) in new[] { (5.0, Ui.Score2), (7.0, Ui.Red) })
        {
            canvas.StrokeColor = c;
            canvas.StrokeDashPattern = [4, 3];
            canvas.DrawLine(r.Left + padL, Y(v), r.Right - 6, Y(v));
            canvas.DrawString(v.ToString(CultureInfo.InvariantCulture), r.Left, Y(v) - 6, padL - 4, 12, HorizontalAlignment.Right, VerticalAlignment.Center);
        }
        canvas.StrokeDashPattern = null;
        if (points.Count == 0) { canvas.DrawString(T("no observations", "belum ada observasi"), r, HorizontalAlignment.Center, VerticalAlignment.Center); return; }
        var t0 = points[0].Time; double span = Math.Max(1, (points[^1].Time - t0).TotalMinutes);
        float X(DateTime t) => r.Left + padL + W * (float)((t - t0).TotalMinutes / span);
        canvas.StrokeColor = Ui.Ink;
        canvas.StrokeSize = 2;
        for (int i = 1; i < points.Count; i++) canvas.DrawLine(X(points[i - 1].Time), Y(points[i - 1].News2), X(points[i].Time), Y(points[i].News2));
        foreach (var (t, n) in points)
        {
            canvas.FillColor = n >= 7 ? Ui.Red : n >= 5 ? Ui.Score2 : Ui.Teal;
            canvas.FillCircle(X(t), Y(n), 4);
        }
        canvas.DrawString(Ui.Local(t0), r.Left + padL, r.Bottom - padB, 80, padB, HorizontalAlignment.Left, VerticalAlignment.Bottom);
        canvas.DrawString(Ui.Local(points[^1].Time), r.Right - 86, r.Bottom - padB, 80, padB, HorizontalAlignment.Right, VerticalAlignment.Bottom);
    }
}

/// <summary>Self-capture for documentation: syncs with the HIS, then renders each page to PNG and quits.</summary>
internal static class Screens
{
    public static async Task CaptureAllAsync(Window window, string dir)
    {
        Directory.CreateDirectory(dir);
        string suffix = Lang == "id" ? "-id" : "";
        await Task.Delay(1500);
        var report = await Model.SyncAsync();
        var nav = (NavigationPage)window.Page!;
        if (nav.CurrentPage is WardPage wp) wp.Refresh();
        await Shot(nav, Path.Combine(dir, $"rounds-ward{suffix}.png"));
        if (Model.Ward().FirstOrDefault() is { } sickest)
        {
            var visit = sickest.Patient.Id;
            Model.RecordVitals(visit, new VitalsInput(24, 93, false, 98, 62, 118, 38.6));
            Model.AddNote(visit, T("Cold peripheries, CRT 3 s. Doctor informed.", "Akral dingin, CRT 3 detik. DPJP sudah dilapori."));
            await nav.PushAsync(new PatientPage(visit), false);
            await Shot(nav, Path.Combine(dir, $"rounds-patient{suffix}.png"));
            await nav.PushAsync(new VitalsPage(visit, new VitalsInput(26, 91, false, 92, 58, 124, 39.1)), false);
            await Shot(nav, Path.Combine(dir, $"rounds-vitals{suffix}.png"));
            await nav.PopToRootAsync(false);
            ((WardPage)nav.CurrentPage).Refresh();
            await Shot(nav, Path.Combine(dir, $"rounds-ward-pending{suffix}.png"));
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), $"rounds-capture{suffix}.log"), report.ToString());
        Application.Current!.Quit();
    }

    private static async Task Shot(VisualElement page, string path)
    {
        await Task.Delay(900);
#if WINDOWS
        await WindowCapture.SaveAsync(Application.Current!.Windows[0], path);
#else
        var img = await page.CaptureAsync();
        if (img is null) return;
        await using var s = await img.OpenReadAsync(ScreenshotFormat.Png);
        await using var f = File.Create(path);
        await s.CopyToAsync(f);
#endif
    }
}

internal static class ViewExtensions
{
    public static T Also<T>(this T v, Action<T> a) { a(v); return v; }
}
