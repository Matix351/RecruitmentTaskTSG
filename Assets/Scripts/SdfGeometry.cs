using UnityEngine;
using UnityEngine.Serialization;

namespace SdfPhysics
{
    // Marks scene objects for the SDF baker.
    public sealed class SdfGeometry : MonoBehaviour
    {
        public enum geometryShape
        {
            PLANE,
            CYLINDER
        }

        [FormerlySerializedAs("shape")]
        [SerializeField] private geometryShape _shape;

        public geometryShape Shape
        {
            get => _shape;
            set => _shape = value;
        }
    }
}
