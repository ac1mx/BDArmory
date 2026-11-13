// Bullet tracers and smoke trails for all bullets in one procedural draw per texture (BDArmory.GPU.TracerRenderer).
// Each segment is a camera-facing quad built here from its two end points; the width scales with camera distance and field of
// view the same way PooledBullet.UpdateWidth scaled the LineRenderers. Output matches BDArmory/Particles/Bullet.
Shader "BDArmory/GPU/Tracer"
{
    Properties
    {
        _MainTex("Particle Texture", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" }
        Blend SrcAlpha OneMinusSrcColor
        ColorMask RGB
        Cull Off Lighting Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct Segment
            {
                float3 p0; float w0;     // start point, base width there
                float3 p1; float w1;     // end point, base width there
                float3 anchor; float pad; // bullet position the camera distance is measured from
                float4 tint;             // _TintColor * _Lum * 2
                float4 uv;               // u at p0, u at p1
            };

            StructuredBuffer<Segment> _Segments;
            sampler2D _MainTex;
            float _BDTracerCamScale; // 4 in a targeting camera, else 1

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 tint : TEXCOORD1;
            };

            v2f vert(uint id : SV_VertexID)
            {
                Segment s = _Segments[id / 6];
                uint corner = id % 6;
                // Two triangles: (start -), (start +), (end +) / (start -), (end +), (end -)
                bool atEnd = corner == 2 || corner == 4 || corner == 5;
                bool plus = corner == 1 || corner == 2 || corner == 4;
                float3 p = atEnd ? s.p1 : s.p0;
                float fov = degrees(2.0 * atan(1.0 / unity_CameraProjection._m11));
                float scale = max(_BDTracerCamScale, 1.0);
                float factor = (fov / 60.0) * scale * clamp(distance(s.anchor, _WorldSpaceCameraPos), 0.0, 3000.0) / 50.0;
                float width = (atEnd ? s.w1 : s.w0) * factor;
                float3 dir = s.p1 - s.p0;
                float3 side = cross(dir, _WorldSpaceCameraPos - p);
                float len = length(side);
                side = len > 1e-6 ? side / len : UNITY_MATRIX_V[0].xyz;
                p += side * (plus ? 0.5 : -0.5) * width;
                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(p, 1.0));
                o.uv = float2(atEnd ? s.uv.y : s.uv.x, plus ? 1.0 : 0.0);
                o.tint = s.tint;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 c = tex2D(_MainTex, i.uv) * i.tint;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
