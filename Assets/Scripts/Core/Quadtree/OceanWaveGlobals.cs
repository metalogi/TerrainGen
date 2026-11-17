using System;
using Unity.Mathematics;
using UnityEngine;
using Sonoma.Core.CoordinateSpace;
using Sonoma.Core.Generation;

namespace Sonoma.Core.Quadtree
{
    // The globals the ocean shader's waves read (ocean-b, Docs/Ocean_B_Plan.md, Step 3), owned by
    // TerrainRoot and pushed by it once a frame, and the one thing that must also move when the world
    // rebases.
    //
    //   _SonomaWaves[16]      float4(K.xyz, amplitude): K in cycles a metre, a float's value (B1)
    //   _SonomaWaveExtra[16]  float4(wavelength, Q, shoal depth, 0)
    //   _SonomaWavePhase[16]  float: frac(K · origin − F t + Phase0), OceanWaves.PhaseOffset (B1)
    //   _SonomaWaveCount      int: 0 for a flat sea or no ocean, which the shader reads as no waves
    //   _SonomaWaveLod        float4(depth-0 vertex spacing, 0, 0, 0): NominalSize(0) / (R − 1) (B2)
    //
    // The arrays are pushed whole, 16 long, every frame, like _SonomaMorphRanges: a global array's
    // length is fixed by the first push, and globals are process-wide, so one scene's waves must not
    // be left for the next to draw. Unused slots are zero.
    //
    // The phase offsets depend on the origin, so a rebase changes them in the frame it happens:
    // WorldOriginSystem.ShiftOrigin moves every render-space position by −shift, and K · p with them,
    // so the offset has to move by K · shift. It is not added to the pushed float, as the view position
    // is, because the offset needs double; it is recomputed whole from the new origin and the time
    // this frame's push used, through OriginShifted. Nothing accumulates.
    public sealed class OceanWaveGlobals : IDisposable
    {
        public const string WavesGlobal = "_SonomaWaves";
        public const string ExtraGlobal = "_SonomaWaveExtra";
        public const string PhaseGlobal = "_SonomaWavePhase";
        public const string CountGlobal = "_SonomaWaveCount";
        public const string LodGlobal   = "_SonomaWaveLod";

        static readonly int WavesId = Shader.PropertyToID(WavesGlobal);
        static readonly int ExtraId = Shader.PropertyToID(ExtraGlobal);
        static readonly int PhaseId = Shader.PropertyToID(PhaseGlobal);
        static readonly int CountId = Shader.PropertyToID(CountGlobal);
        static readonly int LodId   = Shader.PropertyToID(LodGlobal);

        readonly OceanWave[] _waves;
        readonly Vector4[]   _k     = new Vector4[OceanWaves.MaxWaves];
        readonly Vector4[]   _extra = new Vector4[OceanWaves.MaxWaves];
        readonly float[]     _phase = new float[OceanWaves.MaxWaves];
        readonly Vector4     _lod;
        bool                 _subscribed;

        // The time the last Push used, in seconds, which a rebase in the same frame reuses.
        public double Time { get; private set; }

        public int Count => _waves.Length;

        // waves: the table, or null for a flat sea or no ocean. depthZeroSpacing: NominalSize(0) /
        // (R − 1), the vertex spacing at depth 0, which the shader scales by 2^−e for the fade (B2).
        public OceanWaveGlobals(OceanWave[] waves, double depthZeroSpacing)
        {
            _waves = waves ?? Array.Empty<OceanWave>();
            if (_waves.Length > OceanWaves.MaxWaves)
                throw new ArgumentOutOfRangeException(nameof(waves), "OceanWaveGlobals: more waves than the shader holds.");
            for (int i = 0; i < _waves.Length; i++)
            {
                OceanWave w = _waves[i];
                _k[i]     = new Vector4((float)w.K.x, (float)w.K.y, (float)w.K.z, (float)w.Amplitude);
                _extra[i] = new Vector4((float)w.Wavelength, (float)w.Choppiness, (float)w.ShoalDepth, 0f);
            }
            _lod = new Vector4((float)depthZeroSpacing, 0f, 0f, 0f);

            // A flat sea has no offsets to move.
            if (_waves.Length > 0)
            {
                WorldOriginSystem.OriginShifted += OnOriginShifted;
                _subscribed = true;
            }
        }

        // Every global, from the current origin and `time`, in seconds: Time.timeAsDouble, or
        // TerrainRoot.WaveTimeOverride for a frozen frame. Allocates nothing.
        public void Push(double time)
        {
            Time = time;
            FillPhases(WorldOriginSystem.WorldOrigin);
            Shader.SetGlobalVectorArray(WavesId, _k);
            Shader.SetGlobalVectorArray(ExtraId, _extra);
            Shader.SetGlobalFloatArray(PhaseId, _phase);
            Shader.SetGlobalInteger(CountId, _waves.Length);
            Shader.SetGlobalVector(LodId, _lod);
        }

        // The offsets alone, from the new origin and this frame's time.
        void OnOriginShifted()
        {
            FillPhases(WorldOriginSystem.WorldOrigin);
            Shader.SetGlobalFloatArray(PhaseId, _phase);
        }

        void FillPhases(double3 origin)
        {
            for (int i = 0; i < _waves.Length; i++)
                _phase[i] = (float)OceanWaves.PhaseOffset(_waves[i], origin, Time);
        }

        public void Dispose()
        {
            if (!_subscribed) return;
            WorldOriginSystem.OriginShifted -= OnOriginShifted;
            _subscribed = false;
        }
    }
}
