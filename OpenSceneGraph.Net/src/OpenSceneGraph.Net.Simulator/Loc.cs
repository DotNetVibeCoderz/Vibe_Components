using System.ComponentModel;

namespace OpenSceneGraph.Net.Simulator;

/// <summary>UI strings (English / Bahasa Indonesia). XAML uses <c>{local:L Key}</c>.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly Dictionary<string, (string En, string Id)> s_strings = new()
    {
        ["AppTitle"] = ("OpenSceneGraph.Net Simulator", "Simulator OpenSceneGraph.Net"),
        ["Brand"] = ("Simulator", "Simulator"),
        ["Credit"] = ("Created by Gravicode Studios, led by Kang Fadhil", "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil"),
        ["Aircraft"] = ("Aircraft", "Pesawat"),
        ["Quadcopter"] = ("Quadcopter UAV", "UAV Quadcopter"),
        ["Ground"] = ("Tank", "Tank"),
        ["Ship"] = ("Ship", "Kapal"),
        ["Scenario"] = ("Scenario", "Skenario"),
        ["Terrain"] = ("Terrain", "Medan"),
        ["Mountains"] = ("Mountains", "Pegunungan"),
        ["Desert"] = ("Desert", "Gurun"),
        ["Highlands"] = ("Highlands", "Dataran tinggi"),
        ["Archipelago"] = ("Archipelago", "Kepulauan"),
        ["TimeOfDay"] = ("Time of day", "Waktu"),
        ["Visibility"] = ("Visibility", "Jarak pandang"),
        ["Weather"] = ("Precipitation", "Presipitasi"),
        ["Clear"] = ("None", "Cerah"),
        ["Rain"] = ("Rain", "Hujan"),
        ["Snow"] = ("Snow", "Salju"),
        ["NewTerrain"] = ("New terrain", "Medan baru"),
        ["Dis"] = ("DIS output", "Keluaran DIS"),
        ["DisHelp"] = ("Broadcasts an Entity State PDU for your vehicle on UDP 3000 at 10 Hz.",
                       "Menyiarkan Entity State PDU kendaraan Anda di UDP 3000 pada 10 Hz."),
        ["PdusSent"] = ("PDUs sent", "PDU terkirim"),
        ["DisOff"] = ("Off", "Mati"),
        ["DisError"] = ("Network error", "Galat jaringan"),
        ["Camera"] = ("Camera", "Kamera"),
        ["Chase"] = ("Chase", "Kejar"),
        ["Cockpit"] = ("Cockpit", "Kokpit"),
        ["Pause"] = ("Pause", "Jeda"),
        ["Resume"] = ("Resume", "Lanjut"),
        ["Reset"] = ("Reset", "Ulang"),
        ["About"] = ("About", "Tentang"),
        ["Close"] = ("Close", "Tutup"),
        ["KeyMap"] = ("Key map", "Peta tombol"),
        ["Mission"] = ("Mission", "Misi"),
        ["Waypoint"] = ("Waypoint", "Waypoint"),
        ["Distance"] = ("Distance", "Jarak"),
        ["Bearing"] = ("Bearing", "Arah"),
        ["Eta"] = ("ETA", "Tiba"),
        ["MissionComplete"] = ("Mission complete", "Misi selesai"),
        ["MissionCompleteHelp"] = ("All waypoints reached. Press R to fly it again.", "Semua waypoint tercapai. Tekan R untuk mengulang."),
        ["Crashed"] = ("Crashed", "Jatuh"),
        ["CrashedHelp"] = ("Terrain impact. Press R to reset.", "Menabrak medan. Tekan R untuk mengulang."),
        ["Aground"] = ("Aground", "Kandas"),
        ["AgroundHelp"] = ("Shallow water ahead. Turn back to open sea.", "Perairan dangkal. Putar balik ke laut lepas."),
        ["Boundary"] = ("Training area boundary", "Batas area latihan"),
        ["Paused"] = ("Paused", "Dijeda"),
        ["PausedHelp"] = ("Press P to resume.", "Tekan P untuk melanjutkan."),
        ["Speed"] = ("SPD", "KEC"),
        ["Altitude"] = ("ALT", "TING"),
        ["Agl"] = ("AGL", "AGL"),
        ["Vs"] = ("V/S", "V/S"),
        ["Throttle"] = ("THR", "GAS"),
        ["Heading"] = ("HDG", "ARAH"),
        ["Map"] = ("Map", "Peta"),
        ["AboutLead"] = ("A training-style vehicle simulator built on OpenSceneGraph.Net: vehicle dynamics from OpenSceneGraph.Net.Simulation, procedural terrain with LOD, particles for weather, an Avalonia HUD over the OpenGL view and DIS Entity State output for distributed exercises.",
                         "Simulator kendaraan bergaya pelatihan yang dibangun di atas OpenSceneGraph.Net: dinamika kendaraan dari OpenSceneGraph.Net.Simulation, medan prosedural dengan LOD, partikel cuaca, HUD Avalonia di atas tampilan OpenGL, dan keluaran DIS Entity State untuk latihan terdistribusi."),
        ["License"] = ("MIT license", "Lisensi MIT"),
        ["RenderFailed"] = ("OpenGL could not start on this machine. Update the graphics driver or run outside a remote session.",
                            "OpenGL tidak dapat berjalan di mesin ini. Perbarui driver grafis atau jalankan di luar sesi remote."),
        // key map actions
        ["kThrottle"] = ("Throttle up / down", "Gas naik / turun"),
        ["kRoll"] = ("Bank left / right", "Miring kiri / kanan"),
        ["kPitch"] = ("Nose up / down", "Hidung naik / turun"),
        ["kRudder"] = ("Rudder left / right", "Rudder kiri / kanan"),
        ["kMove"] = ("Fly forward / back", "Terbang maju / mundur"),
        ["kStrafe"] = ("Slide left / right", "Geser kiri / kanan"),
        ["kClimb"] = ("Climb / descend", "Naik / turun"),
        ["kYaw"] = ("Turn left / right", "Putar kiri / kanan"),
        ["kDrive"] = ("Accelerate / brake", "Gas / rem"),
        ["kSteer"] = ("Steer", "Belok"),
        ["kTurret"] = ("Turret left / right", "Turret kiri / kanan"),
        ["kTelegraph"] = ("Engine telegraph ahead / astern", "Telegraf mesin maju / mundur"),
        ["kHelm"] = ("Helm", "Kemudi"),
        ["kCamera"] = ("Chase / cockpit camera", "Kamera kejar / kokpit"),
        ["kPause"] = ("Pause", "Jeda"),
        ["kReset"] = ("Reset vehicle and mission", "Ulang kendaraan dan misi"),
        ["kZoom"] = ("Wheel: chase distance", "Roda: jarak kamera"),
    };

    // declared after the table so static initialisation order is correct
    public static Loc Instance { get; } = new();

    private string _lang = "en";

    private Loc() => T = BuildTable("en");

    public IReadOnlyDictionary<string, string> T { get; private set; }

    public string Lang
    {
        get => _lang;
        set
        {
            if (_lang == value) return;
            _lang = value;
            T = BuildTable(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(T)));
            LanguageChanged?.Invoke();
        }
    }

    public bool IsIndonesian => _lang == "id";
    public string this[string key] => T.TryGetValue(key, out var v) ? v : key;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    private static Dictionary<string, string> BuildTable(string lang) =>
        s_strings.ToDictionary(kv => kv.Key, kv => lang == "id" ? kv.Value.Id : kv.Value.En);
}
