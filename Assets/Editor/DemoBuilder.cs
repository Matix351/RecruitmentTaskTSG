using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace SdfPhysics.Editor
{
    public static class DemoBuilder
    {
        public const string ScenePath = "Assets/Scenes/SdfPhysics.unity";

        [MenuItem("SDF Physics/Create demo scene")]
        public static void CreateScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            System.IO.Directory.CreateDirectory(SdfBaker.Folder);
            var ground = createPrimitive("Ground (10 x 10)", PrimitiveType.Plane, Vector3.zero, new Color(0.19f, 0.23f, 0.28f));
            ground.AddComponent<SdfGeometry>().Shape = SdfGeometry.geometryShape.PLANE;
            var cylinder = createPrimitive("Cylinder", PrimitiveType.Cylinder, Vector3.up, new Color(0.72f, 0.40f, 0.18f));
            cylinder.AddComponent<SdfGeometry>().Shape = SdfGeometry.geometryShape.CYLINDER;

            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(11, 10, -13);
            camera.transform.LookAt(new Vector3(0, 1.6f, 0));
            camera.fieldOfView = 48;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 60;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.045f, 0.062f, 0.085f);
            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.5f;
            light.transform.rotation = Quaternion.Euler(45, -35, 0);
            light.shadows = LightShadows.None;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.5f, 0.55f, 0.65f);

            SdfBaker.Bake();
            var demo = new GameObject("SDF Ball Simulation").AddComponent<BallDemo>();
            var volume = AssetDatabase.LoadAssetAtPath<SdfVolume>(SdfBaker.VolumePath);
            var ballMesh = saveAsset(createSphere(), SdfBaker.Folder + "/BallMesh.asset");
            var ballMaterial = saveAsset(new Material(Shader.Find("SDF Physics/Instanced Balls")) { enableInstancing = true },
                SdfBaker.Folder + "/Balls.mat");
            demo.Initialize(volume, ballMesh, ballMaterial);

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
        }

        private static T saveAsset<T>(T value, string path) where T : UnityEngine.Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null)
            {
                EditorUtility.CopySerialized(value, existing);
                UnityEngine.Object.DestroyImmediate(value);
                return existing;
            }

            AssetDatabase.CreateAsset(value, path);
            return value;
        }

        private static GameObject createPrimitive(string name, PrimitiveType type, Vector3 position, Color color)
        {
            var obj = GameObject.CreatePrimitive(type);
            obj.name = name;
            obj.transform.position = position;
            obj.isStatic = true;
            UnityEngine.Object.DestroyImmediate(obj.GetComponent<Collider>());
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetColor("_BaseColor", color);
            obj.GetComponent<Renderer>().sharedMaterial = saveAsset(material, SdfBaker.Folder + "/" + type + ".mat");
            return obj;
        }

        // 62 vertices, 120 triangles. Smooth normals retain round shading at this screen size.
        private static Mesh createSphere()
        {
            const int slices = 12;
            const int rings = 6;
            var vertices = new List<Vector3> { Vector3.up };
            for (int ring = 1; ring < rings; ring++)
            {
                float phi = Mathf.PI * ring / rings;
                for (int slice = 0; slice < slices; slice++)
                {
                    float theta = 2 * Mathf.PI * slice / slices;
                    vertices.Add(new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta)));
                }
            }
            int bottom = vertices.Count;
            vertices.Add(Vector3.down);
            var triangles = new List<int>();
            for (int slice = 0; slice < slices; slice++)
            {
                int next = (slice + 1) % slices;
                triangles.AddRange(new[] { 0, 1 + next, 1 + slice });
                for (int ring = 0; ring < rings - 2; ring++)
                {
                    int a = 1 + ring * slices + slice;
                    int b = 1 + ring * slices + next;
                    int c = a + slices;
                    int d = b + slices;
                    triangles.AddRange(new[] { a, b, c, b, d, c });
                }
                triangles.AddRange(new[] { bottom, bottom - slices + slice, bottom - slices + next });
            }
            var mesh = new Mesh { name = "Unit sphere - 120 triangles" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        public static void BuildSubmission()
        {
            CreateScene();
            SimulationValidation.Run();
            BuildPlayer();
        }

        public static void BuildPlayer()
        {
            PlayerSettings.productName = "SDF Physics";
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.runInBackground = true;
            PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Build/SdfPhysics.exe",
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new Exception("Player build failed.");

            Debug.Log("SDF_SUBMISSION_BUILD_SUCCEEDED");
        }
    }
}
