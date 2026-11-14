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
    /// Skinned meshes are baked once, live local-space particles once per aspect (their billboards face each camera). Vessels with
    /// something this can't draw the way the camera does (local-space lines, particle trails, non-triangle meshes) are left to the
    /// camera renders.
    /// </summary>
    public static class RcsInstancedRender
    {
        /// <summary>
        /// About 7x less main-thread time per signature than the camera renders. The signatures come within about 1% of the camera
        /// renders' but are not bit-identical (single pixels on thin edges and ±1 shading steps differ: Unity's own matrix products
        /// and shader code order aren't reproduced exactly). Off: one camera render per aspect as before (also a BDAPerf flag: arm).
        /// </summary>
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

        struct Draw { public Mesh mesh; public int submesh; public Material material; public int aspect; public bool mirrored; } // aspect -1: every aspect

        static readonly int drawsID = Shader.PropertyToID("_Draws"), vpID = Shader.PropertyToID("_AspectVP"), lightID = Shader.PropertyToID("_AspectLight"),
            drawIndexID = Shader.PropertyToID("_DrawIndex"), aspectBaseID = Shader.PropertyToID("_AspectBase"), rcsColorID = Shader.PropertyToID("_RCSCOLOR");
        static readonly List<Draw> draws = new List<Draw>();
        static readonly List<DrawData> drawData = new List<DrawData>();
        static readonly List<Renderer> renderers = new List<Renderer>();
        static readonly HashSet<Renderer> seen = new HashSet<Renderer>();
        static readonly List<LODGroup> lodGroups = new List<LODGroup>();
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
        /// (GPU projection, rendering into a texture) and light direction. poseCamera(i) puts aspectCamera at aspect i: particle
        /// billboards face the camera, so live local-space particles are baked from each aspect's pose and drawn into that slice.
        /// Null if the vessel has to be rendered by the cameras.
        /// </summary>
        public static RenderTexture Render(Vessel v, Shader rcsShader, int resolution, int aspects, Matrix4x4[] aspectVP, Vector4[] aspectLight,
            Camera aspectCamera, System.Action<int> poseCamera)
        {
            LastFallback = null;
            if (!Available) { LastFallback = "unavailable"; return null; }
            if (!Collect(v, rcsShader)) return null;
            if (particleSystems.Count > 0)
                for (int a = 0; a < aspects; ++a)
                {
                    poseCamera(a);
                    for (int s = 0; s < particleSystems.Count; ++s)
                    {
                        var r = particleSystems[s];
                        var mesh = NextBaked();
                        r.BakeMesh(mesh, aspectCamera, false); // In the particle system's local space.
                        if (mesh.vertexCount == 0) continue;
                        if (!AddMesh(r, mesh, rcsShader, ParticleMatrix(r), a, 1)) return null;
                    }
                }
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
                args[i * 5 + 1] = d.aspect < 0 ? (uint)aspects : 1u;
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

            cmd.Clear();
            cmd.SetRenderTarget(target, 0, CubemapFace.Unknown, -1);
            cmd.ClearRenderTarget(true, true, Color.black, 1f);
            for (int i = 0; i < draws.Count; ++i)
            {
                block.SetInt(drawIndexID, i);
                block.SetInt(aspectBaseID, Mathf.Max(0, draws[i].aspect));
                cmd.DrawMeshInstancedIndirect(draws[i].mesh, draws[i].submesh, material, draws[i].mirrored ? 1 : 0, argsBuffer, i * 5 * sizeof(uint), block);
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
            draws.Clear(); drawData.Clear(); seen.Clear(); particleSystems.Clear();
            bakedUsed = 0;
            for (int p = 0; p < v.parts.Count; ++p)
            {
                var part = v.parts[p];
                if (part == null) continue;
                // LOD groups: the aspect cameras see the vessel filling the view, i.e. LOD 0; the other levels aren't drawn.
                part.GetComponentsInChildren(false, lodGroups);
                for (int g = 0; g < lodGroups.Count; ++g)
                {
                    if (!lodGroups[g].enabled) continue;
                    var lods = lodGroups[g].GetLODs();
                    for (int l = 1; l < lods.Length; ++l)
                        foreach (var lr in lods[l].renderers) if (lr != null) seen.Add(lr);
                    if (lods.Length > 0) foreach (var lr in lods[0].renderers) if (lr != null) seen.Remove(lr);
                }
                part.GetComponentsInChildren(false, renderers);
                for (int i = 0; i < renderers.Count; ++i)
                {
                    var r = renderers[i];
                    if (r == null || !seen.Add(r) || !r.enabled || r.forceRenderingOff || r.gameObject.layer != 0 || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                    switch (r)
                    {
                        case MeshRenderer meshRenderer:
                            var filter = r.GetComponent<MeshFilter>();
                            var mesh = filter != null ? filter.sharedMesh : null;
                            if (mesh == null) continue;
                            if (!AddMesh(r, mesh, rcsShader, r.localToWorldMatrix, -1, int.MaxValue, r.worldToLocalMatrix)) return false;
                            break;
                        case ParticleSystemRenderer particles:
                            // World-space particles stay where they were when the vessel is moved 1000 m away for the render; from
                            // every aspect camera (1000 m from the vessel, ±200 m clip range, 2° view) they are out of view.
                            // Local-space ones move with it: baked per aspect in Render.
                            var system = r.GetComponent<ParticleSystem>();
                            if (system == null || system.particleCount == 0 || system.main.simulationSpace == ParticleSystemSimulationSpace.World) break;
                            if (system.trails.enabled || system.main.simulationSpace == ParticleSystemSimulationSpace.Custom) { LastFallback = $"particle trails or custom space on {part.partInfo?.name}"; return false; }
                            particleSystems.Add(particles);
                            break;
                        case LineRenderer line:
                            if (line.positionCount > 1 && !line.useWorldSpace) { LastFallback = $"line renderer on {part.partInfo?.name}"; return false; }
                            break;
                        case TrailRenderer trail:
                            break; // Always world space: left behind like the particles.
                        case SkinnedMeshRenderer skinned:
                            if (skinned.sharedMesh == null) continue;
                            // BakeMesh applies the transform's scale (Unity 2019.4), so the baked mesh is drawn without it.
                            var bakedMesh = NextBaked();
                            skinned.BakeMesh(bakedMesh);
                            if (!AddMesh(r, bakedMesh, rcsShader, Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one), -1, int.MaxValue)) return false;
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
        static readonly List<ParticleSystemRenderer> particleSystems = new List<ParticleSystemRenderer>();
        static readonly List<Mesh> bakedMeshes = new List<Mesh>();
        static int bakedUsed;

        /// <summary>A pooled mesh for a baked skinned mesh or particle snapshot, valid for this render.</summary>
        static Mesh NextBaked()
        {
            if (bakedUsed == bakedMeshes.Count) bakedMeshes.Add(new Mesh { name = "BDArmory RCS baked" });
            return bakedMeshes[bakedUsed++];
        }

        /// <summary>The matrix a local-space particle system renders with, by its scaling mode.</summary>
        static Matrix4x4 ParticleMatrix(ParticleSystemRenderer r)
        {
            var t = r.transform;
            switch (r.GetComponent<ParticleSystem>().main.scalingMode)
            {
                case ParticleSystemScalingMode.Local: return Matrix4x4.TRS(t.position, t.rotation, t.localScale);
                case ParticleSystemScalingMode.Shape: return Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                default: return t.localToWorldMatrix;
            }
        }

        /// <summary>Queue mesh with r's materials (the first maxMaterials of them) for one aspect, or every aspect if aspect is -1.</summary>
        static bool AddMesh(Renderer r, Mesh mesh, Shader rcsShader, Matrix4x4 objectToWorld, int aspect, int maxMaterials, Matrix4x4? inverse = null)
        {
            r.GetSharedMaterials(materials);
            int submeshes = mesh.subMeshCount;
            if (submeshes == 0) return true;
            Matrix4x4 worldToObject = inverse ?? objectToWorld.inverse; // The renderer's own inverse where there is one.
            for (int k = 0; k < materials.Count && k < maxMaterials; ++k)
            {
                var m = materials[k];
                if (m == null) continue;
                int submesh = Mathf.Min(k, submeshes - 1); // Extra materials draw the last submesh again, as Unity does.
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles) { LastFallback = $"{mesh.GetTopology(submesh)} mesh {mesh.name}"; return false; }
                bool rcs = m.shader == rcsShader;
                draws.Add(new Draw { mesh = mesh, submesh = submesh, material = m, aspect = aspect, mirrored = objectToWorld.determinant < 0f });
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
