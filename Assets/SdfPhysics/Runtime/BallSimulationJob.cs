using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace SdfPhysics
{
    [BurstCompile]
    public struct BallSimulationJob : IJobParallelFor
    {
        public NativeArray<float4> Positions;
        public NativeArray<float3> Velocities;
        public NativeArray<float> RestTimes;
        [ReadOnly] public SdfGrid Grid;
        public int Steps;
        public float DeltaTime;
        public uint Seed;
        public const float Skin = 0.002f;
        public const float SleepDelay = 0.4f;

        public void Execute(int index)
        {
            float4 ball = Positions[index];
            float3 p = ball.xyz, v = Velocities[index];
            float rest = RestTimes[index];
            if (rest >= SleepDelay) return;
            for (int step = 0; step < Steps; step++)
            {
                if (!Grid.Contains(p) || p.y < -0.6f)
                {
                    var random = Unity.Mathematics.Random.CreateFromIndex((uint)index + Seed + (uint)step);
                    p = new float3(random.NextFloat(-4.4f, 4.4f), random.NextFloat(3, 6.4f), random.NextFloat(-4.4f, 4.4f));
                    v = float3.zero;
                    rest = 0;
                }
                v.y -= 9.81f * DeltaTime;
                float remaining = DeltaTime;
                bool supported = false;
                // A trilinear interpolation of an exact SDF is at most sqrt(3)-Lipschitz.
                // Conservative advancement uses that bound, not an unsafe endpoint-only contact test.
                // If the budget is exhausted, discard remaining motion: never take an unchecked step.
                for (int iteration = 0; iteration < 48 && remaining > 0.000001f; iteration++)
                {
                    if (!Grid.Contains(p)) break;
                    float distance = Grid.Sample(p, out float3 gradient);
                    float gap = distance - ball.w - Skin;
                    if (gap <= 0.0005f)
                    {
                        float3 normal = math.normalizesafe(gradient, new float3(0, 1, 0));
                        p += normal * math.max(0, -gap + 0.0001f);
                        float vn = math.dot(v, normal);
                        if (vn < 0)
                        {
                            float bounce = vn < -0.6f ? 0.32f : 0;
                            float3 tangent = v - vn * normal;
                            // Coulomb friction removes at most mu * normal impulse.
                            float speed = math.length(tangent);
                            tangent *= math.max(0, 1 - 0.65f * (1 + bounce) * -vn / math.max(speed, 0.00001f));
                            v = tangent - bounce * vn * normal;
                        }
                        supported = normal.y > 0.65f;
                        // Leaving a contact needs a tiny bounded step to avoid zero-distance stagnation.
                        // Its length is < 1/16 voxel; the next iteration immediately rechecks it.
                        float dt = math.min(remaining, Grid.CellSize / (16 * math.max(math.length(v), 0.01f)));
                        p += v * dt;
                        remaining -= dt;
                    }
                    else
                    {
                        float speed = math.length(v);
                        float dt = math.min(remaining, gap * 0.55f / math.max(speed, 0.00001f));
                        p += v * dt;
                        remaining -= dt;
                    }
                }
                // Final projection after tangential contact movement (including cylinder corners).
                if (Grid.Contains(p))
                    for (int correction = 0; correction < 8; correction++)
                    {
                        float d = Grid.Sample(p, out float3 grad);
                        if (d >= ball.w + Skin - 0.0001f) break;
                        p += math.normalizesafe(grad, new float3(0, 1, 0)) * (ball.w + Skin - d);
                    }
                rest = supported && math.lengthsq(v) < 0.0025f ? rest + DeltaTime : 0;
                if (rest >= SleepDelay) { v = float3.zero; break; }
            }
            Positions[index] = new float4(p, ball.w);
            Velocities[index] = v;
            RestTimes[index] = rest;
        }
    }
}
