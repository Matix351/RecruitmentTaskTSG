# SDF Physics

Unity **6000.3.11f1**, URP 17.3.0. 10,000 independent balls collide with an offline-baked signed distance field. No colliders, rigidbodies, or per-ball GameObjects; no ball-to-ball collision.

For a guided explanation of the implementation, worked collision examples, and hands-on Unity exercises, read `TUTORIAL.md` in the project root.

## Run

Open `Assets/Scenes/SdfPhysics.unity` and press Play. The committed baked data is ready to use. **R** resets the rain; **Space** pauses simulation. The overlay shows ball count, smoothed wall-clock frame time, FPS, job scheduling/completion time, sleeping count, and buffer upload size. The demo starts with 10,000 balls; **x2** doubles the current count and **/2** halves it, rounding down to a minimum of 1. Set **Max Ball Count** on the component to choose an upper limit; its default of 0 adds no user-defined cap. Available memory and graphics-buffer limits still apply. Configure the starting count before entering Play.

Requires a desktop GPU with structured buffers and instancing (shader model 4.5). Windows x64 is the tested target. VSync and the frame limiter are disabled for measurement. Ball shadows are intentionally omitted to retain a single ball draw pass.

## Bake

With the demo scene open, choose **SDF Physics > Bake open scene**. The tagged plane and cylinder are sampled into `Assets/Baked/SceneSdf.bytes`, with dimensions and origin in `SceneSdf.asset`. Save both plus their `.meta` files. The runtime only deserializes those distances into a persistent native array; it never evaluates geometry or bakes.

**SDF Physics > Create demo scene** recreates the demo and its assets. It asks Unity to save a modified scene first. This is a setup tool, not a runtime dependency. Use the bake command for ordinary geometry edits.

The baker supports root-level rotated/translated primitive planes and circular cylinders with positive scale; unsupported transforms and geometry outside the fixed bounds fail explicitly. It is deliberately a primitive baker rather than a general mesh voxelizer.

## Approach

- **Field:** union (`min`) of analytical distances to the default cylinder and a finite 10 x 10 plane extruded one metre downwards. This gives the zero-thickness visible plane a defined negative side. Grid origin `(-5.5, -1, -5.5)`, maximum `(5.5, 7, 5.5)`, spacing 0.0625 m, dimensions 177 x 129 x 177: 4,041,441 float32 values, 16,165,764 bytes (15.42 MiB). Samples are at grid vertices, X fastest, then Y, then Z. BinaryWriter writes little-endian floats. Metadata and data survive reimport.
- **Sampling:** eight neighbouring values form one trilinear lookup. Its analytic derivative supplies the contact normal without six extra finite-difference lookups. The maximum faces use the final grid cell with interpolation weight one. Outside the volume returns positive infinity and zero gradient. Escaped particles are recycled into the rain; boundary clamping would invent walls or invalid normals. There is half a metre of lateral margin around the finite ground.
- **Simulation:** persistent arrays for position/radius (`float4`), velocity (`float3`), and rest time (`float`). One Burst `IJobParallelFor`, batches of 128, independent particles, fixed 1/120 s integration. Up to eight catch-up steps; longer stalls drop simulated time. Sleeping particles early-out. The job completes before GPU upload; there is no outstanding handle at disposal or reset.
- **Collision:** gravity, conservative advancement, positional projection, restitution 0.32 for impacts faster than 0.6 m/s, and Coulomb friction 0.65. A grid interpolant of a 1-Lipschitz field can be up to sqrt(3)-Lipschitz, so advance by at most 0.55 times clearance. Near contact, tiny steps (at most 1/16 voxel) permit sliding, followed by projection. Each substep has a 48-iteration cap; any unconsumed movement is discarded, never advanced blindly. A 2 mm skin absorbs interpolation/contact tolerance. Low-speed supported contacts sleep after 0.4 s and remain exactly stationary until reset. The field is static, so waking is unnecessary.
- **Rendering:** exactly one `Graphics.RenderMeshPrimitives` submission for all balls, one forward shader pass, one structured buffer containing only position and radius. 16 bytes/ball = 160,000 bytes uploaded per frame at 10k (9.6 MB/s at 60 fps), rather than 640,000 bytes of matrices. A generated unit sphere has 62 vertices and 120 triangles: 1.2 million triangles at 10k. World-space positions use `SV_InstanceID`; there are no per-instance transform matrices. Ground, cylinder, and overlay are separate draws. Bounds cover the entire volume; there is no per-ball culling.

Added explicit Unity Registry dependencies: **Burst 1.8.28** for native job compilation, **Collections 2.6.2** for native storage, **Mathematics 1.3.3** for vector math. These were already transitive dependencies of the URP template; they are now pinned direct dependencies. Input System 1.19.0 was already in the template and handles shortcuts. No third-party physics code or assets.

Code lives in `Assets/Runtime`, `Assets/Editor`, and `Assets/Shaders`. Serialized private fields use `_camelCase`, other private instance fields use `mCamelCase`, and private static fields use `_sCamelCase`. Public fields/properties use `PascalCase`, public static fields use `sCamelCase`, public methods use `PascalCase`, and private methods use `camelCase`. Enum types use `camelCase` and members use `UPPER_SNAKE_CASE`. Unity callbacks such as `OnEnable`, `Update`, and `OnDisable` retain the names Unity requires.

## Validation and performance

Run **SDF Physics > Validate baked field and simulation**. Failure throws; success writes `Validation/correctness.txt`. It tests inside/outside signs, gradients, inclusive maximum boundary, outside-volume policy, 500 m/s vertical impacts against both surfaces, a 1,000 m/s cylinder side impact, initial penetration recovery, 10,000-ball settling over 20 simulated seconds, and exact sleeping-position stability.

For a clean batch build (close the editor for this project first):

```powershell
& '<Unity installation>/Editor/Unity.exe' -batchmode -nographics -projectPath '<project>' -executeMethod SdfPhysics.Editor.DemoBuilder.BuildSubmission -quit -logFile build.log
```

This recreates the scene, bakes, validates, and builds `Build/SdfPhysics.exe`. Reproduce the standalone benchmark with:

```powershell
& './Build/SdfPhysics.exe' -batchmode -screen-fullscreen 0 -screen-width 1280 -screen-height 720 --sdf-benchmark '<absolute path>/benchmark.jsonl'
```

Each active phase warms up for four seconds then samples ten seconds, resetting the rain every 2.5 seconds to prevent a sleeping-only benchmark. Counts: 10k, 50k, 100k, 250k, 500k. A separate 10k settled phase warms up for twelve seconds and samples ten seconds. Results identify the actual CPU/GPU and resolution and report median, mean, and p95 frame time plus mean job/wait time. A Burst-discard probe verifies native execution rather than just package availability.

New benchmark JSON uses PascalCase field names, such as `Count`, `MedianFrameMs`, and `BurstNative`, to match the public C# fields. The historical results in `Validation/benchmark.jsonl` retain their original camelCase keys; account for that difference when comparing files with a script.

In batch mode the benchmark explicitly renders the camera to a 1280 x 720 render texture and waits for a one-pixel GPU readback each frame. Hidden windows otherwise skip rendering and produce misleadingly fast timings. These measurements include simulation, upload, rendering, and an extra GPU synchronization/readback cost; they exclude the IMGUI overlay and desktop presentation. They are not isolated GPU timings or a claim about normal windowed latency. Omit `-batchmode` to measure an ordinary **visible** window with the overlay instead. The player saves an early screenshot beside the output and exits when finished.

Measured on **AMD Ryzen 5 5600G / NVIDIA GeForce RTX 5060 Ti**, 1280 x 720, Windows x64 release build, native Burst confirmed, on 2026-09-24. Offscreen synchronization method described above. Raw samples' summaries are in `Validation/benchmark.jsonl` and the rendered scene is in `Validation/benchmark.jsonl.png`.

| Moving balls | Median frame | p95 frame | Mean job + wait |
| ---: | ---: | ---: | ---: |
| 10,000 | 0.86 ms | 1.57 ms | 0.02 ms |
| 50,000 | 1.85 ms | 2.76 ms | 0.14 ms |
| 100,000 | 3.02 ms | 4.90 ms | 0.41 ms |
| 250,000 | 8.17 ms | 10.39 ms | 2.03 ms |
| 500,000 | 17.81 ms | 24.79 ms | 6.76 ms |

The separate settled 10k phase measured **0.79 ms median / 1.15 ms p95**, compared with **0.86 / 1.57 ms** during repeated rain. Mean job/wait figures average all rendered frames, including those between fixed simulation ticks.

The first tested count to exceed the 16.67 ms budget is **500,000**. At that count there are 60 million submitted triangles, an 8 MB upload each frame, and several simulation ticks per rendered frame. Both simulation and rendering costs grow substantially; the 6.76 ms job/wait measurement accounts for part of the 18.59 ms mean total. Without a separate GPU profiler capture, attributing the entire remaining time to the GPU would be speculation. The exact crossover between 250k and 500k was not measured.

The latest correctness run (2026-10-01) passed **35,792 assertions**, including scene references and defaults after the naming refactor. Play-mode checks also passed for the optional count limit, allocating 640,000 balls, halving, and resetting; the editor reported native Burst execution. See `Validation/refactor-smoke.txt`. During the original 2026-09-24 run, Windows Smart App Control blocked the editor's generated Burst JIT DLL, so that earlier correctness run used the managed fallback while the release player ran native Burst AOT. `Validation/environment.txt` records that earlier environment. No machine security settings were changed.

## Limitations and another day

The analytic cylinder is circular while Unity's primitive is a polygonal approximation; tiny visible side offsets plus the 2 mm collision skin are expected. Trilinear SDF interpolation also rounds sharp edges. The sign and zero set of the union are correct, but `min` is not an exact Euclidean interior distance in overlapping solids. The conservative bound still applies. Deep embedded points with zero gradient use an upward recovery direction; arbitrary invalid starting positions are outside the demo's spawning contract.

The plane is finite: balls that leave its edge fall and recycle. Balls may overlap each other by design. No geometry motion, ball collision, or compute simulation. Extremely fast motion may consume the iteration budget and slow down instead of advancing unsafely. Sleeping balls still upload and draw, so rendering/overdraw eventually dominates. The overlay rebuilds its string four times a second; the simulation and upload path use persistent storage. Count changes and resets are setup operations with main-thread work.

With another day: capture CPU/GPU profiler traces and the Frame Debugger, add a GPU upload path that skips unchanged sleeping ranges, compare a lower-poly LOD/impostor at large counts, and extend automated contact tests around primitive seams. AI assistance was used to implement and validate this submission; the algorithms and compromises above are intended to be explainable from the code.
