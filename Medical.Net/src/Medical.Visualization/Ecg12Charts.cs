using System.Text;
using Medical.Signals;

namespace Medical.Visualization;

public static partial class Charts
{
    private static readonly EcgLead[][] Layout =
    [
        [EcgLead.I, EcgLead.aVR, EcgLead.V1, EcgLead.V4],
        [EcgLead.II, EcgLead.aVL, EcgLead.V2, EcgLead.V5],
        [EcgLead.III, EcgLead.aVF, EcgLead.V3, EcgLead.V6],
    ];

    /// <summary>
    /// Standard 12-lead printout on millimetre paper (25 mm/s, 10 mm/mV): a 3 × 4 grid of 2.5 s per lead plus a 10 s
    /// lead II rhythm strip. Leads listed in <paramref name="highlight"/> (e.g. ST elevation) get a red label.
    /// </summary>
    public static string Ecg12Grid(Ecg12 ecg, double mmPx = 3.2, IEnumerable<EcgLead>? highlight = null, string? title = null)
    {
        var marked = new HashSet<EcgLead>(highlight ?? []);
        const double seconds = 10, rowMm = 32, rhythmMm = 32;
        double mmW = seconds * 25 + 8, mmH = 3 * rowMm + rhythmMm;
        int w = (int)(mmW * mmPx), h = (int)(mmH * mmPx);
        var sb = Open(w, h, title ?? "12-lead ECG");
        sb.Append(Inv, $"<rect width=\"{w}\" height=\"{h}\" fill=\"{SvgTheme.Paper}\"/>");
        Grid(sb, mmW, mmH, mmPx);
        double pxPerSample = 25 * mmPx / ecg.SampleRate, pxPerMv = 10 * mmPx, x0 = 8 * mmPx;
        int perCol = (int)(2.5 * ecg.SampleRate);

        void Trace(float[] x, int from, int count, double left, double baseline)
        {
            var path = new StringBuilder();
            int step = Math.Max(1, (int)(1 / pxPerSample / 1.5));
            for (int i = 0; i < count && from + i < x.Length; i += step)
                path.Append(i == 0 ? 'M' : 'L').Append(F(left + i * pxPerSample)).Append(' ').Append(F(baseline - x[from + i] * pxPerMv));
            sb.Append(Inv, $"<path d=\"{path}\" fill=\"none\" stroke=\"{SvgTheme.Trace}\" stroke-width=\"1.3\" stroke-linejoin=\"round\"/>");
        }
        void Calibration(double baseline) =>
            sb.Append(Inv, $"<path d=\"M{F(1.5 * mmPx)} {F(baseline)}h{F(mmPx)}v{F(-pxPerMv)}h{F(5 * mmPx * 0.8)}v{F(pxPerMv)}h{F(mmPx)}\" fill=\"none\" stroke=\"{SvgTheme.Trace}\" stroke-width=\"1.2\" opacity=\"0.6\"/>");
        void Label(string text, double x, double y, bool hot) =>
            sb.Append(Inv, $"<text x=\"{F(x)}\" y=\"{F(y)}\" font-size=\"{F(3.6 * mmPx)}\" font-weight=\"700\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{(hot ? SvgTheme.Red : SvgTheme.Ink)}\">{E(text)}</text>");

        for (int r = 0; r < 3; r++)
        {
            double baseline = (r * rowMm + rowMm * 0.6) * mmPx;
            Calibration(baseline);
            for (int c = 0; c < 4; c++)
            {
                var lead = Layout[r][c];
                double left = x0 + c * 2.5 * 25 * mmPx;
                Trace(ecg[lead], c * perCol, perCol, left, baseline);
                if (c > 0) sb.Append(Inv, $"<path d=\"M{F(left)} {F(baseline - 4 * mmPx)}v{F(8 * mmPx)}\" stroke=\"{SvgTheme.Ink}\" stroke-width=\"1.2\"/>");
                Label(lead.ToString(), left + 1.5 * mmPx, (r * rowMm + 5.5) * mmPx, marked.Contains(lead));
            }
        }
        double rb = (3 * rowMm + rhythmMm * 0.6) * mmPx;
        Calibration(rb);
        Trace(ecg[EcgLead.II], 0, (int)(seconds * ecg.SampleRate), x0, rb);
        Label("II", x0 + 1.5 * mmPx, (3 * rowMm + 5.5) * mmPx, marked.Contains(EcgLead.II));
        sb.Append(Inv, $"<text x=\"{F(w - 6)}\" y=\"{F(h - 6)}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"end\">25 mm/s · 10 mm/mV · {F(ecg.SampleRate)} Hz</text>");
        return sb.Append("</svg>").ToString();
    }

    /// <summary>
    /// Median beats of the 12 leads with the measurement fiducials (P onset, QRS onset / offset, T end) as vertical lines —
    /// shows exactly where PR, QRS and QT were measured.
    /// </summary>
    public static string MedianBeats(Ecg12Analysis a, double mmPx = 3.2, string lang = "en")
    {
        const double cellMm = 46, cellHMm = 34;
        int cols = 4, rows = 3;
        double mmW = cols * cellMm, mmH = rows * cellHMm + 8;
        int w = (int)(mmW * mmPx), h = (int)(mmH * mmPx);
        var sb = Open(w, h, lang == "id" ? "Denyut median" : "Median beats");
        sb.Append(Inv, $"<rect width=\"{w}\" height=\"{h}\" fill=\"{SvgTheme.Paper}\"/>");
        Grid(sb, mmW, mmH, mmPx);
        // 50 mm/s for the median beat (twice paper speed), 10 mm/mV
        double pxPerSample = 50 * mmPx / a.SampleRate, pxPerMv = 10 * mmPx;
        var f = a.Fiducials;
        int len = a.MedianBeats[0].Length;
        int from = Math.Max(0, (f.POnset ?? f.QrsOnset) - (int)(0.06 * a.SampleRate));
        int to = Math.Min(len, f.TEnd + (int)(0.08 * a.SampleRate));
        double span = (to - from) * pxPerSample;
        double scale = Math.Min(1, (cellMm - 4) * mmPx / span);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var lead = Layout[r][c];
                double left = (c * cellMm + 2) * mmPx, baseline = (r * cellHMm + cellHMm * 0.62) * mmPx;
                double X(int i) => left + (i - from) * pxPerSample * scale;
                foreach (var (idx, color) in new (int?, string)[] { (f.POnset, SvgTheme.Teal), (f.QrsOnset, SvgTheme.Teal), (f.QrsOffset, SvgTheme.Amber), (f.TEnd, SvgTheme.Red) })
                    if (idx is { } i && i >= from && i < to)
                        sb.Append(Inv, $"<path d=\"M{F(X(i))} {F((r * cellHMm + 7) * mmPx)}V{F(((r + 1) * cellHMm - 2) * mmPx)}\" stroke=\"{color}\" stroke-width=\"1\" stroke-dasharray=\"3 3\"/>");
                var path = new StringBuilder();
                var m = a.MedianBeats[(int)lead];
                double b = m[Math.Max(0, f.QrsOnset - (int)(0.02 * a.SampleRate))];
                for (int i = from; i < to; i++)
                    path.Append(i == from ? 'M' : 'L').Append(F(X(i))).Append(' ').Append(F(baseline - (m[i] - b) * pxPerMv));
                bool hot = a.StemiTerritories.Count > 0 && a[lead].StJMv >= 0.1;
                sb.Append(Inv, $"<path d=\"{path}\" fill=\"none\" stroke=\"{(hot ? SvgTheme.Red : SvgTheme.Trace)}\" stroke-width=\"1.5\" stroke-linejoin=\"round\"/>");
                sb.Append(Inv, $"<text x=\"{F(left)}\" y=\"{F((r * cellHMm + 5.5) * mmPx)}\" font-size=\"{F(3.4 * mmPx)}\" font-weight=\"700\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{(hot ? SvgTheme.Red : SvgTheme.Ink)}\">{E(lead.ToString())}</text>");
                sb.Append(Inv, $"<text x=\"{F(left + (cellMm - 4) * mmPx)}\" y=\"{F((r * cellHMm + 5.5) * mmPx)}\" font-size=\"{F(2.6 * mmPx)}\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"end\">ST {F(Math.Round(a[lead].StJMv * 10, 1))} mm</text>");
            }
        string legend = lang == "id" ? "awal P · awal / akhir QRS · akhir T — 50 mm/dtk, 10 mm/mV" : "P onset · QRS onset / offset · T end — 50 mm/s, 10 mm/mV";
        sb.Append(Inv, $"<text x=\"{F(w - 6)}\" y=\"{F(h - 6)}\" font-size=\"11\" font-family=\"{SvgTheme.MonoFont}\" fill=\"{SvgTheme.Muted}\" text-anchor=\"end\">{E(legend)}</text>");
        return sb.Append("</svg>").ToString();
    }

    private static void Grid(StringBuilder sb, double mmW, double mmH, double mmPx)
    {
        var minor = new StringBuilder();
        var major = new StringBuilder();
        int w = (int)(mmW * mmPx), h = (int)(mmH * mmPx);
        for (int i = 0; i <= mmW; i++) (i % 5 == 0 ? major : minor).Append(Inv, $"M{F(i * mmPx)} 0V{h}");
        for (int j = 0; j <= mmH; j++) (j % 5 == 0 ? major : minor).Append(Inv, $"M0 {F(j * mmPx)}H{w}");
        sb.Append(Inv, $"<path d=\"{minor}\" stroke=\"{SvgTheme.GridMinor}\" stroke-width=\"0.5\"/>");
        sb.Append(Inv, $"<path d=\"{major}\" stroke=\"{SvgTheme.GridMajor}\" stroke-width=\"0.9\"/>");
    }
}
