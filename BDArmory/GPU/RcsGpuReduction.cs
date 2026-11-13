using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BDArmory.GPU
{
    /// <summary>
    /// Sums rendered RCS aspects on the GPU (RcsReduce.compute) and reads the per-aspect sums back asynchronously, instead of
    /// reading every aspect's pixels back to the CPU (a pipeline stall) and summing them there.
    /// Usage per signature render: Begin, Accumulate once per rendered aspect, End.
    /// </summary>
    public static class RcsGpuReduction
    {
        const int Capacity = 128; // Aspects per render; the aspected table has 107.
        static ComputeShader shader;
        static int kernel = -1, sliceKernel = -1;
        static readonly int sourceArrayID = Shader.PropertyToID("_SourceArray");
        static readonly int sourceID = Shader.PropertyToID("_Source"), sumsID = Shader.PropertyToID("_Sums"), slotID = Shader.PropertyToID("_Slot"), sizeID = Shader.PropertyToID("_Size");
        static readonly Stack<ComputeBuffer> pool = new Stack<ComputeBuffer>();
        static readonly uint[] zeros = new uint[Capacity];
        static readonly uint[] results = new uint[Capacity];
        static ComputeBuffer current;
        static int groups;
        static bool disabled;

        internal static void SetShader(ComputeShader computeShader)
        {
            shader = computeShader;
            kernel = shader != null && shader.HasKernel("Reduce") ? shader.FindKernel("Reduce") : -1;
            sliceKernel = shader != null && shader.HasKernel("ReduceSlices") ? shader.FindKernel("ReduceSlices") : -1;
        }

        public static bool SlicesAvailable => Available && sliceKernel >= 0;

        /// <summary>
        /// Start a render whose aspects are already in the slices of a texture array (slice i = aspect i), and sum them all.
        /// Follow with End. False if the GPU path isn't available.
        /// </summary>
        public static bool BeginSlices(int aspects, RenderTexture array)
        {
            if (!SlicesAvailable || aspects > Capacity || current != null) return false;
            current = pool.Count > 0 ? pool.Pop() : new ComputeBuffer(Capacity, sizeof(uint));
            current.SetData(zeros, 0, 0, aspects);
            int size = array.width;
            groups = (size + 15) / 16;
            shader.SetTexture(sliceKernel, sourceArrayID, array);
            shader.SetBuffer(sliceKernel, sumsID, current);
            shader.SetInt(sizeID, size);
            shader.Dispatch(sliceKernel, groups, groups, aspects);
            return true;
        }

        /// <summary>Off: signatures are read back on the CPU as before (also a BDAPerf flag: arm).</summary>
        public static bool Enabled = true;
        public static bool Available => Enabled && !disabled && kernel >= 0 && SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        /// <summary>Start a render of the given number of aspects, each rendered into source. False if the GPU path isn't available.</summary>
        public static bool Begin(int aspects, RenderTexture source)
        {
            if (!Available || aspects > Capacity || current != null) return false;
            current = pool.Count > 0 ? pool.Pop() : new ComputeBuffer(Capacity, sizeof(uint));
            current.SetData(zeros, 0, 0, aspects);
            int size = source.width;
            groups = (size + 15) / 16;
            shader.SetTexture(kernel, sourceID, source);
            shader.SetBuffer(kernel, sumsID, current);
            shader.SetInt(sizeID, size);
            return true;
        }

        /// <summary>Add the aspect currently in the source texture to slot.</summary>
        public static void Accumulate(int slot)
        {
            shader.SetInt(slotID, slot);
            shader.Dispatch(kernel, groups, groups, 1);
        }

        /// <summary>
        /// Read the sums back. done runs on the main thread a frame or few later with the per-aspect pixel sums (in 8-bit units),
        /// or with null if the read-back failed. The array is only valid during the call.
        /// </summary>
        public static void End(int aspects, Action<uint[]> done)
        {
            var buffer = current;
            current = null;
            AsyncGPUReadback.Request(buffer, aspects * sizeof(uint), 0, request =>
            {
                pool.Push(buffer);
                uint[] sums = null;
                if (!request.hasError)
                {
                    var data = request.GetData<uint>();
                    for (int i = 0; i < aspects; ++i) results[i] = data[i];
                    sums = results;
                }
                else
                {
                    Debug.LogWarning("[BDArmory.RcsGpuReduction]: GPU read-back failed, falling back to the CPU read-back.");
                    disabled = true;
                }
                done(sums);
            });
        }

        /// <summary>Abandon a render started with Begin (e.g. after an exception).</summary>
        public static void Cancel()
        {
            if (current != null) pool.Push(current);
            current = null;
        }
    }
}
