using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SdfPhysics
{
    public sealed class BallDemo : MonoBehaviour
    {
        public SdfVolume volume;
        public Mesh ballMesh;
        public Material ballMaterial;
        [Min(1)] public int ballCount = 10000;
        [Range(0.03f, 0.2f)] public float radius = 0.065f;
        public const float FixedStep = 1f / 120;
        NativeArray<float4> positions;
        NativeArray<float3> velocities;
        NativeArray<float> restTimes;
        SdfGrid grid;
        GraphicsBuffer buffer;
        MaterialPropertyBlock properties;
        JobHandle handle;
        RenderParams renderParams;
        float accumulator, smoothFrameMs, smoothJobMs, hudTimer;
        uint seed = 1;
        string hud;
        bool paused;
        int asleep;
        readonly Stopwatch stopwatch = new Stopwatch();
        readonly List<float> frameSamples = new List<float>(4096);
        readonly List<float> jobSamples = new List<float>(4096);
        readonly int[] benchmarkCounts = { 10000, 50000, 100000, 250000, 500000 };
        bool benchmarking;
        bool captured;
        bool burstExecuted;
        int benchmarkStage;
        float stageStart, lastReset;
        string benchmarkOutput;
        Camera benchmarkCamera;
        RenderTexture benchmarkTarget;
        UniversalRenderPipeline.SingleCameraRequest renderRequest;

        void OnEnable()
        {
            if (volume == null || ballMesh == null || ballMaterial == null || !SystemInfo.supportsInstancing)
            {
                UnityEngine.Debug.LogError("SDF demo requires its baked assets and an instancing-capable GPU.");
                enabled = false;
                return;
            }
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            grid = volume.Load(Allocator.Persistent);
            burstExecuted = BurstExecutionProbe.Run();
            UnityEngine.Debug.Log("SDF native Burst execution: " + burstExecuted);
            properties = new MaterialPropertyBlock();
            renderParams = new RenderParams(ballMaterial)
            {
                worldBounds = new Bounds((grid.Origin + grid.Max) * 0.5f, (Vector3)(grid.Max - grid.Origin) + 2 * Vector3.one),
                matProps = properties, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false
            };
            SetCount(ballCount);
            string[] args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, "--sdf-benchmark");
            if (flag >= 0)
            {
                benchmarking = true;
                benchmarkOutput = flag + 1 < args.Length ? args[flag + 1] : "sdf-benchmark.jsonl";
                File.WriteAllText(benchmarkOutput, "");
                SetCount(benchmarkCounts[0]);
                if (Application.isBatchMode)
                {
                    benchmarkCamera = Camera.main;
                    benchmarkCamera.enabled = false;
                    benchmarkTarget = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32);
                    benchmarkTarget.Create();
                    renderRequest = new UniversalRenderPipeline.SingleCameraRequest { destination = benchmarkTarget };
                }
                stageStart = lastReset = Time.realtimeSinceStartup;
            }
        }

        public void SetCount(int count)
        {
            handle.Complete();
            DisposeBalls();
            ballCount = math.clamp(count, 1, 500000);
            positions = new NativeArray<float4>(ballCount, Allocator.Persistent);
            velocities = new NativeArray<float3>(ballCount, Allocator.Persistent);
            restTimes = new NativeArray<float>(ballCount, Allocator.Persistent);
            buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, ballCount, 16);
            properties.SetBuffer("_Balls", buffer);
            ResetBalls();
        }

        public void ResetBalls()
        {
            handle.Complete();
            var random = Unity.Mathematics.Random.CreateFromIndex(seed++);
            for (int i = 0; i < ballCount; i++)
            {
                // A quarter of the rain targets the cylinder so both contact surfaces stay visible.
                float spread = i % 4 == 0 ? 0.44f : 4.4f;
                positions[i] = new float4(random.NextFloat(-spread, spread), random.NextFloat(2.5f, 6.4f),
                    random.NextFloat(-spread, spread), radius);
                velocities[i] = float3.zero;
                restTimes[i] = 0;
            }
            accumulator = 0;
        }

        void Update()
        {
            if (!benchmarking && Keyboard.current != null)
            {
                if (Keyboard.current.rKey.wasPressedThisFrame) ResetBalls();
                if (Keyboard.current.spaceKey.wasPressedThisFrame) paused = !paused;
            }
            float frameMs = Time.unscaledDeltaTime * 1000;
            smoothFrameMs = math.lerp(smoothFrameMs, frameMs, 0.04f);
            // Bound catch-up after a hitch; excess real time is deliberately dropped, never a huge step.
            accumulator = math.min(accumulator + (paused ? 0 : Time.deltaTime), 8 * FixedStep);
            int steps = (int)(accumulator / FixedStep);
            accumulator -= steps * FixedStep;
            stopwatch.Restart();
            if (steps > 0)
            {
                handle = new BallSimulationJob
                {
                    Positions = positions, Velocities = velocities, RestTimes = restTimes, Grid = grid,
                    DeltaTime = FixedStep, Steps = steps, Seed = seed
                }.Schedule(ballCount, 128);
                handle.Complete();
            }
            float jobMs = (float)stopwatch.Elapsed.TotalMilliseconds;
            smoothJobMs = math.lerp(smoothJobMs, jobMs, 0.04f);
            buffer.SetData(positions);
            Graphics.RenderMeshPrimitives(renderParams, ballMesh, 0, ballCount);
            if (benchmarkTarget != null)
            {
                // Hidden/minimized player windows can skip rendering entirely. Explicitly render and
                // wait for one pixel's GPU readback so batch timings include real completed GPU work.
                RenderPipeline.SubmitRenderRequest(benchmarkCamera, renderRequest);
                var readback = AsyncGPUReadback.Request(benchmarkTarget, 0, 0, 1, 0, 1, 0, 1);
                readback.WaitForCompletion();
                if (readback.hasError) throw new InvalidOperationException("Benchmark GPU readback failed.");
            }
            if ((hudTimer -= Time.unscaledDeltaTime) <= 0)
            {
                hudTimer = 0.25f;
                asleep = 0;
                for (int i = 0; i < ballCount; i++) if (restTimes[i] >= BallSimulationJob.SleepDelay) asleep++;
                hud = $"SDF PHYSICS\n{ballCount:N0} balls  |  {smoothFrameMs:F2} ms  |  {1000 / math.max(smoothFrameMs, 0.001f):F0} fps\n" +
                      $"Jobs + wait: {smoothJobMs:F2} ms  |  asleep: {asleep:N0}\n1 ball draw  |  {ballCount * 16 / 1000f:F0} KB upload/frame\nBurst native: {burstExecuted}  |  R: reset  Space: pause";
            }
            if (benchmarking) Benchmark(frameMs, jobMs);
        }

        [Serializable]
        sealed class Measurement
        {
            public string cpu, gpu, unity, phase, rendering;
            public int count, width, height, samples;
            public bool burstNative;
            public float medianFrameMs, p95FrameMs, meanFrameMs, meanJobMs;
        }

        void Benchmark(float frameMs, float jobMs)
        {
            float now = Time.realtimeSinceStartup, elapsed = now - stageStart;
            if (!captured && elapsed > 1)
            {
                if (benchmarkTarget == null) ScreenCapture.CaptureScreenshot(benchmarkOutput + ".png");
                else
                {
                    var previous = RenderTexture.active;
                    RenderTexture.active = benchmarkTarget;
                    var capture = new Texture2D(benchmarkTarget.width, benchmarkTarget.height, TextureFormat.RGB24, false);
                    capture.ReadPixels(new Rect(0, 0, capture.width, capture.height), 0, 0);
                    capture.Apply();
                    File.WriteAllBytes(benchmarkOutput + ".png", capture.EncodeToPNG());
                    Destroy(capture);
                    RenderTexture.active = previous;
                }
                captured = true;
            }
            bool settled = benchmarkStage == benchmarkCounts.Length;
            if (!settled && now - lastReset > 2.5f) { ResetBalls(); lastReset = now; }
            float warmup = settled ? 12 : 4;
            if (elapsed > warmup) { frameSamples.Add(frameMs); jobSamples.Add(jobMs); }
            if (elapsed < warmup + 10) return;
            frameSamples.Sort();
            float total = 0, jobs = 0;
            foreach (float value in frameSamples) total += value;
            foreach (float value in jobSamples) jobs += value;
            var result = new Measurement
            {
                cpu = SystemInfo.processorType, gpu = SystemInfo.graphicsDeviceName, unity = Application.unityVersion,
                phase = settled ? "settled" : "repeated rain", count = ballCount,
                burstNative = burstExecuted,
                rendering = benchmarkTarget == null ? "windowed" : "offscreen with GPU completion wait (no overlay)",
                width = Screen.width, height = Screen.height, samples = frameSamples.Count,
                medianFrameMs = frameSamples[frameSamples.Count / 2],
                p95FrameMs = frameSamples[(int)((frameSamples.Count - 1) * 0.95f)],
                meanFrameMs = total / frameSamples.Count, meanJobMs = jobs / jobSamples.Count
            };
            File.AppendAllText(benchmarkOutput, JsonUtility.ToJson(result) + Environment.NewLine);
            frameSamples.Clear(); jobSamples.Clear();
            benchmarkStage++;
            if (benchmarkStage > benchmarkCounts.Length) { Application.Quit(); return; }
            SetCount(benchmarkStage == benchmarkCounts.Length ? 10000 : benchmarkCounts[benchmarkStage]);
            stageStart = lastReset = now;
        }

        void OnGUI()
        {
            GUI.Box(new Rect(16, 16, 390, 178), GUIContent.none);
            GUI.Label(new Rect(30, 26, 370, 112), hud ?? "Loading baked SDF...");
            if (GUI.Button(new Rect(30, 151, 105, 28), "Reset rain")) ResetBalls();
            if (GUI.Button(new Rect(144, 151, 112, 28), "10,000 balls")) SetCount(10000);
            if (GUI.Button(new Rect(265, 151, 125, 28), "100,000 balls")) SetCount(100000);
        }

        void DisposeBalls()
        {
            buffer?.Dispose(); buffer = null;
            if (positions.IsCreated) positions.Dispose();
            if (velocities.IsCreated) velocities.Dispose();
            if (restTimes.IsCreated) restTimes.Dispose();
        }

        void OnDisable()
        {
            handle.Complete();
            DisposeBalls();
            if (grid.Values.IsCreated) grid.Values.Dispose();
            if (benchmarkTarget != null)
            {
                benchmarkTarget.Release(); Destroy(benchmarkTarget); benchmarkTarget = null;
                if (benchmarkCamera != null) benchmarkCamera.enabled = true;
            }
        }
    }
}
