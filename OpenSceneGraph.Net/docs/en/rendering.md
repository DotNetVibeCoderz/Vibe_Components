# Rendering

## Renderers

| Renderer | Where | Capabilities |
|---|---|---|
| `GlRenderer` | Avalonia `OsgView`, or any GL 3.3 / GLES 3 context (`new GlRenderer(GL.GetApi(...), isGles)`) | Lighting, textures, shadows, fog, custom shaders, RTT, post-processing, stereo, skinning |
| Blazor WebGL2 | `OsgCanvas` | Same shader library: lighting, textures, shadows, fog, RTT, post-processing, skinning. Wireframe is drawn as triangle edges. **Custom `ShaderProgram`s are ignored (the main shader and the material are used) and stereo is ignored**; line width is always 1 |
| Native OSG | `NativeViewer` | OSG's fixed-function and GLSL pipeline on the converted scene |

Check `renderer.Capabilities` (`RendererCapabilities` flags) to adapt your UI.

## Environment

`viewer.Environment` is a `SceneEnvironment`:

```csharp
var env = viewer.Environment;
env.SkyTopColor = Color.FromHex("#2a6fb0");          // gradient to Camera.ClearColor at the bottom
env.AmbientLight = new Vector3(0.25f);
env.HeadLight = true;                                // light attached to the camera
env.Fog = new FogSettings(true, new Vector3(0.7f, 0.8f, 0.9f), start: 300, end: 2000);
env.Shadows.Enabled = true;                          // first shadow-casting directional light
env.Shadows.Radius = 80;                             // metres around the manipulator centre
env.Post.Bloom = true; env.Post.Hdr = true; env.Post.Exposure = 1.1f; env.Post.Vignette = true;
```

Lights come from `LightSource` nodes (up to 8 per frame, including the headlight): `Light.Sun(...)`, `Light.Point(...)`, and spot lights (`LightType.Spot`, `SpotCutoffDegrees`).

## Materials and shading

Blinn-Phong with `Diffuse` (RGBA), `Ambient`, `Specular`, `Shininess` and `Emission`. `Reflectivity` adds a Fresnel environment reflection using `Environment.ReflectionColor`, which is good for water, glass and metal. Per-vertex colours multiply the diffuse colour, and textures multiply both.

## Shadows

A directional shadow map with texel snapping and 3×3 PCF. Per-object control: `StateSet.CastShadows` and `StateSet.ReceiveShadows`. Settings: `MapSize`, `Radius`, `Bias`, `Softness`.

## Points, lines and particles

Points with `TexCoords` are drawn as soft round sprites whose size `TexCoords.X` is in world units, attenuated by distance. `ParticleSystem` uses this. Points without `TexCoords` use `StateSet.PointSize` in pixels. Line widths above 1 are honoured only where the driver supports them.

## Cameras in the graph

```csharp
// HUD: pixel coordinates, origin bottom-left, drawn after the 3D scene
var hud = new CameraNode { RenderOrder = CameraRenderOrder.PostRender };
hud.AddChild(MatrixTransform.Translate(20, 20, 0).AddChild(panel));

// Render to texture: the result can be used as a texture anywhere in the scene
var target = new Texture2D(512, 512);
var rtt = new CameraNode { RenderOrder = CameraRenderOrder.PreRender, RenderTarget = target,
                           ViewMatrix = mirrorView, ProjectionMatrix = mirrorProj, ClearColor = Color.Black };
rtt.AddChild(sceneToMirror);
screen.GetOrCreateStateSet().Texture = target;
```

## Stereo

`viewer.Camera.Stereo = StereoMode.AnaglyphRedCyan` (also `SideBySide` and `TopBottom`), with `EyeSeparation` in metres. Desktop renderer only; `OsgCanvas` ignores it.

## Custom shaders

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

The renderer prepends the GLSL version header, the attributes (`osg_Vertex`, `osg_Normal`, `osg_Color`, `osg_MultiTexCoord0`) and the built-in uniforms (`osg_ModelMatrix`, `osg_ViewMatrix`, `osg_ProjectionMatrix`, `osg_NormalMatrix`, `osg_SimulationTime`, `osg_ViewportSize`, `osg_EyePosition`). Write GLSL ES 3.00-compatible code so the same program runs on desktop GL and on ANGLE. The Blazor renderer doesn't run custom programs yet ([roadmap](../../PLAN.md)): it draws the geometry with the main shader, so also set a `Material` as a fallback.

## Writing your own renderer

Implement `IRenderer.Render(RenderList)`. The shader sources in `ShaderLibrary` and `GlRenderer` are the reference implementation of every `ResolvedState` field.
