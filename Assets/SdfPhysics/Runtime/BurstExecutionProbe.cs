using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace SdfPhysics
{
    // Report actual native execution, rather than assuming the Burst package being present is enough.
    [BurstCompile(CompileSynchronously = true)]
    public struct BurstExecutionProbe : IJob
    {
        public NativeArray<int> Result;
        public void Execute()
        {
            int native = 1;
            MarkManaged(ref native);
            Result[0] = native;
        }
        [BurstDiscard] static void MarkManaged(ref int native) { native = 0; }
        public static bool Run()
        {
            using var result = new NativeArray<int>(1, Allocator.TempJob);
            new BurstExecutionProbe { Result = result }.Schedule().Complete();
            return result[0] == 1;
        }
    }
}
