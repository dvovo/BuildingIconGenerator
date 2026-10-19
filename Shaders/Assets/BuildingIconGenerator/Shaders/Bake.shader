// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo
//
// Bakes a building's colour variation into its albedo:
//
//   rgb = albedo.rgb * lerp(1, c0, control.r) * lerp(1, c1, control.g) * lerp(1, c2, control.b)
//         * lerp(1, _Metal.y, saturate(maskMap.r * _Metal.x))
//   a   = albedo.a
//
// The last factor darkens metal texels (CS2 stores metalness in the mask's red channel, scaled by
// the template's _Metallic) to _Metal.y of their brightness: metal under a neutral sky rather than
// white paint, since no reflections are rendered. _Metal.x = 0 disables it.
//
// All maths in linear space: the target is a linear render texture, sRGB sources are decoded by
// the sampler and the colour slots are passed already linear. Used only through Graphics.Blit.
Shader "Hidden/BuildingIconGenerator/Bake"
{
    Properties
    {
        _MainTex ("Albedo", 2D) = "white" {}
        _ControlMask ("Control mask", 2D) = "black" {}
        _Colour0 ("Colour slot 0 (linear)", Vector) = (1, 1, 1, 1)
        _Colour1 ("Colour slot 1 (linear)", Vector) = (1, 1, 1, 1)
        _Colour2 ("Colour slot 2 (linear)", Vector) = (1, 1, 1, 1)
        _MaskMap ("Mask map (metalness in R)", 2D) = "black" {}
        _Metal ("Metal (scale, tone, -, -)", Vector) = (0, 1, 0, 0)
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
            sampler2D _ControlMask;
            float4 _Colour0;
            float4 _Colour1;
            float4 _Colour2;
            sampler2D _MaskMap;
            float4 _Metal;

            float4 frag(v2f_img i) : SV_Target
            {
                float4 albedo = tex2D(_MainTex, i.uv);
                float3 w = tex2D(_ControlMask, i.uv).rgb;
                float3 tint = lerp(1.0.xxx, _Colour0.rgb, w.r)
                            * lerp(1.0.xxx, _Colour1.rgb, w.g)
                            * lerp(1.0.xxx, _Colour2.rgb, w.b);
                float metal = saturate(tex2D(_MaskMap, i.uv).r * _Metal.x);
                return float4(albedo.rgb * tint * lerp(1.0, _Metal.y, metal), albedo.a);
            }
            ENDCG
        }
    }

    Fallback Off
}
