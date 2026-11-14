using System.Globalization;
using Medical.Dicom;

namespace Medical.Cli;

/// <summary>v2.1 interface commands: DICOM Modality Worklist query (SCU) and a demo worklist server (SCP).</summary>
internal static class InterfaceCommands
{
    private static string T(string lang, string en, string id) => lang == "id" ? id : en;

    // ------------------------------------------------------------------ medical worklist query|serve

    public static async Task<int> Worklist(Args a, string lang) => a.Positional(0, "sub-command") switch
    {
        "query" => await Query(a, lang),
        "serve" => await Serve(a, lang),
        _ => throw new ArgumentException("worklist query <host> <port> | worklist serve [--port 11112]"),
    };

    private static async Task<int> Query(Args a, string lang)
    {
        string host = a.Positional(1, "host");
        int port = int.Parse(a.Positional(2, "port"), CultureInfo.InvariantCulture);
        var (from, to) = DateRange(a.Opt("date"));
        var q = new WorklistQuery
        {
            Modality = a.Opt("modality")?.ToUpperInvariant(), PatientName = a.Opt("name"), PatientId = a.Opt("patient-id"), AccessionNumber = a.Opt("accession"),
            ScheduledStationAeTitle = a.Opt("station"), DateFrom = from, DateTo = to,
        };
        string calledAe = a.Opt("aet") ?? ModalityWorklistServer.DefaultAeTitle, callingAe = a.Opt("calling") ?? "MEDICALNET";
        var items = await ModalityWorklistClient.QueryItemsAsync(host, port, callingAe, calledAe, q);
        Console.WriteLine(T(lang, $"Modality Worklist {host}:{port} (AE {calledAe}): {items.Count} scheduled procedure step(s)",
            $"Modality Worklist {host}:{port} (AE {calledAe}): {items.Count} langkah prosedur terjadwal"));
        foreach (var i in items.OrderBy(i => i.ScheduledStart))
            Console.WriteLine($"  {i.ScheduledStart:yyyy-MM-dd HH:mm}  {i.Modality,-3} {i.ScheduledStationAeTitle ?? "-",-10} {i.AccessionNumber,-14} {i.PatientId,-8} {i.PatientName,-22} {i.RequestedProcedureDescription}  [{i.RequestedProcedurePriority}]  {i.StudyInstanceUid}");
        return 0;
    }

    private static async Task<int> Serve(Args a, string lang)
    {
        int port = (int)(a.OptNum("port") ?? 11112);
        var day = a.Opt("date") is { } d ? DateOnly.ParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture) : DateOnly.FromDateTime(DateTime.Now);
        using var server = ModalityWorklistServer.Start(port, InMemoryWorklistSource.Demo(day));
        Console.WriteLine(T(lang, $"Demo Modality Worklist SCP (AE {ModalityWorklistServer.DefaultAeTitle}) on port {port}, 5 items for {day:yyyy-MM-dd}. Ctrl+C to stop.",
            $"SCP Modality Worklist demo (AE {ModalityWorklistServer.DefaultAeTitle}) di port {port}, 5 item untuk {day:yyyy-MM-dd}. Ctrl+C untuk berhenti."));
        Console.WriteLine(T(lang, $"Try: medical worklist query 127.0.0.1 {port} --modality CT --date {day:yyyyMMdd}",
            $"Coba: medical worklist query 127.0.0.1 {port} --modality CT --date {day:yyyyMMdd}"));
        var done = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
        await done.Task;
        return 0;
    }

    /// <summary>"20261116", "20261116-20261120", "20261116-" or "-20261120" (DICOM DA range).</summary>
    private static (DateOnly? From, DateOnly? To) DateRange(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null);
        static DateOnly? P(string v) => v.Length == 0 ? null : DateOnly.ParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture);
        int dash = s.IndexOf('-');
        return dash < 0 ? (P(s), P(s)) : (P(s[..dash]), P(s[(dash + 1)..]));
    }
}
