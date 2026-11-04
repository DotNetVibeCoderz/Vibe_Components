# Animation and effects

## Update callbacks

```csharp
node.OnUpdate((n, frame) => ((PositionAttitudeTransform)n).Position = Orbit(frame.SimulationTime));
node.AddUpdateCallback(new SpinCallback(Vector3.UnitZ, degreesPerSecond: 90));
```

`FrameStamp` carries `FrameNumber`, `ReferenceTime`, `SimulationTime` and `DeltaTime`. `Viewer.FixedTimeStep`, `TimeScale` and `Paused` control simulation time.

## Animation paths (`osg::AnimationPath`)

```csharp
var path = AnimationPath.ThroughWaypoints(waypoints, speed: 25);         // oriented along the track
var orbit = AnimationPath.Circle(Vector3.Zero, radius: 200, period: 60, height: 120);
path.Insert(12.5, new ControlPoint(position, rotation, scale));          // custom keys, LoopMode Loop/Swing/NoLoop
vehicle.AddUpdateCallback(new AnimationPathCallback(path) { TimeOffset = 3, TimeMultiplier = 1.5 });
viewer.CameraManipulator = new AnimationPathManipulator(path);           // fly-through camera
```

## Keyframe clips (`osgAnimation`)

```csharp
var clip = new AnimationClip("wave") { LoopMode = LoopMode.Loop };
clip.Rotation("arm").Add(0, Quaternion.Identity).Add(0.5, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1)).Add(1, Quaternion.Identity);
clip.Translation("body").Add(0, Vector3.Zero).Add(1, new Vector3(0, 0, 0.3f)).Interpolation = Interpolation.CubicHermite;

var manager = new AnimationManager().Register(clip);
manager.Play("wave", weight: 1);
character.AddUpdateCallback(manager);     // targets are found by node name (PAT or Bone)
```

## Skeletal animation and skinning

`Skeleton` is the root and `Bone`s are nested transforms with an inverse bind matrix. `RigGeometry` carries up to four `BoneIndices`/`BoneWeights` per vertex and is skinned on the CPU (linear blend, parallel for large meshes), so every renderer can draw it.

```csharp
var skeleton = new Skeleton();
var hip = new Bone("hip"); skeleton.AddChild(hip);
var knee = hip.AddBone(new Bone("knee") { Translation = new Vector3(0, 0, -0.5f) });
skeleton.SetBindPose();                       // capture inverse bind matrices

var skin = new RigGeometry { Positions = ..., Normals = ..., Indices = ..., BoneIndices = ..., BoneWeights = ..., Skeleton = skeleton };
skin.CaptureBindPose();
skeleton.AddChild(skin);
skeleton.AddUpdateCallback(manager);          // drive bones
skeleton.AddUpdateCallback(new SkinningCallback());
```

`RigFactory.Tentacle()` builds a complete example: a bone chain, weights and a looping clip.

## Particles (`osgParticle`)

`ParticleSystem` is a `Geometry` of point sprites with an `Emitter` (point, box, sphere or disc; rate; cone direction; speed and life ranges), gravity, wind, drag, and colour/size over life. It is double-buffered for pipelined rendering. Integration uses Rust/rayon for systems above 4k particles.

```csharp
root.AddChild(ParticleEffects.Fire(position, scale: 2));
root.AddChild(ParticleEffects.Smoke(position + Vector3.UnitZ * 2, 2));
root.AddChild(ParticleEffects.Rain(center, area: 80, height: 30));
var boom = ParticleEffects.Explosion(target, scale: 3, autoBurst: false);
root.AddChild(boom);
boom.Burst(800);                              // e.g. on impact
trail.Emitter.Position = missile.Position;    // moving emitter for trails
```

Presets: `Smoke`, `Fire`, `Explosion`, `Rain`, `Snow`, `Dust`, `Sparks`. Use additive blending (`SetBlendAdditive(true)`) for emissive effects and combine with `Environment.Post.Bloom`.
