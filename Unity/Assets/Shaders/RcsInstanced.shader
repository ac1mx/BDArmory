// All RCS aspects of a signature render in one instanced draw per mesh (BDArmory.GPU.RcsInstancedRender): instance i is aspect
// i, drawn into slice i of a texture array with that aspect's camera matrices. The shading is Custom/rcsShader's.
Shader "BDArmory/GPU/RcsInstanced"
{
    CGINCLUDE
    #pragma target 4.5
    #include "UnityCG.cginc"
    #include "UnityStandardBRDF.cginc"

    struct DrawData
    {
        float4x4 objectToWorld;
        float4x4 worldToObject;
        float4 color;    // _RCSCOLOR of the material (0 for materials that don't carry the RCS shader)
        float4 lit;      // x = 1 if the material is lit by the aspect light direction, else 0 (its _LIGHTDIR is zero)
    };
    StructuredBuffer<DrawData> _Draws;
    StructuredBuffer<float4x4> _AspectVP;  // GPU projection x view of each aspect's camera
    StructuredBuffer<float4> _AspectLight; // light direction of each aspect (-camera direction)
    int _DrawIndex;
    int _AspectBase;

    struct appdata
    {
        float4 position : POSITION;
        float3 normal : NORMAL;
        uint instance : SV_InstanceID;
    };

    struct v2g
    {
        float4 position : SV_POSITION;
        float3 normal : TEXCOORD1;
        uint slice : TEXCOORD2;
    };

    struct g2f
    {
        float4 position : SV_POSITION;
        float3 normal : TEXCOORD1;
        nointerpolation uint slice : TEXCOORD2;
        uint layer : SV_RenderTargetArrayIndex;
    };

    v2g vert(appdata v)
    {
        DrawData d = _Draws[_DrawIndex];
        uint aspect = v.instance + _AspectBase;
        v2g o;
        // As UnityObjectToClipPos and UnityObjectToWorldNormal with this aspect's matrices.
        o.position = mul(_AspectVP[aspect], mul(d.objectToWorld, float4(v.position.xyz, 1.0)));
        o.normal = normalize(mul(v.normal, (float3x3)d.worldToObject));
        o.slice = aspect;
        return o;
    }

    [maxvertexcount(3)]
    void geom(triangle v2g input[3], inout TriangleStream<g2f> stream)
    {
        for (int k = 0; k < 3; ++k)
        {
            g2f o;
            o.position = input[k].position;
            o.normal = input[k].normal;
            o.slice = input[k].slice;
            o.layer = input[k].slice;
            stream.Append(o);
        }
    }

    float4 frag(g2f i) : SV_Target
    {
        DrawData d = _Draws[_DrawIndex];
        float3 lightDir = _AspectLight[i.slice].xyz * d.lit.x;
        float3 reflectionDir = DotClamped(lightDir, reflect(-lightDir, i.normal));
        half returnStr = max(0, reflectionDir);
        return returnStr * d.color;
    }
    ENDCG

    SubShader
    {
        // The aspect matrices are flipped for rendering into a texture, as the camera's are; with a texture bound, Unity flips the
        // cull mode for that itself.
        Pass
        {
            Cull Back
            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            ENDCG
        }
        // Mirrored transforms (negative determinant): the camera inverts culling for them.
        Pass
        {
            Cull Front
            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            ENDCG
        }
    }
}
