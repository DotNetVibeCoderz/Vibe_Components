# Computer vision (Blazor.AI.MediaPipe)

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/vision.md)

`Blazor.AI.MediaPipe` menghadirkan computer vision real-time ke Blazor: face mesh dan ekspresi, absensi dan verifikasi wajah, tangan dan gesture, bahasa isyarat, pose tubuh dan analitik olahraga, holistic tracking dan motion capture, penghapusan latar belakang, deteksi objek, klasifikasi gambar, vision copilot, tampilan Three.js, serta component siap pakai untuk kesehatan, agritech, edukasi, industri dan analitik.

Package ini berjalan di atas **MediaPipe.NET** (`Gravicode.MediaPipeNet.Tasks.Vision`), port .NET native dari Google MediaPipe Tasks di atas ONNX Runtime. Inferensi dilakukan di server; browser hanya menangkap frame dan menggambar hasilnya.

![Halaman vision di gallery](../screenshots/vision-core.jpg)

## Persiapan

```bash
dotnet add package Blazor.AI.MediaPipe
```

```csharp
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddBlazorAI(builder.Configuration).AddSimulated();
builder.Services.AddBlazorAIComponents();
builder.Services.AddBlazorAIMediaPipe(o =>
{
    o.PreloadModels = true;                    // unduh model saat startup, bukan saat pertama dipakai
    // o.Provider = ExecutionProvider.Cuda;    // atau DirectML / CoreML; kembali ke CPU bila tidak tersedia
    // o.ModelDirectory = "/opt/models";       // offline / edge: model yang sudah disalin
    // o.AllowModelDownload = false;           // lokasi tanpa internet
});
```

```html
<link rel="stylesheet" href="_content/Blazor.AI.Components/blazor-ai.css" />
<link rel="stylesheet" href="_content/Blazor.AI.MediaPipe/blazor-ai-vision.css" />
```

Gunakan render mode **Interactive Server** untuk component vision. ONNX Runtime tidak berjalan di dalam WebAssembly browser, jadi aplikasi WebAssembly membutuhkan server untuk menerima frame.

## Alur frame

1. `CameraViewer` mengambil frame dari webcam, file video, gambar atau stream HTTP, memperkecilnya ke `MaxWidth` (default 640 px) dan meng-encode-nya sebagai JPEG.
2. JPEG dikirim ke server lewat circuit Blazor. Hanya satu frame yang diproses per viewer: frame berikutnya diambil setelah hasil sebelumnya kembali, sehingga perangkat yang lambat melewatkan frame alih-alih menumpuknya.
3. Method `Process(VisionFrame)` milik component menjalankan task MediaPipe dalam mode VIDEO (dengan tracking dan smoothing) untuk sumber live, dan mode IMAGE untuk gambar diam.
4. Hasilnya berupa `VisionOverlay` (landmark, kotak, label, garis, heat grid, mask) dalam koordinat ternormalisasi, yang digambar browser di atas gambar, dicerminkan bila perlu.

Di CPU laptop, face mesh, tangan, pose dan segmentasi masing-masing butuh sekitar 20–40 ms per frame 640×480, jadi 10–15 fps per viewer adalah hal yang wajar. `AddBlazorAIMediaPipe` menaikkan batas pesan SignalR menjadi 2 MB untuk frame.

## Membuat component sendiri

```razor
@inherits VisionComponentBase

<CameraViewer @ref="Camera" OnFrame="HandleFrameAsync" OnSourceChanged="HandleSourceChanged" Samples="@EffectiveSamples" />
<p>@_fingers jari</p>

@code {
    int _fingers;

    protected override VisionOverlay? Process(VisionFrame frame)
    {
        var hands = Tasks.Hands(frame);
        _fingers = hands.Hands.Sum(VisionMath.CountFingers);
        var overlay = new VisionOverlay { Status = $"{_fingers} jari" };
        overlay.Landmarks.AddRange(hands.Hands.Select(h => Overlay.Hand(h)));
        return overlay;
    }
}
```

- `Tasks` menyediakan `DetectFaces`, `FaceLandmarks` (478 titik, 52 blendshape), `Hands`, `Gestures`, `Pose` (33 titik, koordinat dunia, mask opsional), `Holistic`, `Segment` (Selfie, SelfieMulticlass, Hair, DeepLabV3), `SegmentAt` (klik untuk segmentasi), `Objects` (80 kelas COCO), `Classify` (1.000 kelas ImageNet), `Embed` dan `Stylize`.
- `VisionMath` berisi sudut sendi (2-D dan 3-D), jarak, status jari, eye aspect ratio dan head pose.
- `AskAboutFrameAsync(role, question, facts)` mengirim frame terakhir ke model yang mendukung vision bersama fakta hasil perhitunganmu, dan hanya mengirim fakta untuk model teks.
- `RecordEvent(summary)` menulis ke audit trail guardrail dan counter `blazorai.vision.events`.

## Privasi dan enterprise

- Frame diproses di memori lalu dibuang. Tidak ada yang disimpan kecuali kamu memakai `VisionRecorder`, snapshot atau log absensi.
- Pengenalan wajah memakai `IFaceEmbedder`. Implementasi default meng-embed potongan wajah yang diluruskan berdasarkan posisi mata dengan MobileNet (wajah yang sama skornya sekitar 0,94–0,99, orang berbeda sekitar 0,4–0,65): cocok untuk demo dan kelompok kecil yang kooperatif, dan **bukan tingkat biometrik**. Daftarkan embedder sendiri (misalnya model ONNX ArcFace) untuk identitas di produksi, dan minta persetujuan.
- RBAC dan audit berasal dari guardrail Blazor.AI (`RecordEvent` masuk ke `IAuditTrail`).
- OpenTelemetry: tambahkan source dan meter bernama `Blazor.AI.MediaPipe` (`VisionTelemetry.Name`) untuk trace (`vision.frame`) dan metrik (`blazorai.vision.frames`, `blazorai.vision.latency`, `blazorai.vision.errors`, `blazorai.vision.events`).
- Offline dan edge: atur `ModelDirectory` dan `AllowModelDownload = false`, lalu gunakan `ModelManagerPanel` atau `VisionModelManager.EnsureAsync` untuk mengunduh model lebih awal. GPU: atur `Provider` ke CUDA, DirectML atau CoreML.
- Multi-tenant: service bersifat scoped per circuit (misalnya `FaceGallery` dan `VisionEventStream`); simpan gallery per tenant di storage milikmu.

## Sumber gambar

`CameraViewer` menerima webcam (depan atau belakang, perangkat apa pun), gambar dan video yang diunggah, URL gambar atau video, stream MJPEG lewat HTTP, dan HLS. Kamera RTSP butuh bridge yang menyediakan HTTP (MJPEG atau HLS), seperti go2rtc atau MediaMTX. Stream dari origin lain membutuhkan header CORS agar frame bisa dibaca.

## Component

Lihat [katalog component](components.md#blazoraimediapipe). Setiap component punya demo live dengan foto contoh (dan kameramu) di halaman Vision pada gallery.

| | |
|---|---|
| ![Gesture](../screenshots/vision-gestures.jpg) | ![Yoga trainer](../screenshots/vision-yoga.jpg) |
| ![Vision copilot](../screenshots/vision-copilot.jpg) | ![Deteksi jatuh](../screenshots/vision-fall.jpg) |
