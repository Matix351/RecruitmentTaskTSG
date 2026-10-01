using System;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SdfPhysics.Editor
{
    public static class SimulationValidation
    {
        private static int _sChecks;

        private static void require(bool condition, string message)
        {
            _sChecks++;
            if (!condition)
                throw new Exception("SDF validation failed: " + message);
        }

        [MenuItem("SDF Physics/Validate baked field and simulation")]
        public static void Run()
        {
            _sChecks = 0;
            var asset = AssetDatabase.LoadAssetAtPath<SdfVolume>(SdfBaker.VolumePath);
            require(asset != null, "baked volume asset exists");
            require(asset.Distances != null, "baked distance data reference");
            validateScene(asset);

            var grid = asset.Load(Allocator.Persistent);
            try
            {
                require(grid.Sample(new float3(0, 1, 0), out _) < -0.49f, "cylinder inside sign");
                require(grid.Sample(new float3(2, -0.25f, 2), out _) < -0.24f, "ground inside sign");
                require(math.abs(grid.Sample(new float3(2, 0, 2), out var normal)) < 0.00001f && normal.y > 0.99f,
                    "plane surface and gradient");
                require(float.IsPositiveInfinity(grid.Sample(grid.Max + 1, out normal)) && math.all(normal == 0), "outside policy");
                require(math.isfinite(grid.Sample(grid.Max, out normal)) && math.all(math.isfinite(normal)), "inclusive maximum face");

                checkTrajectory(grid, new float3(2, 3, 2), new float3(0, -500, 0), 0.067f, "fast floor hit");
                checkTrajectory(grid, new float3(0, 5, 0), new float3(0, -500, 0), 2.067f, "fast cylinder cap hit");
                checkTrajectory(grid, new float3(-4, 1, 0), new float3(1000, 0, 0), float.NaN, "fast cylinder side hit");
                checkTrajectory(grid, new float3(2, 0.025f, 2), float3.zero, 0.067f, "initial penetration recovery");
                validateRain(grid);

                string result = $"PASS: {_sChecks:N0} assertions. Scene references and defaults, signs, boundary, " +
                    "500 m/s vertical and 1000 m/s lateral impacts, initial penetration, " +
                    "10,000-ball settling and sleeping stability.";
                Directory.CreateDirectory("Validation");
                File.WriteAllText("Validation/correctness.txt", result);
                Debug.Log(result);
            }
            finally
            {
                grid.Values.Dispose();
            }
        }

        private static void validateScene(SdfVolume expectedVolume)
        {
            var scene = SceneManager.GetSceneByPath(DemoBuilder.ScenePath);
            bool openedForValidation = !scene.isLoaded;
            if (openedForValidation)
                scene = EditorSceneManager.OpenScene(DemoBuilder.ScenePath, OpenSceneMode.Additive);

            try
            {
                int demoCount = 0;
                int planeCount = 0;
                int cylinderCount = 0;
                int colliderCount = 0;
                int rigidbodyCount = 0;
                BallDemo demo = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var demos = root.GetComponentsInChildren<BallDemo>(true);
                    demoCount += demos.Length;
                    if (demos.Length > 0)
                        demo = demos[0];

                    foreach (var geometry in root.GetComponentsInChildren<SdfGeometry>(true))
                    {
                        if (geometry.Shape == SdfGeometry.geometryShape.PLANE)
                            planeCount++;
                        else if (geometry.Shape == SdfGeometry.geometryShape.CYLINDER)
                            cylinderCount++;
                    }

                    colliderCount += root.GetComponentsInChildren<Collider>(true).Length;
                    rigidbodyCount += root.GetComponentsInChildren<Rigidbody>(true).Length;
                }

                require(demoCount == 1, "scene contains one ball simulation");
                require(demo.Volume == expectedVolume, "scene retains the baked volume reference");
                require(demo.BallMesh == AssetDatabase.LoadAssetAtPath<Mesh>(SdfBaker.Folder + "/BallMesh.asset") && demo.BallMesh != null,
                    "scene retains the ball mesh reference");
                require(demo.BallMaterial == AssetDatabase.LoadAssetAtPath<Material>(SdfBaker.Folder + "/Balls.mat") && demo.BallMaterial != null,
                    "scene retains the ball material reference");
                require(demo.BallMaterial.enableInstancing, "ball material supports instancing");
                require(demo.BallCount == 10000, "demo starts with 10,000 balls");
                require(demo.MaxBallCount >= 0 && (demo.MaxBallCount == 0 || demo.MaxBallCount >= demo.BallCount),
                    "configured count limit allows the starting count; zero disables the limit");
                require(demo.Radius > 0 && math.isfinite(demo.Radius), "positive finite ball radius");
                require(planeCount == 1 && cylinderCount == 1, "plane and cylinder retain their baked shape settings");
                require(colliderCount == 0 && rigidbodyCount == 0, "demo scene does not use Unity physics components");
            }
            finally
            {
                if (openedForValidation)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void checkTrajectory(SdfGrid grid, float3 position, float3 velocity, float expectedHeight, string label)
        {
            using var positions = new NativeArray<float4>(1, Allocator.TempJob);
            using var velocities = new NativeArray<float3>(1, Allocator.TempJob);
            using var rest = new NativeArray<float>(1, Allocator.TempJob);
            var writablePositions = positions;
            var writableVelocities = velocities;
            writablePositions[0] = new float4(position, 0.065f);
            writableVelocities[0] = velocity;
            var job = new BallSimulationJob
            {
                Grid = grid,
                Positions = positions,
                Velocities = velocities,
                RestTimes = rest,
                DeltaTime = BallDemo.FixedStep,
                Steps = 1,
                Seed = 1
            };
            job.Schedule(1, 1).Complete();
            require(!grid.Contains(positions[0].xyz) || grid.Sample(positions[0].xyz, out _) >= 0.0645f, label + " first impact penetration");
            if (velocity.y < -100)
                require(velocities[0].y >= 0 && positions[0].y >= expectedHeight - 0.003f, label + " tunneled through surface");
            if (velocity.x > 100)
                require(positions[0].x < -0.5f && velocities[0].x < 0, label + " crossed cylinder");

            // Extreme restitution can launch a ball beyond the finite volume and intentionally recycle it.
            // Test eventual resting height independently with a normal zero-velocity drop at the same point.
            writablePositions[0] = new float4(position, 0.065f);
            writableVelocities[0] = float3.zero;
            for (int step = 0; step < 1440; step++)
            {
                job.Schedule(1, 1).Complete();
                if (grid.Contains(positions[0].xyz))
                    require(grid.Sample(positions[0].xyz, out _) >= 0.0645f, label + " penetration at " + step);
            }

            if (math.isfinite(expectedHeight))
                require(math.abs(positions[0].y - expectedHeight) < 0.003f, label + " rest height");
            require(rest[0] >= BallSimulationJob.SleepDelay, label + " sleeps");
        }

        private static void validateRain(SdfGrid grid)
        {
            const int count = 10000;
            using var positions = new NativeArray<float4>(count, Allocator.Persistent);
            using var velocities = new NativeArray<float3>(count, Allocator.Persistent);
            using var rest = new NativeArray<float>(count, Allocator.Persistent);
            var random = Unity.Mathematics.Random.CreateFromIndex(42);
            var writablePositions = positions;
            for (int i = 0; i < count; i++)
            {
                writablePositions[i] = new float4(random.NextFloat(-4.4f, 4.4f), random.NextFloat(2.5f, 6.4f),
                    random.NextFloat(-4.4f, 4.4f), 0.065f);
            }

            var job = new BallSimulationJob
            {
                Grid = grid,
                Positions = positions,
                Velocities = velocities,
                RestTimes = rest,
                DeltaTime = BallDemo.FixedStep,
                Steps = 8,
                Seed = 5
            };
            for (int i = 0; i < 300; i++)
                job.Schedule(count, 128).Complete();

            int sleeping = 0;
            for (int i = 0; i < count; i++)
            {
                require(math.all(math.isfinite(positions[i])), "finite rain position");
                require(grid.Sample(positions[i].xyz, out _) >= 0.0645f, "rain penetration");
                if (rest[i] >= BallSimulationJob.SleepDelay)
                    sleeping++;
            }

            require(sleeping >= 9900, "at least 99% sleep after 20 simulated seconds; got " + sleeping);
            using var snapshot = new NativeArray<float4>(positions, Allocator.TempJob);
            job.Schedule(count, 128).Complete();
            for (int i = 0; i < count; i++)
            {
                if (rest[i] >= BallSimulationJob.SleepDelay)
                    require(math.all(snapshot[i] == positions[i]), "sleeping particle jitter");
            }
        }
    }
}
