using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace BDArmory.GPU
{
    /// <summary>
    /// Renders every aspect of a radar signature at once: one instanced draw per mesh (shader BDArmory/GPU/RcsInstanced), instance i
    /// drawn with aspect i's camera matrices into slice i of a texture array, instead of one camera render per aspect (each of them
    /// culling the scene and submitting every part again). The shading and the matrices are those of the camera renders.
    /// Vessels with something this can't draw the way the camera does (skinned meshes, live particles or lines, non-triangle
    /// meshes) are left to the camera renders.
    /// </summary>
    public static class RcsInstancedRender
    {
        /// <summary>Off: one camera render per aspect as before (also a BDAPerf flag: arm).</summary>
        public static bool Enabled = true;
        public static bool Available => Enabled && GpuAssets.RcsInstanced != null && RcsGpuReduction.SlicesAvailable && SystemInfo.supports2DArrayTextures && SystemInfo.supportsGeometryShaders;
        /// <summary>Why the last render fell back to the cameras (for the check log).</summary>
        public static string LastFallback { get; private set; }
        public static int LastDraws { get; private set; }

        [StructLayout(LayoutKind.Sequential)]
        struct DrawData
        {
            public Matrix4x4 objectToWorld, worldToObject;
            public Color color;
            public Vector4 lit;
        }
        const int DrawStride = 2 * 64 + 2 * 16;

        struct Draw { public Mesh mesh; public int submesh; public Material material; }

        static readonly int drawsID = Shader.PropertyToID("_Draws"), vpID = Shader.PropertyToID("_AspectVP"), lightID = Shader.PropertyToID("_AspectLight"),
            drawIndexID = Shader.PropertyToID("_DrawIndex"), aspectBaseID = Shader.PropertyToID("_AspectBase"), rcsColorID = Shader.PropertyToID("_RCSCOLOR");
        static readonly List<Draw> draws = new List<Draw>();
        static readonly List<DrawData> drawData = new List<DrawData>();
        static readonly List<Renderer> renderers = new List<Renderer>();
        static readonly HashSet<Renderer> seen = new HashSet<Renderer>();
        static DrawData[] drawArray = new DrawData[0];
        static uint[] args = new uint[0];
        static ComputeBuffer drawBuffer, argsBuffer, vpBuffer, lightBuffer;
        static Matrix4x4[] vps = new Matrix4x4[0];
        static Vector4[] lights = new Vector4[0];
        static RenderTexture target;
        static Material material;
        static CommandBuffer cmd;
        static MaterialPropertyBlock block;

        /// <summary>
        /// Draw all aspects of v into a texture array (slice i = aspect i). vp/light give each aspect's camera view-projection
        /// (GPU projection, rendering into a texture) and light direction. Null if the vessel has to be rendered by the cameras.
        /// </summary>
        public static RenderTexture Render(Vessel v, Shader rcsShader, int resolution, int aspects, Matrix4x4[] aspectVP, Vector4[] aspectLight)
        {
            LastFallback = null;
            if (!Available) { LastFallback = "unavailable"; return null; }
            if (!Collect(v, rcsShader)) return null;
            if (draws.Count == 0) { LastFallback = "nothing to draw"; return null; }
            LastDraws = draws.Count;
            if (material == null) material = new Material(GpuAssets.RcsInstanced);
            if (block == null) block = new MaterialPropertyBlock();
            if (cmd == null) cmd = new CommandBuffer { name = "BDArmory RCS aspects" };
            EnsureTarget(resolution, aspects);

            if (drawBuffer == null || drawBuffer.count < draws.Count)
            {
                drawBuffer?.Release(); argsBuffer?.Release();
                int capacity = Mathf.NextPowerOfTwo(draws.Count);
                drawBuffer = new ComputeBuffer(capacity, DrawStride);
                argsBuffer = new ComputeBuffer(capacity * 5, sizeof(uint), ComputeBufferType.IndirectArguments);
                drawArray = new DrawData[capacity];
                args = new uint[capacity * 5];
            }
            if (vpBuffer == null || vpBuffer.count < aspects)
            {
                vpBuffer?.Release(); lightBuffer?.Release();
                vpBuffer = new ComputeBuffer(aspects, 64);
                lightBuffer = new ComputeBuffer(aspects, 16);
            }
            for (int i = 0; i < draws.Count; ++i)
            {
                drawArray[i] = drawData[i];
                var d = draws[i];
                args[i * 5 + 0] = d.mesh.GetIndexCount(d.submesh);
                args[i * 5 + 1] = (uint)aspects;
                args[i * 5 + 2] = d.mesh.GetIndexStart(d.submesh);
                args[i * 5 + 3] = d.mesh.GetBaseVertex(d.submesh);
                args[i * 5 + 4] = 0;
            }
            drawBuffer.SetData(drawArray, 0, 0, draws.Count);
            argsBuffer.SetData(args, 0, 0, draws.Count * 5);
            vpBuffer.SetData(aspectVP, 0, 0, aspects);
            lightBuffer.SetData(aspectLight, 0, 0, aspects);
            material.SetBuffer(drawsID, drawBuffer);
            material.SetBuffer(vpID, vpBuffer);
            material.SetBuffer(lightID, lightBuffer);
            material.SetInt(aspectBaseID, 0);

            cmd.Clear();
            cmd.SetRenderTarget(target, 0, CubemapFace.Unknown, -1);
            cmd.ClearRenderTarget(true, true, Color.black, 1f);
            for (int i = 0; i < draws.Count; ++i)
            {
                block.SetInt(drawIndexID, i);
                cmd.DrawMeshInstancedIndirect(draws[i].mesh, draws[i].submesh, material, 0, argsBuffer, i * 5 * sizeof(uint), block);
            }
            Graphics.ExecuteCommandBuffer(cmd);
            return target;
        }

        static void EnsureTarget(int resolution, int aspects)
        {
            if (target != null && target.width == resolution && target.volumeDepth >= aspects) return;
            if (target != null) target.Release();
            target = new RenderTexture(resolution, resolution, 16, RenderTextureFormat.ARGB32)
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = aspects,
                antiAliasing = 1,
                useMipMap = false,
                name = "BDArmory RCS aspects",
            };
            target.Create();
        }

        /// <summary>The vessel's renderers as the layer-0 radar camera would draw them; false (with LastFallback) if that can't be matched.</summary>
        static bool Collect(Vessel v, Shader rcsShader)
        {
            draws.Clear(); drawData.Clear(); seen.Clear();
            bakedUsed = 0;
            for (int p = 0; p < v.parts.Count; ++p)
            {
                var part = v.parts[p];
                if (part == null) continue;
                part.GetComponentsInChildren(false, renderers);
                for (int i = 0; i < renderers.Count; ++i)
                {
                    var r = renderers[i];
                    if (r == null || !seen.Add(r) || !r.enabled || r.forceRenderingOff || r.gameObject.layer != 0) continue;
                    switch (r)
                    {
                        case MeshRenderer meshRenderer:
                            var filter = r.GetComponent<MeshFilter>();
                            var mesh = filter != null ? filter.sharedMesh : null;
                            if (mesh == null) continue;
                            if (!AddMesh(r, mesh, rcsShader)) return false;
                            break;
                        case ParticleSystemRenderer particles:
                            // World-space particles stay where they were when the vessel is moved 1000 m away for the render; from
                            // every aspect camera (1000 m from the vessel, ±200 m clip range, 2° view) they are out of view.
                            var system = r.GetComponent<ParticleSystem>();
                            if (system != null && system.particleCount > 0 && system.main.simulationSpace != ParticleSystemSimulationSpace.World) { LastFallback = $"local-space particles on {part.partInfo?.name}"; return false; }
                            break;
                        case LineRenderer line:
                            if (line.positionCount > 1 && !line.useWorldSpace) { LastFallback = $"line renderer on {part.partInfo?.name}"; return false; }
                            break;
                        case TrailRenderer trail:
                            break; // Always world space: left behind like the particles.
                        case SkinnedMeshRenderer skinned:
                            if (skinned.sharedMesh == null) continue;
                            if ((skinned.transform.lossyScale - Vector3.one).sqrMagnitude > 1e-8f) { LastFallback = $"scaled skinned mesh on {part.partInfo?.name}"; return false; }
                            if (!AddMesh(r, Baked(skinned), rcsShader)) return false;
                            break;
                        default:
                            LastFallback = $"{r.GetType().Name} on {part.partInfo?.name}";
                            return false;
                    }
                }
            }
            return true;
        }

        static readonly List<Material> materials = new List<Material>();
        static readonly List<Mesh> bakedMeshes = new List<Mesh>();
        static int bakedUsed;

        /// <summary>The skinned mesh in its current pose (a pooled mesh, valid for this render), in the renderer's local space.</summary>
        static Mesh Baked(SkinnedMeshRenderer skinned)
        {
            if (bakedUsed == bakedMeshes.Count) bakedMeshes.Add(new Mesh { name = "BDArmory RCS baked" });
            var mesh = bakedMeshes[bakedUsed++];
            skinned.BakeMesh(mesh);
            return mesh;
        }

        static bool AddMesh(Renderer r, Mesh mesh, Shader rcsShader)
        {
            r.GetSharedMaterials(materials);
            int submeshes = mesh.subMeshCount;
            if (submeshes == 0) return true;
            Matrix4x4 objectToWorld = r.localToWorldMatrix, worldToObject = r.worldToLocalMatrix;
            for (int k = 0; k < materials.Count; ++k)
            {
                var m = materials[k];
                if (m == null) continue;
                int submesh = Mathf.Min(k, submeshes - 1); // Extra materials draw the last submesh again, as Unity does.
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles) { LastFallback = $"{mesh.GetTopology(submesh)} mesh {mesh.name}"; return false; }
                bool rcs = m.shader == rcsShader;
                draws.Add(new Draw { mesh = mesh, submesh = submesh, material = m });
                drawData.Add(new DrawData
                {
                    objectToWorld = objectToWorld,
                    worldToObject = worldToObject,
                    color = rcs ? m.GetColor(rcsColorID) : Color.clear,
                    lit = new Vector4(rcs ? 1f : 0f, 0f, 0f, 0f),
                });
            }
            return true;
        }
    }
}
