# SDF Physics

A Unity 6.3 URP demo simulating 10,000 balls against a baked signed distance field. Physics runs on the CPU with Jobs and Burst; all balls render in one instanced draw. No per-ball GameObjects, rigidbodies, or colliders.

## Run

1. Open the project with **Unity 6000.3.11f1**.
2. Open `Assets/Scenes/SdfPhysics.unity` and press Play.
3. Use **R** to reset, **Space** to pause, and **x2** or **/2** to change the ball count.

The demo starts with **10,000 balls**. **Max Ball Count** in the Inspector sets an optional limit; **0** disables the configured cap. Memory and GPU buffer limits still apply.

## Bake and validate

Use **SDF Physics > Bake open scene** after changing the ground or cylinder. The bake writes distances to `Assets/Baked/SceneSdf.bytes` and grid metadata to `SceneSdf.asset`. Both assets are included, so no bake is needed on startup.

Run **SDF Physics > Validate baked field and simulation** to check contacts, high-speed impacts, settling, and scene references.

## Implementation

- Trilinear SDF sampling supplies distance and surface normals.
- Fixed 120 Hz simulation uses conservative advancement, friction, restitution, and sleeping.
- Rendering uploads position and radius only: **160 KB per frame at 10,000 balls**, with a 120-triangle sphere mesh.
- Explicit package dependencies: **Burst**, **Collections**, and **Mathematics**.

## Performance and limits

Recorded on a **Ryzen 5 5600G / RTX 5060 Ti**, at **1280 x 720** on 2026-09-24: **0.86 ms median / 1.57 ms p95** for 10,000 moving balls. At 500,000 balls, the median reached **17.81 ms**. Results are in `Validation/benchmark.jsonl`.

These measurements use offscreen rendering with a GPU completion wait; they exclude the overlay and window presentation.

Geometry is static, balls do not collide with each other, and grid interpolation approximates curved surfaces and corners. Escaped balls respawn. Next improvements would be an SDF debug view and avoiding uploads for unchanged sleeping balls.
