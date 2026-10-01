using UnityEngine;

namespace SdfPhysics
{
    // Explicit authoring tags keep the baker independent of names and renderer material choices.
    public sealed class SdfGeometry : MonoBehaviour
    {
        public enum Shape { Plane, Cylinder }
        public Shape shape;
    }
}
