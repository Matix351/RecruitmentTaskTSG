using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SdfPhysics.Editor
{
    public static class SdfBaker
    {
        public const string Folder = "Assets/Baked";
        public const string VolumePath = Folder + "/SceneSdf.asset";

        // Exact primitive distances in world metres. A finite plane is extruded downwards to give it an inside.
        public static float Distance(Vector3 p, SdfGeometry source)
        {
            Vector3 local = Quaternion.Inverse(source.transform.rotation) * (p - source.transform.position);
            Vector3 scale = source.transform.lossyScale;
            if (source.Shape == SdfGeometry.geometryShape.PLANE)
            {
                Vector3 q = new Vector3(Mathf.Abs(local.x) - 5 * scale.x,
                    Mathf.Abs(local.y + 0.5f) - 0.5f, Mathf.Abs(local.z) - 5 * scale.z);
                return Vector3.Max(q, Vector3.zero).magnitude + Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0);
            }
            float radial = new Vector2(local.x, local.z).magnitude - 0.5f * scale.x;
            float vertical = Mathf.Abs(local.y) - scale.y;
            return new Vector2(Mathf.Max(radial, 0), Mathf.Max(vertical, 0)).magnitude + Mathf.Min(Mathf.Max(radial, vertical), 0);
        }

        [MenuItem("SDF Physics/Bake open scene")]
        public static void Bake()
        {
            var sources = UnityEngine.Object.FindObjectsByType<SdfGeometry>(FindObjectsSortMode.None);
            if (sources.Length == 0)
                throw new InvalidOperationException("No SdfGeometry authoring components in this scene.");

            foreach (var source in sources)
            {
                Vector3 scale = source.transform.lossyScale;
                if (scale.x <= 0 || scale.y <= 0 || scale.z <= 0 ||
                    (source.Shape == SdfGeometry.geometryShape.CYLINDER && Mathf.Abs(scale.x - scale.z) > 0.0001f))
                    throw new InvalidOperationException("Use positive scale and circular (equal X/Z) cylinders.");

                if (source.transform.parent != null)
                    throw new InvalidOperationException("Bake sources must be root objects (no sheared parent transforms).");

                var bounds = source.GetComponent<Renderer>().bounds;
                var bakeBounds = new Bounds(new Vector3(0, 3, 0), new Vector3(11, 8, 11));
                if (!bakeBounds.Contains(bounds.min) || !bakeBounds.Contains(bounds.max))
                    throw new InvalidOperationException("Source geometry extends beyond fixed bake bounds.");
            }

            Directory.CreateDirectory(Folder);
            Vector3 origin = new Vector3(-5.5f, -1, -5.5f);
            var size = new Vector3Int(177, 129, 177);
            const float cell = 0.0625f;
            using (var writer = new BinaryWriter(File.Create(Folder + "/SceneSdf.bytes")))
            {
                for (int z = 0; z < size.z; z++)
                {
                    for (int y = 0; y < size.y; y++)
                    {
                        for (int x = 0; x < size.x; x++)
                        {
                            Vector3 p = origin + new Vector3(x, y, z) * cell;
                            float distance = float.PositiveInfinity;
                            foreach (var source in sources)
                                distance = Mathf.Min(distance, Distance(p, source));

                            writer.Write(distance);
                        }
                    }
                }
            }

            AssetDatabase.Refresh();
            var volume = AssetDatabase.LoadAssetAtPath<SdfVolume>(VolumePath);
            if (volume == null)
            {
                volume = ScriptableObject.CreateInstance<SdfVolume>();
                AssetDatabase.CreateAsset(volume, VolumePath);
            }
            var distances = AssetDatabase.LoadAssetAtPath<TextAsset>(Folder + "/SceneSdf.bytes");
            volume.Initialize(origin, size, cell, distances);
            EditorUtility.SetDirty(volume);
            AssetDatabase.SaveAssets();
            Debug.Log($"Baked {size.x * size.y * size.z:N0} SDF samples into {VolumePath}");
        }
    }
}
