using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.Serialization;
using Microsoft.Extensions.Logging;

namespace Medical.Dicom;

/// <summary>DIMSE service-class-user operations (C-ECHO, C-STORE) against a PACS / modality.</summary>
public static class DicomScu
{
    /// <summary>Verification (C-ECHO). Returns success and round-trip time.</summary>
    public static async Task<(bool Ok, TimeSpan Elapsed)> EchoAsync(string host, int port, string callingAe = "MEDICALNET", string calledAe = "ANY-SCP", CancellationToken ct = default)
    {
        var client = DicomClientFactory.Create(host, port, false, callingAe, calledAe);
        bool ok = false;
        var req = new DicomCEchoRequest { OnResponseReceived = (_, r) => ok = r.Status == DicomStatus.Success };
        await client.AddRequestAsync(req).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        await client.SendAsync(ct).ConfigureAwait(false);
        return (ok, sw.Elapsed);
    }

    /// <summary>Sends instances with C-STORE; returns the number stored successfully.</summary>
    public static async Task<int> StoreAsync(string host, int port, IEnumerable<DicomFile> files, string callingAe = "MEDICALNET", string calledAe = "ANY-SCP", CancellationToken ct = default)
    {
        var client = DicomClientFactory.Create(host, port, false, callingAe, calledAe);
        int stored = 0;
        foreach (var f in files)
            await client.AddRequestAsync(new DicomCStoreRequest(f)
            {
                OnResponseReceived = (_, r) => { if (r.Status == DicomStatus.Success) Interlocked.Increment(ref stored); },
            }).ConfigureAwait(false);
        await client.SendAsync(ct).ConfigureAwait(false);
        return stored;
    }
}

/// <summary>Destination for instances received by <see cref="StoreScp"/>.</summary>
public sealed class DicomInbox
{
    private readonly ConcurrentQueue<DicomFile> _items = new();

    public event Action<DicomFile>? Received;

    public int Count => _items.Count;
    public IReadOnlyList<DicomFile> Items => _items.ToArray();

    internal void Add(DicomFile f)
    {
        _items.Enqueue(f);
        Received?.Invoke(f);
    }
}

/// <summary>
/// Storage + Verification SCP. Start with <see cref="StartServer"/>; received instances land in the <see cref="DicomInbox"/>
/// passed as user state. Accepts all storage SOP classes in uncompressed transfer syntaxes.
/// </summary>
public sealed class StoreScp : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
{
    private static readonly DicomTransferSyntax[] Accepted =
        [DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian, DicomTransferSyntax.ExplicitVRBigEndian];

    public StoreScp(INetworkStream stream, Encoding fallbackEncoding, ILogger log, DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies) { }

    /// <summary>Starts a C-STORE/C-ECHO server on <paramref name="port"/> delivering into <paramref name="inbox"/>.</summary>
    public static IDicomServer StartServer(int port, DicomInbox inbox) => DicomServerFactory.Create<StoreScp>(port, userState: inbox);

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification || pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
                pc.AcceptTransferSyntaxes(Accepted);
            else
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
        }
        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

    public void OnConnectionClosed(Exception exception) { }

    public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        (UserState as DicomInbox)?.Add(new DicomFile(request.Dataset.Clone()));
        return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.Success));
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e) => Task.CompletedTask;

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request) => Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
}

/// <summary>DICOMweb client (PS3.18): QIDO-RS search, WADO-RS retrieve and STOW-RS store over HTTP.</summary>
public sealed class DicomWebClient(HttpClient http, string baseUrl)
{
    private readonly string _base = baseUrl.TrimEnd('/');

    public Task<DicomDataset[]> SearchStudiesAsync(IReadOnlyDictionary<string, string>? query = null, CancellationToken ct = default) =>
        QidoAsync("studies", query, ct);

    public Task<DicomDataset[]> SearchSeriesAsync(string studyUid, CancellationToken ct = default) => QidoAsync($"studies/{studyUid}/series", null, ct);

    public Task<DicomDataset[]> SearchInstancesAsync(string studyUid, string seriesUid, CancellationToken ct = default) =>
        QidoAsync($"studies/{studyUid}/series/{seriesUid}/instances", null, ct);

    private async Task<DicomDataset[]> QidoAsync(string path, IReadOnlyDictionary<string, string>? query, CancellationToken ct)
    {
        string qs = query is null || query.Count == 0 ? "" : "?" + string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/{path}{qs}");
        req.Headers.Accept.ParseAdd("application/dicom+json");
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == System.Net.HttpStatusCode.NoContent) return [];
        res.EnsureSuccessStatusCode();
        return DicomJson.ConvertJsonToDicomArray(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>WADO-RS retrieve of one instance (multipart/related; type="application/dicom").</summary>
    public async Task<DicomFile> RetrieveInstanceAsync(string studyUid, string seriesUid, string sopUid, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/studies/{studyUid}/series/{seriesUid}/instances/{sopUid}");
        req.Headers.TryAddWithoutValidation("Accept", "multipart/related; type=\"application/dicom\"");
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var boundary = res.Content.Headers.ContentType?.Parameters.FirstOrDefault(p => p.Name == "boundary")?.Value?.Trim('"');
        var part = boundary is null ? body : Multipart.Split(body, boundary).FirstOrDefault() ?? throw new InvalidDataException("Empty multipart response.");
        using var ms = new MemoryStream(part);
        return await DicomFile.OpenAsync(ms, FileReadOption.ReadAll).ConfigureAwait(false);
    }

    /// <summary>STOW-RS: stores instances in one multipart request; returns the HTTP status code.</summary>
    public async Task<int> StoreAsync(IEnumerable<DicomFile> files, CancellationToken ct = default)
    {
        string boundary = "mednet-" + Guid.NewGuid().ToString("N");
        var content = new MultipartContent("related", boundary);
        content.Headers.ContentType!.Parameters.Add(new NameValueHeaderValue("type", "\"application/dicom\""));
        foreach (var f in files)
        {
            var ms = new MemoryStream();
            await f.SaveAsync(ms).ConfigureAwait(false);
            var part = new ByteArrayContent(ms.ToArray());
            part.Headers.ContentType = new MediaTypeHeaderValue("application/dicom");
            content.Add(part);
        }
        using var res = await http.PostAsync($"{_base}/studies", content, ct).ConfigureAwait(false);
        return (int)res.StatusCode;
    }
}

/// <summary>Tiny multipart/related body splitter (RFC 2046).</summary>
internal static class Multipart
{
    public static IEnumerable<byte[]> Split(byte[] body, string boundary)
    {
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        int pos = IndexOf(body, delimiter, 0);
        while (pos >= 0)
        {
            int start = pos + delimiter.Length;
            if (start + 1 < body.Length && body[start] == '-' && body[start + 1] == '-') yield break;
            int headersEnd = IndexOf(body, "\r\n\r\n"u8.ToArray(), start);
            if (headersEnd < 0) yield break;
            int next = IndexOf(body, delimiter, headersEnd + 4);
            int end = next < 0 ? body.Length : next - 2; // strip CRLF before the delimiter
            yield return body[(headersEnd + 4)..Math.Max(headersEnd + 4, end)];
            pos = next;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from) =>
        from >= haystack.Length ? -1 : haystack.AsSpan(from).IndexOf(needle) is var i and >= 0 ? i + from : -1;
}
