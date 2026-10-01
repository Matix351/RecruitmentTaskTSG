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
            float3 position = ball.xyz;
            float3 velocity = Velocities[index];
            float restTime = RestTimes[index];
            if (restTime >= SleepDelay)
            {
                return;
            }

            for (int step = 0; step < Steps; step++)
            {
                if (!Grid.Contains(position) || position.y < -0.6f)
                {
                    var random = Unity.Mathematics.Random.CreateFromIndex((uint)index + Seed + (uint)step);
                    position = new float3(
                        random.NextFloat(-4.4f, 4.4f),
                        random.NextFloat(3, 6.4f),
                        random.NextFloat(-4.4f, 4.4f));
                    velocity = float3.zero;
                    restTime = 0;
                }

                velocity.y -= 9.81f * DeltaTime;
                float remainingTime = DeltaTime;
                bool supported = false;

                // Trilinear interpolation of an exact SDF is at most sqrt(3)-Lipschitz.
                // The 0.55 factor stays below 1/sqrt(3). If we run out of iterations,
                // discard the remaining motion to avoid crossing an unchecked surface.
                for (int iteration = 0; iteration < 48 && remainingTime > 0.000001f; iteration++)
                {
                    if (!Grid.Contains(position))
                    {
                        break;
                    }

                    float distance = Grid.Sample(position, out float3 gradient);
                    float gap = distance - ball.w - Skin;
                    if (gap <= 0.0005f)
                    {
                        float3 normal = math.normalizesafe(gradient, new float3(0, 1, 0));
                        position += normal * math.max(0, -gap + 0.0001f);
                        float normalSpeed = math.dot(velocity, normal);
                        if (normalSpeed < 0)
                        {
                            float bounce = normalSpeed < -0.6f ? 0.32f : 0;
                            float3 tangent = velocity - normalSpeed * normal;

                            // Coulomb friction removes at most mu * normal impulse.
                            float tangentSpeed = math.length(tangent);
                            tangent *= math.max(
                                0, 1 - 0.65f * (1 + bounce) * -normalSpeed / math.max(tangentSpeed, 0.00001f));
                            velocity = tangent - bounce * normalSpeed * normal;
                        }

                        supported = normal.y > 0.65f;

                        // Move at most 1/16 voxel along a contact, then check the surface again.
                        float stepTime = math.min(
                            remainingTime, Grid.CellSize / (16 * math.max(math.length(velocity), 0.01f)));
                        position += velocity * stepTime;
                        remainingTime -= stepTime;
                    }
                    else
                    {
                        float speed = math.length(velocity);
                        float stepTime = math.min(remainingTime, gap * 0.55f / math.max(speed, 0.00001f));
                        position += velocity * stepTime;
                        remainingTime -= stepTime;
                    }
                }

                // Correct any penetration left by movement along a contact or around a corner.
                if (Grid.Contains(position))
                {
                    for (int correction = 0; correction < 8; correction++)
                    {
                        float distance = Grid.Sample(position, out float3 gradient);
                        if (distance >= ball.w + Skin - 0.0001f)
                        {
                            break;
                        }

                        position += math.normalizesafe(gradient, new float3(0, 1, 0)) * (ball.w + Skin - distance);
                    }
                }

                restTime = supported && math.lengthsq(velocity) < 0.0025f ? restTime + DeltaTime : 0;
                if (restTime >= SleepDelay)
                {
                    velocity = float3.zero;
                    break;
                }
            }

            Positions[index] = new float4(position, ball.w);
            Velocities[index] = velocity;
            RestTimes[index] = restTime;
        }
    }
}
