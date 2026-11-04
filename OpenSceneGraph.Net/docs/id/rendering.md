# Rendering

## Renderer

| Renderer | Tempat | Kemampuan |
|---|---|---|
| `GlRenderer` | `OsgView` Avalonia, atau context GL 3.3 / GLES 3 apa pun (`new GlRenderer(GL.GetApi(...), isGles)`) | Pencahayaan, texture, bayangan, kabut, shader kustom, RTT, post-processing, stereo, skinning |
| Blazor WebGL2 | `OsgCanvas` | Library shader yang sama: pencahayaan, texture, bayangan, kabut, RTT, post-processing, skinning. Wireframe digambar sebagai tepi segitiga. **`ShaderProgram` kustom diabaikan (yang dipakai shader utama dan material) dan stereo diabaikan**; lebar garis selalu 1 |
| OSG native | `NativeViewer` | Pipeline fixed-function dan GLSL milik OSG pada scene hasil konversi |

Periksa `renderer.Capabilities` (flag `RendererCapabilities`) untuk menyesuaikan UI Anda.

## Environment

`viewer.Environment` adalah sebuah `SceneEnvironment`:

```csharp
var env = viewer.Environment;
env.SkyTopColor = Color.FromHex("#2a6fb0");          // gradasi ke Camera.ClearColor di bagian bawah
env.AmbientLight = new Vector3(0.25f);
env.HeadLight = true;                                // lampu yang menempel pada kamera
env.Fog = new FogSettings(true, new Vector3(0.7f, 0.8f, 0.9f), start: 300, end: 2000);
env.Shadows.Enabled = true;                          // lampu directional pertama yang menghasilkan bayangan
env.Shadows.Radius = 80;                             // meter di sekitar pusat manipulator
env.Post.Bloom = true; env.Post.Hdr = true; env.Post.Exposure = 1.1f; env.Post.Vignette = true;
```

Lampu berasal dari node `LightSource` (hingga 8 per frame, termasuk headlight): `Light.Sun(...)`, `Light.Point(...)`, dan spot light (`LightType.Spot`, `SpotCutoffDegrees`).

## Material dan shading

Blinn-Phong dengan `Diffuse` (RGBA), `Ambient`, `Specular`, `Shininess`, dan `Emission`. `Reflectivity` menambahkan refleksi lingkungan Fresnel menggunakan `Environment.ReflectionColor`, yang cocok untuk air, kaca, dan logam. Warna per vertex dikalikan dengan warna diffuse, dan texture dikalikan dengan keduanya.

## Bayangan

Shadow map directional dengan texel snapping dan PCF 3×3. Kontrol per objek: `StateSet.CastShadows` dan `StateSet.ReceiveShadows`. Pengaturan: `MapSize`, `Radius`, `Bias`, `Softness`.

## Titik, garis, dan partikel

Titik yang memiliki `TexCoords` digambar sebagai sprite bulat lembut dengan ukuran `TexCoords.X` dalam satuan dunia, yang mengecil sesuai jarak. `ParticleSystem` memanfaatkan hal ini. Titik tanpa `TexCoords` menggunakan `StateSet.PointSize` dalam piksel. Lebar garis di atas 1 hanya berlaku jika didukung oleh driver.

## Kamera di dalam graph

```csharp
// HUD: koordinat piksel, origin di kiri bawah, digambar setelah scene 3D
var hud = new CameraNode { RenderOrder = CameraRenderOrder.PostRender };
hud.AddChild(MatrixTransform.Translate(20, 20, 0).AddChild(panel));

// Render to texture: hasilnya dapat dipakai sebagai texture di mana saja dalam scene
var target = new Texture2D(512, 512);
var rtt = new CameraNode { RenderOrder = CameraRenderOrder.PreRender, RenderTarget = target,
                           ViewMatrix = mirrorView, ProjectionMatrix = mirrorProj, ClearColor = Color.Black };
rtt.AddChild(sceneToMirror);
screen.GetOrCreateStateSet().Texture = target;
```

## Stereo

`viewer.Camera.Stereo = StereoMode.AnaglyphRedCyan` (juga `SideBySide` dan `TopBottom`), dengan `EyeSeparation` dalam meter. Hanya untuk renderer desktop; `OsgCanvas` mengabaikannya.

## Shader kustom

```csharp
geometry.GetOrCreateStateSet().Program = new ShaderProgram
{
    VertexSource = """
        out vec3 vN;
        void main() {
            vN = osg_NormalMatrix * osg_Normal;
            gl_Position = osg_ProjectionMatrix * osg_ViewMatrix * osg_ModelMatrix * vec4(osg_Vertex, 1.0);
        }
        """,
    FragmentSource = """
        in vec3 vN; out vec4 fragColor; uniform vec3 tint;
        void main() { fragColor = vec4(tint * (0.5 + 0.5 * normalize(vN).z), 1.0); }
        """,
};
geometry.StateSet.Uniforms.Set("tint", new Vector3(1, 0.4f, 0.2f));
```

Renderer menambahkan di awal header versi GLSL, atribut (`osg_Vertex`, `osg_Normal`, `osg_Color`, `osg_MultiTexCoord0`), dan uniform bawaan (`osg_ModelMatrix`, `osg_ViewMatrix`, `osg_ProjectionMatrix`, `osg_NormalMatrix`, `osg_SimulationTime`, `osg_ViewportSize`, `osg_EyePosition`). Tulis kode yang kompatibel dengan GLSL ES 3.00 agar program yang sama berjalan di GL desktop dan ANGLE. Renderer Blazor belum menjalankan program kustom ([roadmap](../../PLAN.md)): geometry digambar dengan shader utama, jadi atur juga `Material` sebagai fallback.

## Membuat renderer sendiri

Implementasikan `IRenderer.Render(RenderList)`. Source shader di `ShaderLibrary` dan `GlRenderer` merupakan implementasi referensi untuk setiap field `ResolvedState`.
