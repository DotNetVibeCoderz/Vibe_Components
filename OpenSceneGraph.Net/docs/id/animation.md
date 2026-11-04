# Animasi dan efek

## Update callback

```csharp
node.OnUpdate((n, frame) => ((PositionAttitudeTransform)n).Position = Orbit(frame.SimulationTime));
node.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, degreesPerSecond: 90));
```

`FrameStamp` membawa `FrameNumber`, `ReferenceTime`, `SimulationTime`, dan `DeltaTime`. `Viewer.FixedTimeStep`, `TimeScale`, dan `Paused` mengendalikan waktu simulasi.

## Animation path (`osg::AnimationPath`)

```csharp
var path = AnimationPath.ThroughWaypoints(waypoints, speed: 25);         // berorientasi mengikuti lintasan
var orbit = AnimationPath.Circle(Vector3.Zero, radius: 200, period: 60, height: 120);
path.Insert(12.5, new ControlPoint(position, rotation, scale));          // key kustom, LoopMode Loop/Swing/NoLoop
vehicle.AddUpdateCallback(new AnimationPathCallback(path) { TimeOffset = 3, TimeMultiplier = 1.5 });
viewer.CameraManipulator = new AnimationPathManipulator(path);           // kamera fly-through
```

## Klip keyframe (`osgAnimation`)

```csharp
var clip = new AnimationClip("wave") { LoopMode = LoopMode.Loop };
clip.Rotation("arm").Add(0, Quaternion.Identity).Add(0.5, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1)).Add(1, Quaternion.Identity);
clip.Translation("body").Add(0, Vector3.Zero).Add(1, new Vector3(0, 0, 0.3f)).Interpolation = Interpolation.CubicHermite;

var manager = new AnimationManager().Register(clip);
manager.Play("wave", weight: 1);
character.AddUpdateCallback(manager);     // target dicari berdasarkan nama node (PAT atau Bone)
```

## Animasi skeletal dan skinning

`Skeleton` adalah root dan `Bone` adalah transform bersarang dengan inverse bind matrix. `RigGeometry` membawa hingga empat `BoneIndices`/`BoneWeights` per vertex dan di-skin di CPU (linear blend, paralel untuk mesh besar), sehingga setiap renderer dapat menggambarnya.

```csharp
var skeleton = new Skeleton();
var hip = new Bone("hip"); skeleton.AddChild(hip);
var knee = hip.AddBone(new Bone("knee") { Translation = new Vector3(0, 0, -0.5f) });
skeleton.SetBindPose();                       // menangkap inverse bind matrix

var skin = new RigGeometry { Positions = ..., Normals = ..., Indices = ..., BoneIndices = ..., BoneWeights = ..., Skeleton = skeleton };
skin.CaptureBindPose();
skeleton.AddChild(skin);
skeleton.AddUpdateCallback(manager);          // menggerakkan bone
skeleton.AddUpdateCallback(new SkinningCallback());
```

`RigFactory.Tentacle()` membangun contoh lengkap: rantai bone, bobot, dan klip yang berulang.

## Partikel (`osgParticle`)

`ParticleSystem` adalah `Geometry` berisi point sprite dengan sebuah `Emitter` (titik, kotak, bola, atau cakram; laju; arah kerucut; rentang kecepatan dan umur), gravitasi, angin, hambatan (drag), serta warna/ukuran sepanjang umur partikel. Sistem ini menggunakan double buffering untuk rendering yang di-pipeline. Integrasi menggunakan Rust/rayon untuk sistem dengan lebih dari 4k partikel.

```csharp
root.AddChild(ParticleEffects.Fire(position, scale: 2));
root.AddChild(ParticleEffects.Smoke(position + Vector3.UnitZ * 2, 2));
root.AddChild(ParticleEffects.Rain(center, area: 80, height: 30));
var boom = ParticleEffects.Explosion(target, scale: 3, autoBurst: false);
root.AddChild(boom);
boom.Burst(800);                              // mis. saat terjadi benturan
trail.Emitter.Position = missile.Position;    // emitter bergerak untuk jejak (trail)
```

Preset: `Smoke`, `Fire`, `Explosion`, `Rain`, `Snow`, `Dust`, `Sparks`. Gunakan additive blending (`SetBlendAdditive(true)`) untuk efek emisif dan kombinasikan dengan `Environment.Post.Bloom`.
