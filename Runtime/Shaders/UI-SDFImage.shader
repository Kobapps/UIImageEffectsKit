// Surface-correct UI shader for SDFImage. Renders an ordered STACK of effect layers (face /
// outline / shadow / glow, repeatable) packed into uniform arrays, with the full uGUI masking +
// clipping contract. Effect sizes are fractions of the field's spread, so they're crisp and
// resolution/zoom-independent.
Shader "UI/SDF Image"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _SDFTex ("SDF Field", 2D) = "black" {}
        _SDFRect ("SDF UV Remap (sx,sy,ox,oy)", Vector) = (1,1,0,0)
        _SDFExtend ("SDF Extend (sx,sy)", Vector) = (0,0,0,0)

        // --- uGUI stencil / mask plumbing ---
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        _UIMaskSoftnessX ("Mask SoftnessX", Float) = 0
        _UIMaskSoftnessY ("Mask SoftnessY", Float) = 0

        [HideInInspector] _ClipRect ("Clip Rect", Vector) = (-32767,-32767,32767,32767)
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #pragma shader_feature_local SDFIMAGE_SDF

            #define SDF_MAX_FX 8

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex        : SV_POSITION;
                fixed4 color         : COLOR;
                float2 texcoord      : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                half4  mask          : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float _UIMaskSoftnessX;
            float _UIMaskSoftnessY;

            sampler2D _SDFTex;
            float4 _SDFRect;
            float4 _SDFExtend;

            // Effect stack (composited 0..count-1, index 0 = back).
            float4 _FxColor[SDF_MAX_FX];
            float4 _FxParams[SDF_MAX_FX];
            float  _FxType[SDF_MAX_FX];
            int    _FxCount;

            // Straight-alpha "fg over bg".
            inline half4 Over(half4 bg, half4 fg)
            {
                float a = fg.a + bg.a * (1.0 - fg.a);
                float3 rgb = (fg.rgb * fg.a + bg.rgb * bg.a * (1.0 - fg.a)) / max(a, 1e-4);
                return half4(rgb, a);
            }

            // Disk blur of the sprite via a golden-angle "sunflower" sample pattern — points fill the
            // disk evenly (no visible rings), weighted by a Gaussian toward the centre so the falloff is
            // smooth and faint at the rim. Explicit LOD keeps it safe inside the effect loop. `r` is the
            // radius in UV (a fraction of the sprite). Blurs colour AND alpha, so the silhouette softens.
            #define SDF_BLUR_TAPS 64
            // Disk blur via a golden-angle "sunflower" pattern — 64 points fill the disk evenly (no
            // visible rings), Gaussian-weighted toward the centre. Dense enough to stay smooth without
            // per-pixel dithering (so no grain). `r` is the radius in UV (a fraction of the sprite).
            // Blurs colour AND alpha, so the silhouette softens too.
            inline half4 BlurSprite(float2 uv, float r)
            {
                const float golden = 2.39996323;   // radians (137.5 degrees)
                half4 acc = half4(0, 0, 0, 0);
                float wsum = 0.0;
                [loop]
                for (int i = 0; i < SDF_BLUR_TAPS; i++)
                {
                    float fi = (float)i + 0.5;
                    float t  = fi / (float)SDF_BLUR_TAPS;     // 0..1
                    float rr = sqrt(t);                       // even area coverage
                    float th = fi * golden;
                    float2 o = float2(cos(th), sin(th)) * (rr * r);
                    float w  = exp(-3.0 * rr * rr);           // Gaussian falloff (rr is already normalized)
                    acc  += (tex2Dlod(_MainTex, float4(uv + o, 0, 0)) + _TextureSampleAdd) * w;
                    wsum += w;
                }
                return acc / max(wsum, 1e-4);
            }

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = v.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = v.texcoord;

                float2 pixelSize = OUT.vertex.w;
                pixelSize /= float2(1, 1) * abs(mul((float2x2)UNITY_MATRIX_P, _ScreenParams.xy));

                float4 clampedRect = clamp(_ClipRect, -2e10, 2e10);
                OUT.mask = half4(v.vertex.xy * 2 - clampedRect.xy - clampedRect.zw,
                    0.25 / (0.25 * half2(_UIMaskSoftnessX, _UIMaskSoftnessY) + abs(pixelSize.xy)));

                OUT.color = v.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                float2 uv = IN.texcoord;
                half4 sprite = (tex2D(_MainTex, uv) + _TextureSampleAdd);
                half ia = IN.color.a;

            #ifdef SDFIMAGE_SDF
                float2 sdfUV = uv * _SDFRect.xy + _SDFRect.zw;
                // Normalized signed distance: 0 = edge, +1 = `spread` inside, -1 = `spread` outside.
                float t = (tex2D(_SDFTex, sdfUV).a - 0.5) * 2.0;
                float aa = max(fwidth(t), 1e-4);   // computed in uniform flow (outside the loop)

                half4 col = half4(0, 0, 0, 0);

                [loop]
                for (int i = 0; i < _FxCount; i++)
                {
                    int ty = (int)_FxType[i];
                    half4 c = _FxColor[i];
                    float4 p = _FxParams[i];
                    half4 layer = half4(0, 0, 0, 0);

                    if (ty == 0) // Face
                    {
                        if (p.x > 0.5)
                        {
                            // Silhouette: alpha from the field. dilate = p.y, softness = p.z.
                            float ft = t + p.y;
                            float cov = smoothstep(-(aa + p.z), (aa + p.z), ft);
                            layer = half4(sprite.rgb * c.rgb * IN.color.rgb, cov * c.a);
                        }
                        else
                        {
                            // Textured: the real sprite, masked to the silhouette so the expanded
                            // padding margin shows only effects (no smeared/clamped sprite there).
                            float fm = smoothstep(-aa, aa, t);
                            layer = half4(sprite.rgb * c.rgb * IN.color.rgb, sprite.a * c.a * fm);
                        }
                    }
                    else if (ty == 1) // Outline: width = p.x, softness = p.y
                    {
                        float oaa = aa + p.y;
                        float ocov = smoothstep(-oaa, oaa, t + p.x);
                        layer = half4(c.rgb, c.a * ocov);
                    }
                    else if (ty == 2) // Shadow: offset = p.xy, softness = p.z, dilate = p.w
                    {
                        float2 shUV = sdfUV - p.xy * _SDFRect.xy;
                        // tex2Dlod: explicit LOD avoids undefined derivatives inside the loop.
                        float st = (tex2Dlod(_SDFTex, float4(shUV, 0, 0)).a - 0.5) * 2.0 + p.w;
                        float saa = aa + p.z;
                        float scov = smoothstep(-saa, saa, st);
                        layer = half4(c.rgb, c.a * scov);
                    }
                    else if (ty == 3) // Glow: width = p.x, power = p.y, inner = p.z
                    {
                        // Extend the signed distance beyond the baked field so the glow can reach past
                        // the sprite rect. Inside the field (q == 0) `te == t`, so the near glow follows
                        // the real silhouette. Outside, the clamped field carries no shape, so we fall
                        // off RADIALLY from the sprite centre (elliptical, per-axis via _SDFExtend) —
                        // a soft round halo — instead of by distance-to-the-field-box, which would read
                        // as a square. We blend box->radial with how far outside the box we are so the
                        // two regions join smoothly.
                        float2 center  = 0.5 * _SDFRect.xy + _SDFRect.zw;          // sprite centre (SDF-UV)
                        float2 halfExt = 0.5 * abs(_SDFRect.xy) * _SDFExtend.xy;   // sprite half-size (spread units)
                        float2 rel     = (sdfUV - center) * _SDFExtend.xy;         // offset from centre (spread units)
                        float2 q       = max(0.0, max(-sdfUV, sdfUV - 1.0));
                        float boxD     = length(q * _SDFExtend.xy);                // distance outside the field box
                        float radD     = max(length(rel) - min(halfExt.x, halfExt.y), 0.0);
                        float ext      = lerp(boxD, max(boxD, radD),
                                              saturate(boxD / max(min(halfExt.x, halfExt.y), 1e-3)));
                        float te       = t - ext;
                        float gd       = saturate((te + p.x) / max(p.x + p.z, 1e-3));
                        float gcov     = pow(gd, max(p.y, 1e-3));
                        layer = half4(c.rgb, c.a * gcov);
                    }
                    else // Blur: radius = p.x, strength = p.y. Blurs the sprite (colour + alpha).
                    {
                        half4 blurred = BlurSprite(uv, p.x);
                        half4 mixed   = lerp(sprite, blurred, saturate(p.y));
                        layer = half4(mixed.rgb * c.rgb * IN.color.rgb, mixed.a * c.a);
                    }

                    layer.a *= ia;
                    col = Over(col, layer);
                }

                half4 color = col;
            #else
                half4 color = sprite * IN.color;
            #endif

            #ifdef UNITY_UI_CLIP_RECT
                half2 m = saturate((_ClipRect.zw - _ClipRect.xy - abs(IN.mask.xy)) * IN.mask.zw);
                color.a *= m.x * m.y;
            #endif

            #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
            #endif

                return color;
            }
        ENDCG
        }
    }

    Fallback "UI/Default"
}
