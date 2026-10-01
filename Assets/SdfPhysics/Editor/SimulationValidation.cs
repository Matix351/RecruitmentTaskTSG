using System;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace SdfPhysics.Editor
{
    public static class SimulationValidation
    {
        static int checks;
        static void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new Exception("SDF validation failed: " + message);
        }

        [MenuItem("SDF Physics/Validate baked field and simulation")]
        public static void Run()
        {
            checks = 0;
            var asset = AssetDatabase.LoadAssetAtPath<SdfVolume>(SdfBaker.VolumePath);
            var grid = asset.Load(Allocator.Persistent);
            try
            {
                Require(grid.Sample(new float3(0, 1, 0), out _) < -0.49f, "cylinder inside sign");
                Require(grid.Sample(new float3(2, -0.25f, 2), out _) < -0.24f, "ground inside sign");
                Require(math.abs(grid.Sample(new float3(2, 0, 2), out var n)) < 0.00001f && n.y > 0.99f, "plane surface and gradient");
                Require(float.IsPositiveInfinity(grid.Sample(grid.Max + 1, out n)) && math.all(n == 0), "outside policy");
                Require(math.isfinite(grid.Sample(grid.Max, out n)) && math.all(math.isfinite(n)), "inclusive maximum face");
                CheckTrajectory(grid, new float3(2, 3, 2), new float3(0, -500, 0), 0.067f, "fast floor hit");
                CheckTrajectory(grid, new float3(0, 5, 0), new float3(0, -500, 0), 2.067f, "fast cylinder cap hit");
                CheckTrajectory(grid, new float3(-4, 1, 0), new float3(1000, 0, 0), float.NaN, "fast cylinder side hit");
                CheckTrajectory(grid, new float3(2, 0.025f, 2), float3.zero, 0.067f, "initial penetration recovery");
                ValidateRain(grid);
                string result = $"PASS: {checks:N0} assertions. Signs, boundary, 500 m/s vertical and 1000 m/s lateral impacts, initial penetration, 10,000-ball settling and sleeping stability.";
                Directory.CreateDirectory("Validation");
                File.WriteAllText("Validation/correctness.txt", result);
                Debug.Log(result);
            }
            finally { grid.Values.Dispose(); }
        }

        static void CheckTrajectory(SdfGrid grid, float3 p, float3 v, float expectedHeight, string label)
        {
            using var positions = new NativeArray<float4>(1, Allocator.TempJob);
            using var velocities = new NativeArray<float3>(1, Allocator.TempJob);
            using var rest = new NativeArray<float>(1, Allocator.TempJob);
            var writablePositions = positions;
            var writableVelocities = velocities;
            writablePositions[0] = new float4(p, 0.065f); writableVelocities[0] = v;
            var job = new BallSimulationJob { Grid = grid, Positions = positions, Velocities = velocities, RestTimes = rest,
                DeltaTime = BallDemo.FixedStep, Steps = 1, Seed = 1 };
            job.Schedule(1, 1).Complete();
            Require(!grid.Contains(positions[0].xyz) || grid.Sample(positions[0].xyz, out _) >= 0.0645f, label + " first impact penetration");
            if (v.y < -100) Require(velocities[0].y >= 0 && positions[0].y >= expectedHeight - 0.003f, label + " tunneled through surface");
            if (v.x > 100) Require(positions[0].x < -0.5f && velocities[0].x < 0, label + " crossed cylinder");
            // Extreme restitution can launch a ball beyond the finite volume and intentionally recycle it.
            // Test eventual resting height independently with a normal zero-velocity drop at the same point.
            writablePositions[0] = new float4(p, 0.065f);
            writableVelocities[0] = float3.zero;
            for (int step = 0; step < 1440; step++)
            {
                job.Schedule(1, 1).Complete();
                if (grid.Contains(positions[0].xyz))
                    Require(grid.Sample(positions[0].xyz, out _) >= 0.0645f, label + " penetration at " + step);
            }
            if (math.isfinite(expectedHeight)) Require(math.abs(positions[0].y - expectedHeight) < 0.003f, label + " rest height");
            Require(rest[0] >= BallSimulationJob.SleepDelay, label + " sleeps");
        }

        static void ValidateRain(SdfGrid grid)
        {
            const int count = 10000;
            using var positions = new NativeArray<float4>(count, Allocator.Persistent);
            using var velocities = new NativeArray<float3>(count, Allocator.Persistent);
            using var rest = new NativeArray<float>(count, Allocator.Persistent);
            var random = Unity.Mathematics.Random.CreateFromIndex(42);
            var writablePositions = positions;
            for (int i = 0; i < count; i++)
                writablePositions[i] = new float4(random.NextFloat(-4.4f, 4.4f), random.NextFloat(2.5f, 6.4f), random.NextFloat(-4.4f, 4.4f), 0.065f);
            var job = new BallSimulationJob { Grid = grid, Positions = positions, Velocities = velocities, RestTimes = rest,
                DeltaTime = BallDemo.FixedStep, Steps = 8, Seed = 5 };
            for (int i = 0; i < 300; i++) job.Schedule(count, 128).Complete();
            int sleeping = 0;
            for (int i = 0; i < count; i++)
            {
                Require(math.all(math.isfinite(positions[i])), "finite rain position");
                Require(grid.Sample(positions[i].xyz, out _) >= 0.0645f, "rain penetration");
                if (rest[i] >= BallSimulationJob.SleepDelay) sleeping++;
            }
            Require(sleeping >= 9900, "at least 99% sleep after 20 simulated seconds; got " + sleeping);
            using var snapshot = new NativeArray<float4>(positions, Allocator.TempJob);
            job.Schedule(count, 128).Complete();
            for (int i = 0; i < count; i++)
                if (rest[i] >= BallSimulationJob.SleepDelay) Require(math.all(snapshot[i] == positions[i]), "sleeping particle jitter");
        }
    }
}
