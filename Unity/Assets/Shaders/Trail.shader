// Missile smoke, engine contrails and wingtip vortices for every trail in one procedural draw (BDArmory.GPU.Trails).
// Each trail is a ring of points in its own block of _Points; segment k joins its k-th and (k+1)-th newest points (segment 0 joins
// the live emitter position to the newest point). Every segment is a camera-facing quad that widens and fades with the age of
// its ends, at least a pixel and a half wide (thinner trails keep their coverage by fading instead), lit by the sun.
Shader "BDArmory/GPU/Trail"
{
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "BDArmoryTrail" } // Not a replacement-shader type: the thermal view leaves trails out.
        Blend One OneMinusSrcAlpha
        ColorMask RGB
        Cull Off Lighting Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct TrailPoint
            {
                float3 offset; float birth;  // from the trail's anchor; Time.time when laid
                float width; float alpha;    // base width (m) and opacity when laid
                float along; float pad;      // distance along the trail (m)
            };

            struct TrailSlot
            {
                float3 anchor; float head;   // anchor in world space; index of the newest point in the block
                float3 live; float count;    // live emitter position (world); points in use
                float spread; float lifetime; float fadeIn; float liveAlpha; // width growth (m per sqrt s), seconds, seconds, opacity at the emitter
                float4 color;
                float liveWidth; float liveAlong; float seed; float pad;
            };

            StructuredBuffer<TrailPoint> _Points;
            StructuredBuffer<TrailSlot> _Slots;
            int _PointsPerSlot;
            float _TrailTime;
            float4 _TrailSunDir;   // xyz: towards the sun; w: daylight 0-1
            float4 _TrailSunColor; // rgb
            float4 _TrailAmbient;  // rgb

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0; // x: along the trail in widths (noise), y: -1..1 across
                float4 color : TEXCOORD1;
            };

            struct End { float3 world; float age; float width; float alpha; float along; };

            End PointEnd(TrailSlot s, uint slot, int k)
            {
                int n = _PointsPerSlot;
                int index = ((int)s.head - k + n) % n;
                TrailPoint p = _Points[slot * n + index];
                End e;
                e.world = s.anchor + p.offset;
                e.age = _TrailTime - p.birth;
                e.width = p.width;
                e.alpha = p.alpha;
                e.along = p.along;
                return e;
            }

            v2f Hidden()
            {
                v2f o;
                o.pos = float4(2.0, 2.0, 2.0, 1.0); // Outside the clip volume.
                o.uv = 0;
                o.color = 0;
                return o;
            }

            v2f vert(uint id : SV_VertexID, uint slot : SV_InstanceID)
            {
                TrailSlot s = _Slots[slot];
                int seg = (int)(id / 6);
                uint corner = id % 6;
                if (seg >= (int)s.count) return Hidden();

                End a, b; // a: the younger end, b: the older end
                if (seg == 0)
                {
                    a.world = s.live; a.age = 0.0; a.width = s.liveWidth; a.alpha = s.liveAlpha; a.along = s.liveAlong;
                }
                else a = PointEnd(s, slot, seg - 1);
                b = PointEnd(s, slot, seg);
                if (b.age > s.lifetime) return Hidden();

                // Two triangles: (a -), (a +), (b +) / (a -), (b +), (b -)
                bool atB = corner == 2 || corner == 4 || corner == 5;
                bool plus = corner == 1 || corner == 2 || corner == 4;
                End e = a;
                if (atB) e = b;

                float age = max(e.age, 0.0);
                float width = e.width + s.spread * sqrt(age);
                float life = saturate(1.0 - age / s.lifetime);
                float alpha = e.alpha * life * sqrt(life) * sqrt(e.width / max(width, 1e-3)); // The same smoke over a wider band.
                if (s.fadeIn > 0.0) alpha *= saturate(age / s.fadeIn);

                float distance = length(e.world - _WorldSpaceCameraPos);
                float pixel = distance * 2.0 / (unity_CameraProjection._m11 * _ScreenParams.y);
                float minWidth = 1.5 * pixel;
                if (width < minWidth) { alpha *= width / minWidth; width = minWidth; }

                float3 dir = b.world - a.world;
                float3 side = cross(dir, _WorldSpaceCameraPos - e.world);
                float len = length(side);
                side = len > 1e-6 ? side / len : UNITY_MATRIX_V[0].xyz;
                float3 p = e.world + side * (plus ? 0.5 : -0.5) * width;

                // Sunlit from the side, brighter looking towards the sun (forward scattering), dark at night.
                float3 view = normalize(e.world - _WorldSpaceCameraPos);
                float forward = pow(saturate(dot(view, _TrailSunDir.xyz)), 6.0);
                float3 light = _TrailAmbient.rgb + _TrailSunColor.rgb * _TrailSunDir.w * (0.75 + 1.25 * forward);

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(p, 1.0));
                o.uv = float2(e.along / max(2.0 * width, 0.5) + s.seed, plus ? 1.0 : -1.0);
                o.color = float4(s.color.rgb * light, alpha * s.color.a);
                return o;
            }

            float Hash(float x) { return frac(sin(x * 127.1) * 43758.5453); }

            float Noise(float x)
            {
                float i = floor(x), f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(Hash(i), Hash(i + 1.0), f);
            }

            half4 frag(v2f i) : SV_Target
            {
                float across = 1.0 - i.uv.y * i.uv.y;
                float puff = 0.65 + 0.35 * Noise(i.uv.x) + 0.15 * (Noise(i.uv.x * 3.7 + 11.0) - 0.5);
                float a = saturate(i.color.a * across * across * puff);
                return half4(i.color.rgb * a, a);
            }
            ENDCG
        }
    }
}
