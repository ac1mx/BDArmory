// White-hot thermal image for the targeting camera (BDArmory.GPU.ThermalView), used as a replacement shader by RenderType.
// Parts carry their heat in _BDHeat (0 = ambient, 1 = engine hot, set per renderer from part temperatures); everything else
// (terrain, buildings, water) gets a cool background value shaded by the sun. Texture luminance adds faint surface detail.
Shader "BDArmory/GPU/Thermal"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _BDHeat("Heat", Float) = -1
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex;
    float4 _MainTex_ST;
    float _BDHeat;
    float4 _BDThermalSun;   // world-space direction to the sun, w = sun strength (0 at night)
    float4 _BDThermalScene; // background level, sun warming, part ambient level, texture detail

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
        float3 normal : TEXCOORD1;
    };

    v2f vert(appdata_base v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
        o.normal = UnityObjectToWorldNormal(v.normal);
        return o;
    }

    float Level(v2f i)
    {
        float sun = saturate(dot(normalize(i.normal), _BDThermalSun.xyz)) * _BDThermalSun.w;
        float detail = (Luminance(tex2D(_MainTex, i.uv).rgb) - 0.5) * _BDThermalScene.w;
        float heat = _BDHeat - 1.0; // Stored +1 (ThermalView): materials without it read 0, i.e. not a part.
        float level = heat >= 0.0
            ? lerp(_BDThermalScene.z, 1.0, saturate(heat)) + sun * 0.5 * _BDThermalScene.y
            : _BDThermalScene.x + sun * _BDThermalScene.y;
        return saturate(level + detail);
    }

    half4 fragOpaque(v2f i) : SV_Target
    {
        float t = Level(i);
        return half4(t, t, t, 1.0);
    }

    half4 fragCutout(v2f i) : SV_Target
    {
        clip(tex2D(_MainTex, i.uv).a - 0.5);
        float t = Level(i);
        return half4(t, t, t, 1.0);
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragOpaque
            ENDCG
        }
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragCutout
            ENDCG
        }
    }
    SubShader
    {
        // Flames and plumes are hot: added as glow by their own alpha.
        Tags { "RenderType" = "Transparent" }
        Pass
        {
            Blend One One
            ZWrite Off
            Cull Off
            CGPROGRAM
            #pragma vertex vertParticle
            #pragma fragment fragParticle
            struct appdata_p { float4 vertex : POSITION; float4 color : COLOR; float2 texcoord : TEXCOORD0; };
            struct v2p { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float a : TEXCOORD1; };
            v2p vertParticle(appdata_p v)
            {
                v2p o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                o.a = v.color.a;
                return o;
            }
            half4 fragParticle(v2p i) : SV_Target
            {
                float t = tex2D(_MainTex, i.uv).a * i.a * 0.35;
                return half4(t, t, t, 0.0);
            }
            ENDCG
        }
    }
}
