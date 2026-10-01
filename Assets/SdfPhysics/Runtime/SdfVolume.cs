using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace SdfPhysics
{
    [CreateAssetMenu(menuName = "SDF Physics/Baked volume")]
    public sealed class SdfVolume : ScriptableObject
    {
        public Vector3 origin;
        public Vector3Int resolution;
        public float cellSize;
        public TextAsset distances;

        public SdfGrid Load(Allocator allocator)
        {
            int count = checked(resolution.x * resolution.y * resolution.z);
            if (cellSize <= 0 || resolution.x < 2 || resolution.y < 2 || resolution.z < 2 ||
                distances == null || distances.bytes.Length != count * sizeof(float))
                throw new InvalidOperationException("Invalid SDF asset. Run SDF Physics > Bake open scene.");
            var values = new float[count];
            Buffer.BlockCopy(distances.bytes, 0, values, 0, count * sizeof(float));
            return new SdfGrid
            {
                Values = new NativeArray<float>(values, allocator), Origin = (float3)origin,
                Size = new int3(resolution.x, resolution.y, resolution.z), CellSize = cellSize
            };
        }
    }

    public struct SdfGrid
    {
        [ReadOnly] public NativeArray<float> Values;
        public float3 Origin;
        public int3 Size;
        public float CellSize;
        public float3 Max => Origin + (float3)(Size - 1) * CellSize;

        public bool Contains(float3 p) => math.all(p >= Origin) && math.all(p <= Max);

        // One trilinear stencil: the gradient is the analytic derivative of those same eight values.
        // Outside is explicitly empty; callers recycle before sampling rather than clamp to a fake wall.
        public float Sample(float3 p, out float3 gradient)
        {
            gradient = float3.zero;
            if (!Contains(p)) return float.PositiveInfinity;
            float3 g = (p - Origin) / CellSize;
            int3 c = math.clamp((int3)math.floor(g), 0, Size - 2);
            float3 t = g - c;
            int i = c.x + Size.x * (c.y + Size.y * c.z);
            int y = Size.x, z = Size.x * Size.y;
            float a = Values[i], b = Values[i + 1], d = Values[i + y], e = Values[i + y + 1];
            float f = Values[i + z], h = Values[i + z + 1], j = Values[i + y + z], k = Values[i + y + z + 1];
            float ab = math.lerp(a, b, t.x), de = math.lerp(d, e, t.x);
            float fh = math.lerp(f, h, t.x), jk = math.lerp(j, k, t.x);
            gradient = new float3(
                math.lerp(math.lerp(b - a, e - d, t.y), math.lerp(h - f, k - j, t.y), t.z),
                math.lerp(de - ab, jk - fh, t.z),
                math.lerp(fh, jk, t.y) - math.lerp(ab, de, t.y)) / CellSize;
            return math.lerp(math.lerp(ab, de, t.y), math.lerp(fh, jk, t.y), t.z);
        }
    }
}
