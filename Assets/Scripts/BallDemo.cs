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
using UnityEngine.Serialization;

namespace SdfPhysics
{
    public sealed class BallDemo : MonoBehaviour
    {
        [SerializeField, FormerlySerializedAs("volume")]
        private SdfVolume _volume;

        [SerializeField, FormerlySerializedAs("ballMesh")]
        private Mesh _ballMesh;

        [SerializeField, FormerlySerializedAs("ballMaterial")]
        private Material _ballMaterial;

        [SerializeField, Min(1), FormerlySerializedAs("ballCount")]
        private int _ballCount = 10000;

        [SerializeField, Range(0.03f, 0.2f), FormerlySerializedAs("radius")]
        private float _radius = 0.065f;

        [SerializeField, Min(0), Tooltip("Optional ball limit. Zero leaves the limit up to available memory and GPU buffer capacity.")]
        private int _maxBallCount;

        public const float FixedStep = 1f / 120;

        public SdfVolume Volume => _volume;
        public Mesh BallMesh => _ballMesh;
        public Material BallMaterial => _ballMaterial;
        public int BallCount => _ballCount;
        public float Radius => _radius;
        public int MaxBallCount => _maxBallCount;

        private NativeArray<float4> mPositions;
        private NativeArray<float3> mVelocities;
        private NativeArray<float> mRestTimes;
        private SdfGrid mGrid;
        private GraphicsBuffer mBuffer;
        private MaterialPropertyBlock mProperties;
        private JobHandle mHandle;
        private RenderParams mRenderParams;

        private float mAccumulator;
        private float mSmoothFrameMs;
        private float mSmoothJobMs;
        private float mHudTimer;
        private uint mSeed = 1;
        private string mHud;
        private bool mPaused;
        private bool mBurstExecuted;
        private readonly Stopwatch mStopwatch = new Stopwatch();

        private readonly List<float> mFrameSamples = new List<float>(4096);
        private readonly List<float> mJobSamples = new List<float>(4096);
        private readonly int[] mBenchmarkCounts = { 10000, 50000, 100000, 250000, 500000 };
        private bool mBenchmarking;
        private bool mCaptured;
        private int mBenchmarkStage;
        private float mStageStart;
        private float mLastReset;
        private string mBenchmarkOutput;
        private Camera mBenchmarkCamera;
        private RenderTexture mBenchmarkTarget;
        private UniversalRenderPipeline.SingleCameraRequest mRenderRequest;

        public void Initialize(SdfVolume volume, Mesh ballMesh, Material ballMaterial)
        {
            _volume = volume;
            _ballMesh = ballMesh;
            _ballMaterial = ballMaterial;
        }

        private void OnEnable()
        {
            if (_volume == null || _ballMesh == null || _ballMaterial == null || !SystemInfo.supportsInstancing)
            {
                UnityEngine.Debug.LogError("SDF demo requires its baked assets and an instancing-capable GPU.", this);
                enabled = false;
                return;
            }

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            mGrid = _volume.Load(Allocator.Persistent);
            mBurstExecuted = BurstExecutionProbe.Run();
            UnityEngine.Debug.Log("SDF native Burst execution: " + mBurstExecuted);

            mProperties = new MaterialPropertyBlock();
            mRenderParams = new RenderParams(_ballMaterial)
            {
                worldBounds = new Bounds((mGrid.Origin + mGrid.Max) * 0.5f, (Vector3)(mGrid.Max - mGrid.Origin) + 2 * Vector3.one),
                matProps = mProperties,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false
            };

            try
            {
                SetCount(_ballCount);
                startBenchmarkIfRequested();
            }
            catch
            {
                enabled = false;
                throw;
            }
        }

        public void SetCount(int count)
        {
            // Use wide arithmetic: repeated doubling can otherwise wrap into a negative count.
            long maximum = Math.Min(int.MaxValue, SystemInfo.maxGraphicsBufferSize / 16);
            if (_maxBallCount > 0)
            {
                maximum = Math.Min(maximum, _maxBallCount);
            }

            int nextCount = (int)Math.Max(1, Math.Min(count, maximum));
            if (mPositions.IsCreated && nextCount == mPositions.Length)
            {
                return;
            }

            mHandle.Complete();
            NativeArray<float4> positions = default;
            NativeArray<float3> velocities = default;
            NativeArray<float> restTimes = default;
            GraphicsBuffer buffer = null;

            // Keep the current simulation if a larger allocation fails.
            try
            {
                positions = new NativeArray<float4>(nextCount, Allocator.Persistent);
                velocities = new NativeArray<float3>(nextCount, Allocator.Persistent);
                restTimes = new NativeArray<float>(nextCount, Allocator.Persistent);
                buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, nextCount, 16);
            }
            catch
            {
                buffer?.Dispose();
                if (positions.IsCreated) positions.Dispose();
                if (velocities.IsCreated) velocities.Dispose();
                if (restTimes.IsCreated) restTimes.Dispose();
                throw;
            }

            disposeBalls();
            mPositions = positions;
            mVelocities = velocities;
            mRestTimes = restTimes;
            mBuffer = buffer;
            _ballCount = nextCount;
            mProperties.SetBuffer("_Balls", mBuffer);
            ResetBalls();
        }

        public void ResetBalls()
        {
            mHandle.Complete();
            var random = Unity.Mathematics.Random.CreateFromIndex(mSeed++);
            for (int i = 0; i < _ballCount; i++)
            {
                // Aim part of the rain at the cylinder so its contacts are easy to see.
                float spread = i % 4 == 0 ? 0.44f : 4.4f;
                mPositions[i] = new float4(random.NextFloat(-spread, spread), random.NextFloat(2.5f, 6.4f),
                    random.NextFloat(-spread, spread), _radius);
                mVelocities[i] = float3.zero;
                mRestTimes[i] = 0;
            }

            mAccumulator = 0;
            mHudTimer = 0;
        }

        private void Update()
        {
            if (!mBenchmarking && Keyboard.current != null)
            {
                if (Keyboard.current.rKey.wasPressedThisFrame) ResetBalls();
                if (Keyboard.current.spaceKey.wasPressedThisFrame) mPaused = !mPaused;
            }

            float frameMs = Time.unscaledDeltaTime * 1000;
            mSmoothFrameMs = math.lerp(mSmoothFrameMs, frameMs, 0.04f);
            float jobMs = simulate();
            mSmoothJobMs = math.lerp(mSmoothJobMs, jobMs, 0.04f);

            mBuffer.SetData(mPositions);
            Graphics.RenderMeshPrimitives(mRenderParams, _ballMesh, 0, _ballCount);
            renderBenchmarkFrame();
            updateHud();

            if (mBenchmarking)
            {
                benchmark(frameMs, jobMs);
            }
        }

        private float simulate()
        {
            // Drop excess catch-up time after a hitch instead of taking a large physics step.
            mAccumulator = math.min(mAccumulator + (mPaused ? 0 : Time.deltaTime), 8 * FixedStep);
            int steps = (int)(mAccumulator / FixedStep);
            mAccumulator -= steps * FixedStep;

            mStopwatch.Restart();
            if (steps > 0)
            {
                mHandle = new BallSimulationJob
                {
                    Positions = mPositions,
                    Velocities = mVelocities,
                    RestTimes = mRestTimes,
                    Grid = mGrid,
                    DeltaTime = FixedStep,
                    Steps = steps,
                    Seed = mSeed
                }.Schedule(_ballCount, 128);
                mHandle.Complete();
            }

            return (float)mStopwatch.Elapsed.TotalMilliseconds;
        }

        private void updateHud()
        {
            mHudTimer -= Time.unscaledDeltaTime;
            if (mHudTimer > 0)
            {
                return;
            }

            mHudTimer = 0.25f;
            int asleep = 0;
            for (int i = 0; i < _ballCount; i++)
            {
                if (mRestTimes[i] >= BallSimulationJob.SleepDelay) asleep++;
            }

            mHud = $"SDF PHYSICS\n{_ballCount:N0} balls  |  {mSmoothFrameMs:F2} ms  |  {1000 / math.max(mSmoothFrameMs, 0.001f):F0} fps\n" +
                   $"Jobs + wait: {mSmoothJobMs:F2} ms  |  asleep: {asleep:N0}\n" +
                   $"1 ball draw  |  {_ballCount * 16L / 1000f:F0} KB upload/frame\n" +
                   $"Burst native: {mBurstExecuted}  |  R: reset  Space: pause";
        }

        private void OnGUI()
        {
            GUI.Box(new Rect(16, 16, 390, 178), GUIContent.none);
            GUI.Label(new Rect(30, 26, 370, 112), mHud ?? "Loading baked SDF...");
            if (GUI.Button(new Rect(30, 151, 105, 28), "Reset rain")) ResetBalls();
            if (GUI.Button(new Rect(144, 151, 112, 28), "x2")) SetCount((int)Math.Min((long)_ballCount * 2, int.MaxValue));
            if (GUI.Button(new Rect(265, 151, 125, 28), "/2")) SetCount(_ballCount / 2);
        }

        private void startBenchmarkIfRequested()
        {
            string[] args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, "--sdf-benchmark");
            if (flag < 0)
            {
                return;
            }

            mBenchmarking = true;
            mBenchmarkOutput = flag + 1 < args.Length ? args[flag + 1] : "sdf-benchmark.jsonl";
            File.WriteAllText(mBenchmarkOutput, "");
            SetCount(mBenchmarkCounts[0]);

            if (Application.isBatchMode)
            {
                mBenchmarkCamera = Camera.main;
                mBenchmarkCamera.enabled = false;
                mBenchmarkTarget = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32);
                mBenchmarkTarget.Create();
                mRenderRequest = new UniversalRenderPipeline.SingleCameraRequest { destination = mBenchmarkTarget };
            }

            mStageStart = mLastReset = Time.realtimeSinceStartup;
        }

        private void renderBenchmarkFrame()
        {
            if (mBenchmarkTarget == null)
            {
                return;
            }

            // Hidden windows can skip rendering. The readback waits for actual GPU work to finish.
            RenderPipeline.SubmitRenderRequest(mBenchmarkCamera, mRenderRequest);
            var readback = AsyncGPUReadback.Request(mBenchmarkTarget, 0, 0, 1, 0, 1, 0, 1);
            readback.WaitForCompletion();
            if (readback.hasError)
            {
                throw new InvalidOperationException("Benchmark GPU readback failed.");
            }
        }

        private void captureBenchmarkFrame()
        {
            if (mBenchmarkTarget == null)
            {
                ScreenCapture.CaptureScreenshot(mBenchmarkOutput + ".png");
                return;
            }

            RenderTexture previous = RenderTexture.active;
            var capture = new Texture2D(mBenchmarkTarget.width, mBenchmarkTarget.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = mBenchmarkTarget;
                capture.ReadPixels(new Rect(0, 0, capture.width, capture.height), 0, 0);
                capture.Apply();
                File.WriteAllBytes(mBenchmarkOutput + ".png", capture.EncodeToPNG());
            }
            finally
            {
                Destroy(capture);
                RenderTexture.active = previous;
            }
        }

        private void benchmark(float frameMs, float jobMs)
        {
            float now = Time.realtimeSinceStartup;
            float elapsed = now - mStageStart;
            if (!mCaptured && elapsed > 1)
            {
                captureBenchmarkFrame();
                mCaptured = true;
            }

            bool settled = mBenchmarkStage == mBenchmarkCounts.Length;
            if (!settled && now - mLastReset > 2.5f)
            {
                ResetBalls();
                mLastReset = now;
            }

            float warmup = settled ? 12 : 4;
            if (elapsed > warmup)
            {
                mFrameSamples.Add(frameMs);
                mJobSamples.Add(jobMs);
            }

            if (elapsed < warmup + 10)
            {
                return;
            }

            mFrameSamples.Sort();
            float total = 0;
            float jobs = 0;
            foreach (float value in mFrameSamples) total += value;
            foreach (float value in mJobSamples) jobs += value;

            var result = new Measurement
            {
                Cpu = SystemInfo.processorType,
                Gpu = SystemInfo.graphicsDeviceName,
                Unity = Application.unityVersion,
                Phase = settled ? "settled" : "repeated rain",
                Count = _ballCount,
                BurstNative = mBurstExecuted,
                Rendering = mBenchmarkTarget == null ? "windowed" : "offscreen with GPU completion wait (no overlay)",
                Width = Screen.width,
                Height = Screen.height,
                Samples = mFrameSamples.Count,
                MedianFrameMs = mFrameSamples[mFrameSamples.Count / 2],
                P95FrameMs = mFrameSamples[(int)((mFrameSamples.Count - 1) * 0.95f)],
                MeanFrameMs = total / mFrameSamples.Count,
                MeanJobMs = jobs / mJobSamples.Count
            };
            File.AppendAllText(mBenchmarkOutput, JsonUtility.ToJson(result) + Environment.NewLine);
            mFrameSamples.Clear();
            mJobSamples.Clear();
            mBenchmarkStage++;

            if (mBenchmarkStage > mBenchmarkCounts.Length)
            {
                Application.Quit();
                return;
            }

            SetCount(mBenchmarkStage == mBenchmarkCounts.Length ? 10000 : mBenchmarkCounts[mBenchmarkStage]);
            mStageStart = mLastReset = now;
        }

        private void disposeBalls()
        {
            mBuffer?.Dispose();
            mBuffer = null;
            if (mPositions.IsCreated) mPositions.Dispose();
            if (mVelocities.IsCreated) mVelocities.Dispose();
            if (mRestTimes.IsCreated) mRestTimes.Dispose();
        }

        private void OnDisable()
        {
            mHandle.Complete();
            disposeBalls();
            if (mGrid.Values.IsCreated) mGrid.Values.Dispose();
            if (mBenchmarkTarget == null) return;

            mBenchmarkTarget.Release();
            Destroy(mBenchmarkTarget);
            mBenchmarkTarget = null;
            if (mBenchmarkCamera != null) mBenchmarkCamera.enabled = true;
        }

        [Serializable]
        private sealed class Measurement
        {
            public string Cpu;
            public string Gpu;
            public string Unity;
            public string Phase;
            public string Rendering;
            public int Count;
            public int Width;
            public int Height;
            public int Samples;
            public bool BurstNative;
            public float MedianFrameMs;
            public float P95FrameMs;
            public float MeanFrameMs;
            public float MeanJobMs;
        }
    }
}
