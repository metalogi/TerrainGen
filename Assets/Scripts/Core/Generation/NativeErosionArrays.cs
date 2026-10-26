using Unity.Collections;
using Unity.Mathematics;

namespace Sonoma.Core.Generation
{
    // NativeArrays as the erosion code's arrays (IErosionArray, IMacroPairs): one set for every
    // erosion job and for MacroMaps.Build's managed steps on the main thread. A job's read-only
    // arrays are never written through these; the code they wrap writes only its own.

    public struct NativeInts : IErosionArray<int>
    {
        NativeArray<int> _a;
        public NativeInts(NativeArray<int> a) { _a = a; }
        public int  Get(int index)            => _a[index];
        public void Set(int index, int value) => _a[index] = value;
    }

    public struct NativeFloats : IErosionArray<float>
    {
        NativeArray<float> _a;
        public NativeFloats(NativeArray<float> a) { _a = a; }
        public float Get(int index)              => _a[index];
        public void  Set(int index, float value) => _a[index] = value;
    }

    public struct NativeDoubles : IErosionArray<double>
    {
        NativeArray<double> _a;
        public NativeDoubles(NativeArray<double> a) { _a = a; }
        public double Get(int index)               => _a[index];
        public void   Set(int index, double value) => _a[index] = value;
    }

    public struct NativeDouble3s : IErosionArray<double3>
    {
        NativeArray<double3> _a;
        public NativeDouble3s(NativeArray<double3> a) { _a = a; }
        public double3 Get(int index)                => _a[index];
        public void    Set(int index, double3 value) => _a[index] = value;
    }

    public struct NativeFloat2s : IErosionArray<float2>
    {
        NativeArray<float2> _a;
        public NativeFloat2s(NativeArray<float2> a) { _a = a; }
        public float2 Get(int index)               => _a[index];
        public void   Set(int index, float2 value) => _a[index] = value;
    }

    public struct NativeFloat4s : IErosionArray<float4>
    {
        NativeArray<float4> _a;
        public NativeFloat4s(NativeArray<float4> a) { _a = a; }
        public float4 Get(int index)               => _a[index];
        public void   Set(int index, float4 value) => _a[index] = value;
    }

    public readonly struct NativeIntPairs : IMacroPairs
    {
        [ReadOnly] readonly NativeArray<int2> _a;
        public NativeIntPairs(NativeArray<int2> a) { _a = a; }
        public int2 Get(int k) => _a[k];
    }
}
