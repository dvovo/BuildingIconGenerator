// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Composes the ground of a lot into one top-down texture.
//
// Drawn with GL into a render target covering the lot: first the surface layers (lot grass,
// asphalt, paving...) in the game's renderer-priority order, then every ground decal (parking
// bays, lane markings, arrows) as a quad textured with its atlas rectangle.
//
// _Alpha.z = 0: opaque layer, colour = texture * tint.
// _Alpha.z = 1: decal. Opacity = albedo alpha remapped from [_Alpha.x, _Alpha.y] to [0, 1], so
//   painted lines (~0.5 alpha) render solid and dirt or wear fade out. Sampled 4x4 over the pixel
//   footprint (the atlas has no mips); when the atlas is minified, the maximum over a widened
//   footprint is blended in, so lines thinner than a pixel on large lots stay visible.
Shader "Hidden/BuildingIconGenerator/GroundSplat"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Alpha ("Alpha remap (lo, hi, decal, -)", Vector) = (0, 1, 0, 0)
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _Tint;
            float4 _Alpha;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 dx = ddx(i.uv);
                float2 dy = ddy(i.uv);

                if (_Alpha.z < 0.5)
                {
                    float4 layer = tex2D(_MainTex, i.uv);
                    return float4(layer.rgb * _Tint.rgb, 1.0);
                }

                float2 texels = _MainTex_TexelSize.zw;
                float minification = max(length(dx * texels), length(dy * texels));
                float k = saturate((minification - 1.5) / 4.0);
                float spread = lerp(1.0, 3.0, k);

                float3 rgb = 0;
                float sum = 0;
                float peak = 0;

                [unroll]
                for (int y = 0; y < 4; y++)
                {
                    [unroll]
                    for (int x = 0; x < 4; x++)
                    {
                        float2 offset = (((x + 0.5) / 4.0 - 0.5) * dx + ((y + 0.5) / 4.0 - 0.5) * dy) * spread;
                        float4 c = tex2Dlod(_MainTex, float4(i.uv + offset, 0, 0));
                        float a = saturate((c.a - _Alpha.x) / max(1e-4, _Alpha.y - _Alpha.x));
                        rgb += c.rgb * a;
                        sum += a;
                        peak = max(peak, a);
                    }
                }

                float3 colour = sum > 1e-4 ? rgb / sum : float3(0, 0, 0);
                float alpha = lerp(sum / 16.0, peak, k);
                return float4(colour * _Tint.rgb, alpha * _Tint.a);
            }
            ENDCG
        }
    }

    Fallback Off
}
