# Integrasi CAD & AI

## CAD ⇄ GIS

```csharp
// Gambar CAD tidak memiliki CRS: lakukan georeferensi dengan dua titik kontrol
var georef = CadGeoreference.FromControlPoints((0, 0), (702300, 9311800), (100, 0), (702392.4, 9311838.3));
var siteplan = Dxf.Read("siteplan.dxf", new DxfReadOptions { Georeference = georef, Crs = CoordinateReferenceSystem.FromEpsg(32748) });
siteplan.ToFile("siteplan.gpkg");                                    // CAD → GIS
Dxf.Write(persil, "persil.dxf", layerColumn: "zonasi", textColumn: "nib");   // GIS → CAD
```

Entitas yang didukung: POINT, LINE, LWPOLYLINE (termasuk bulge/busur), POLYLINE, CIRCLE, ARC, ELLIPSE, SPLINE, TEXT, MTEXT, INSERT dan 3DFACE. DWG/DGN masih dalam roadmap; untuk sementara konversikan dulu ke DXF.

## GeoLLM: bertanya ke peta dengan bahasa alami

`GeoAssistant` mengirim skema data ke LLM. LLM hanya boleh menjawab dengan rencana JSON yang memakai operasi ter-whitelist (filter, sort, head, select, area, length, centroid, buffer, to_crs, bbox, nearest_to, dissolve, groupby, count). GeoPandas.Net lalu mengeksekusi rencana tersebut secara deterministik. Kode yang dihasilkan LLM tidak pernah dijalankan.

```csharp
using var llm = new OpenAiChatClient(LlmProvider.AzureOpenAI, "https://<resource>.openai.azure.com/", apiKey, "gpt-5-mini");
var asisten = new GeoAssistant(llm);
var jawaban = await asisten.AskAsync(kecamatan, "Tampilkan 5 distrik terpadat di wilayah Selatan beserta luasnya");
Console.WriteLine(jawaban.Explanation);
Console.WriteLine(jawaban.Result);
var ringkasan = await asisten.SummarizeAsync(kecamatan, "Bahasa Indonesia");
```

Konfigurasi lewat environment variable (dipakai oleh CLI `gpd ask`): `GEOPANDAS_LLM_PROVIDER` (`openai` | `azure` | `compatible`), `GEOPANDAS_LLM_ENDPOINT`, `GEOPANDAS_LLM_API_KEY`, `GEOPANDAS_LLM_MODEL`. Simpan API key di luar repository.

Sudah diuji langsung dengan Azure OpenAI `gpt-5-mini`: pertanyaan berbahasa Indonesia menghasilkan rencana `filter → sort → area → head → select` dan jawaban dalam Bahasa Indonesia.
