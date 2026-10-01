using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace SdfPhysics
{
    [CreateAssetMenu(menuName = "SDF Physics/Baked volume")]
    public sealed class SdfVolume : ScriptableObject
    {
        [FormerlySerializedAs("origin")]
        [SerializeField] private Vector3 _origin;

        [FormerlySerializedAs("resolution")]
        [SerializeField] private Vector3Int _resolution;

        [FormerlySerializedAs("cellSize")]
        [SerializeField] private float _cellSize;

        [FormerlySerializedAs("distances")]
        [SerializeField] private TextAsset _distances;

        public Vector3 Origin => _origin;
        public Vector3Int Resolution => _resolution;
        public float CellSize => _cellSize;
        public TextAsset Distances => _distances;

        public void Initialize(Vector3 origin, Vector3Int resolution, float cellSize, TextAsset distances)
        {
            _origin = origin;
            _resolution = resolution;
            _cellSize = cellSize;
            _distances = distances;
        }

        public SdfGrid Load(Allocator allocator)
        {
            int count = checked(_resolution.x * _resolution.y * _resolution.z);
            if (_cellSize <= 0 || _resolution.x < 2 || _resolution.y < 2 || _resolution.z < 2 ||
                _distances == null || _distances.bytes.Length != count * sizeof(float))
            {
                throw new InvalidOperationException("Invalid SDF asset. Run SDF Physics > Bake open scene.");
            }

            var values = new float[count];
            Buffer.BlockCopy(_distances.bytes, 0, values, 0, count * sizeof(float));
            return new SdfGrid
            {
                Values = new NativeArray<float>(values, allocator),
                Origin = (float3)_origin,
                Size = new int3(_resolution.x, _resolution.y, _resolution.z),
                CellSize = _cellSize
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

        public bool Contains(float3 position)
        {
            return math.all(position >= Origin) && math.all(position <= Max);
        }

        // Differentiate the same eight samples used for trilinear interpolation.
        public float Sample(float3 position, out float3 gradient)
        {
            gradient = float3.zero;
            // The volume has no boundary wall; the simulation respawns balls that leave it.
            if (!Contains(position))
            {
                return float.PositiveInfinity;
            }

            float3 gridPosition = (position - Origin) / CellSize;
            int3 cell = math.clamp((int3)math.floor(gridPosition), 0, Size - 2);
            float3 fraction = gridPosition - cell;
            int index = cell.x + Size.x * (cell.y + Size.y * cell.z);
            int rowStride = Size.x;
            int sliceStride = Size.x * Size.y;

            // The suffix gives each corner's x, y and z offset from the cell origin.
            float value000 = Values[index];
            float value100 = Values[index + 1];
            float value010 = Values[index + rowStride];
            float value110 = Values[index + rowStride + 1];
            float value001 = Values[index + sliceStride];
            float value101 = Values[index + sliceStride + 1];
            float value011 = Values[index + rowStride + sliceStride];
            float value111 = Values[index + rowStride + sliceStride + 1];

            float lowerFront = math.lerp(value000, value100, fraction.x);
            float upperFront = math.lerp(value010, value110, fraction.x);
            float lowerBack = math.lerp(value001, value101, fraction.x);
            float upperBack = math.lerp(value011, value111, fraction.x);

            gradient = new float3(
                math.lerp(
                    math.lerp(value100 - value000, value110 - value010, fraction.y),
                    math.lerp(value101 - value001, value111 - value011, fraction.y),
                    fraction.z),
                math.lerp(upperFront - lowerFront, upperBack - lowerBack, fraction.z),
                math.lerp(lowerBack, upperBack, fraction.y) - math.lerp(lowerFront, upperFront, fraction.y)) / CellSize;

            return math.lerp(
                math.lerp(lowerFront, upperFront, fraction.y),
                math.lerp(lowerBack, upperBack, fraction.y),
                fraction.z);
        }
    }
}
