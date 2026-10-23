using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CesiumNet.Interop;

namespace CesiumNet.Avalonia;

/// <summary>
/// A tiny loopback-only HTTP server that gives the embedded WebView a real origin
/// (<c>http://localhost:{port}/</c>). It serves the host page, the Cesium.Net bridge and any folders you map —
/// for example your own glTF models, GeoJSON files, or a self-hosted CesiumJS build for offline use.
/// </summary>
/// <example><code>
/// var modelsUrl = CesiumLocalServer.Shared.MapFolder("models", @"C:\data\models");
/// // modelsUrl + "truck.glb" can now be used as a ModelGraphics.Uri
/// </code></example>
public sealed class CesiumLocalServer : IDisposable
{
    private static readonly Lazy<CesiumLocalServer> s_shared = new(() => new CesiumLocalServer());
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] _page;
    private readonly byte[] _bridge;

    /// <summary>The process-wide server used by all <see cref="CesiumView"/> controls.</summary>
    public static CesiumLocalServer Shared => s_shared.Value;

    /// <summary>Base URL, e.g. <c>http://localhost:51234/</c>.</summary>
    public Uri BaseUrl { get; }

    private CesiumLocalServer()
    {
        _page = Encoding.UTF8.GetBytes(HostPage);
        _bridge = Encoding.UTF8.GetBytes(CesiumScripts.BridgeScript);
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = FreePort();
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                BaseUrl = new Uri($"http://localhost:{port}/");
                _ = AcceptLoopAsync();
                return;
            }
            catch (HttpListenerException ex) { last = ex; }
        }
        throw new InvalidOperationException("Cesium.Net could not start its local content server.", last);
    }

    /// <summary>Serves files from <paramref name="physicalPath"/> under <c>/{name}/</c>. Returns the folder URL.</summary>
    public string MapFolder(string name, string physicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Directory.Exists(physicalPath)) throw new DirectoryNotFoundException(physicalPath);
        name = name.Trim('/');
        if (name.Contains('/') || name.Contains('\\')) throw new ArgumentException("Folder name must be a single path segment.", nameof(name));
        _folders[name] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(physicalPath)) + Path.DirectorySeparatorChar;
        return new Uri(BaseUrl, name + "/").ToString();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (!_listener.IsListening) { return; }
            catch (HttpListenerException) { continue; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            var path = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/");
            res.Headers["Cache-Control"] = "no-cache";
            if (path is "/" or "/index.html")
            {
                await WriteAsync(res, _page, "text/html; charset=utf-8").ConfigureAwait(false);
                return;
            }
            if (path == "/cesium-net.js")
            {
                await WriteAsync(res, _bridge, "text/javascript; charset=utf-8").ConfigureAwait(false);
                return;
            }

            var trimmed = path.TrimStart('/');
            var slash = trimmed.IndexOf('/');
            if (slash > 0 && _folders.TryGetValue(trimmed[..slash], out var root))
            {
                var full = Path.GetFullPath(Path.Combine(root, trimmed[(slash + 1)..]));
                // Prevent path traversal outside the mapped folder.
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    res.ContentType = MimeType(full);
                    res.Headers["Cache-Control"] = "max-age=3600";
                    await using var fs = File.OpenRead(full);
                    res.ContentLength64 = fs.Length;
                    await fs.CopyToAsync(res.OutputStream).ConfigureAwait(false);
                    return;
                }
            }
            res.StatusCode = 404;
        }
        catch (Exception) { try { res.StatusCode = 500; } catch { /* response already started */ } }
        finally { try { res.Close(); } catch { /* client gone */ } }
    }

    private static async Task WriteAsync(HttpListenerResponse res, byte[] body, string contentType)
    {
        res.ContentType = contentType;
        res.ContentLength64 = body.Length;
        await res.OutputStream.WriteAsync(body).ConfigureAwait(false);
    }

    private static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".js" or ".mjs" => "text/javascript",
        ".css" => "text/css",
        ".json" or ".geojson" or ".topojson" or ".czml" => "application/json",
        ".glb" => "model/gltf-binary",
        ".gltf" => "model/gltf+json",
        ".b3dm" or ".i3dm" or ".pnts" or ".cmpt" or ".subtree" or ".terrain" => "application/octet-stream",
        ".kml" => "application/vnd.google-earth.kml+xml",
        ".kmz" => "application/vnd.google-earth.kmz",
        ".gpx" => "application/gpx+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".ktx2" => "image/ktx2",
        ".wasm" => "application/wasm",
        ".xml" => "application/xml",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* ignore */ }
    }

    private const string HostPage = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Cesium.Net</title>
<style>html,body,#cesium{margin:0;padding:0;width:100%;height:100%;overflow:hidden;background:#0b1016}</style>
<script src="/cesium-net.js"></script>
</head>
<body>
<div id="cesium"></div>
<script>
(function () {
  function post(msg) {
    if (window.invokeCSharpAction) { window.invokeCSharpAction(msg); return; }
    var text = JSON.stringify(msg);
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(text);
    else if (window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.invokeCSharpAction) window.webkit.messageHandlers.invokeCSharpAction.postMessage(text);
  }
  window.__cesiumNetReply = post;
  window.__cesiumNetStart = function (optionsJson) {
    CesiumNet.create('cesium', optionsJson, function (name, json) { post({ type: 'event', name: name, data: json }); })
      .then(function (id) { post({ type: 'created', viewerId: id }); },
            function (e) { post({ type: 'failed', error: String(e && e.message || e) }); });
    return true;
  };
  window.addEventListener('error', function (e) { post({ type: 'event', name: 'error', data: JSON.stringify({ message: String(e.message), source: 'window' }) }); });
  post({ type: 'loaded' });
})();
</script>
</body>
</html>
""";
}
