using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace SdfPhysics
{
    // Burst removes markManaged, so the result tells us whether this job ran as native code.
    [BurstCompile(CompileSynchronously = true)]
    public struct BurstExecutionProbe : IJob
    {
        public NativeArray<int> Result;

        public void Execute()
        {
            int native = 1;
            markManaged(ref native);
            Result[0] = native;
        }

        [BurstDiscard]
        private static void markManaged(ref int native)
        {
            native = 0;
        }

        public static bool Run()
        {
            using var result = new NativeArray<int>(1, Allocator.TempJob);
            new BurstExecutionProbe { Result = result }.Schedule().Complete();
            return result[0] == 1;
        }
    }
}
