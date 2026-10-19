// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Turns the two background passes of an icon into the final RGBA image.
//
// Input: the same scene rendered over black (_MainTex) and over white (_White) at twice the output
// size. For each output pixel, a 2x2 box of each pass, in sRGB-encoded values:
//   alpha  = 1 - mean(white - black) / span        (span measured on the corner texel: the backgrounds)
//   colour = (black - blackBackground) / alpha      (un-premultiplied)
//   colour = luminance + (colour - luminance) * _Saturation
// The output goes to a linear render texture, so the stored bytes are these sRGB values unchanged.
Shader "Hidden/BuildingIconGenerator/Compose"
{
    Properties
    {
        _MainTex ("Over black", 2D) = "black" {}
        _White ("Over white", 2D) = "white" {}
        _Saturation ("Saturation", Float) = 1.5
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _White;
            float4 _MainTex_TexelSize;
            float _Saturation;

            float3 Encoded(sampler2D s, float2 uv)
            {
                // The passes are sRGB render targets: the sampler decodes them; re-encode so the maths below
                // works on the stored sRGB values.
                return LinearToGammaSpace(tex2Dlod(s, float4(uv, 0, 0)).rgb);
            }

            float3 Box(sampler2D s, float2 uv)
            {
                float2 h = 0.5 * _MainTex_TexelSize.xy;
                return 0.25 * (Encoded(s, uv + float2(-h.x, -h.y)) + Encoded(s, uv + float2(h.x, -h.y))
                             + Encoded(s, uv + float2(-h.x, h.y)) + Encoded(s, uv + float2(h.x, h.y)));
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float2 corner = 1.0 * _MainTex_TexelSize.xy;
                float3 background0 = Box(_MainTex, corner);
                float3 background1 = Box(_White, corner);
                float span = max(1.0 / 255.0, dot(background1 - background0, 1.0 / 3.0));

                float3 overBlack = Box(_MainTex, i.uv);
                float3 overWhite = Box(_White, i.uv);
                float difference = dot(overWhite - overBlack, 1.0 / 3.0);
                float alpha = saturate(1.0 - difference / span);

                if (alpha < 0.01)
                {
                    return float4(0, 0, 0, 0);
                }

                float3 colour = (overBlack - background0) / alpha;
                float luminance = dot(colour, float3(0.2126, 0.7152, 0.0722));
                colour = luminance + (colour - luminance) * _Saturation;
                return float4(saturate(colour), alpha);
            }
            ENDCG
        }
    }

    Fallback Off
}
