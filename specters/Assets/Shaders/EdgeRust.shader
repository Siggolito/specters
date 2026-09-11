// Rust that collects on the edges of an object and fades inward.
//
// The rust layer is triplanar-projected in object space, so it crosses face
// boundaries and wraps around corners with no UV seams. Its opacity comes from the
// distance to the nearest edge of the mesh's bounding box: full strength right on an
// edge, fading to nothing toward the middle of each face.
//
// The bounds come from the renderer itself (unity_RendererBounds_*), so this works with
// no setup at all. EdgeRustBounds.cs is optional and only needed to override them.

Shader "Specters/EdgeRust"
{
    Properties
    {
        // The base layer is the material's own surface settings - no albedo texture.
        // Convert an existing URP/Lit material to this shader and its colour, metallic
        // and smoothness carry straight over.
        [Header(Base Surface)]
        _BaseColor          ("Base Color", Color) = (0.32, 0.38, 0.42, 1)
        _Metallic           ("Metallic", Range(0,1)) = 0.0
        _Smoothness         ("Smoothness", Range(0,1)) = 0.5
        [Normal] _BumpMap   ("Normal Map (optional)", 2D) = "bump" {}
        _BumpScale          ("Normal Scale", Float) = 1.0

        [Header(Rust Layer)]
        _RustMap            ("Rust Map (seamless)", 2D) = "white" {}
        _RustColor          ("Rust Tint", Color) = (1, 1, 1, 1)
        _RustTiling         ("Rust Tiling (tiles per metre)", Float) = 0.5
        _RustMetallic       ("Rust Metallic", Range(0,1)) = 0.1
        _RustSmoothness     ("Rust Smoothness", Range(0,1)) = 0.12
        _TriplanarSharpness ("Triplanar Sharpness", Range(1,16)) = 6

        [Header(Edge Falloff)]
        _EdgeWidth          ("Edge Width (fraction of median extent)", Range(0.01, 1)) = 0.3
        _EdgeFalloff        ("Edge Falloff Power", Range(0.1, 8)) = 2.2
        _RustStrength       ("Rust Strength", Range(0,1)) = 1.0
        _EdgeNoise          ("Edge Noise Breakup", Range(0,1)) = 0.45
        _EdgeNoiseTiling    ("Edge Noise Tiling", Float) = 1.7

        // Left at zero the shader reads the renderer's own bounds and needs no setup.
        // EdgeRustBounds.cs can override these for rotated meshes or manual tweaking.
        [Header(Bounds   leave zero for automatic)]
        _BoundsCenter       ("Bounds Center Override", Vector) = (0,0,0,0)
        _BoundsExtents      ("Bounds Extents Override", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "Queue" = "Geometry"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _BumpMap_ST;
            half4  _BaseColor;
            half   _Metallic;
            half   _Smoothness;
            half   _BumpScale;

            half4  _RustColor;
            float  _RustTiling;
            half   _RustMetallic;
            half   _RustSmoothness;
            half   _TriplanarSharpness;

            float  _EdgeWidth;
            half   _EdgeFalloff;
            half   _RustStrength;
            half   _EdgeNoise;
            float  _EdgeNoiseTiling;

            float4 _BoundsCenter;
            float4 _BoundsExtents;
        CBUFFER_END

        TEXTURE2D(_BumpMap);   SAMPLER(sampler_BumpMap);
        TEXTURE2D(_RustMap);   SAMPLER(sampler_RustMap);

        // Weights for projecting a texture down each of the three axes. Higher
        // sharpness narrows the cross-fade band where two projections overlap.
        float3 TriplanarWeights(float3 n)
        {
            float3 b = pow(abs(n), _TriplanarSharpness);
            return b / max(dot(b, float3(1, 1, 1)), 1e-4);
        }

        half4 SampleTriplanar(TEXTURE2D_PARAM(tex, samp), float3 posOS, float3 w, float tiling)
        {
            float3 p = posOS * tiling;
            half4 x = SAMPLE_TEXTURE2D(tex, samp, p.zy);
            half4 y = SAMPLE_TEXTURE2D(tex, samp, p.xz);
            half4 z = SAMPLE_TEXTURE2D(tex, samp, p.xy);
            return x * w.x + y * w.y + z * w.z;
        }

        // Where the object's edges are, in the object's own frame.
        //
        // Unity fills unity_RendererBounds_* per draw, so this needs no helper component
        // in the common case. Those bounds are a WORLD AABB; converting the two corners
        // back to object space is exact for an axis-aligned mesh and merely conservative
        // for a rotated one. Supply the override properties (EdgeRustBounds.cs does) if
        // that ever matters.
        void GetObjectBounds(out float3 center, out float3 extents)
        {
            if (all(_BoundsExtents.xyz > 0.0))
            {
                center  = _BoundsCenter.xyz;
                extents = _BoundsExtents.xyz;
                return;
            }

            float3 a = TransformWorldToObject(unity_RendererBounds_Min.xyz);
            float3 b = TransformWorldToObject(unity_RendererBounds_Max.xyz);
            float3 lo = min(a, b);
            float3 hi = max(a, b);
            center  = (hi + lo) * 0.5;
            extents = (hi - lo) * 0.5;
        }

        // The object's world scale, read off the object-to-world matrix as the length of
        // each of its three basis vectors.
        float3 GetObjectScale()
        {
            float4x4 m = GetObjectToWorldMatrix();
            return max(float3(length(m._m00_m10_m20),
                              length(m._m01_m11_m21),
                              length(m._m02_m12_m22)), 1e-5);
        }

        // The space the rust lives in: centred on the object, rotating and travelling
        // with it, but measured in WORLD units.
        //
        // Measuring in raw object space instead is what breaks on any scaled model. These
        // containers carry scales near 170 on two axes and 400 on the third, so their
        // object-space half-extents come out around 0.01 and wildly anisotropic - the
        // rust texture stretches roughly 40:1 and the edge band shrinks to nothing.
        // Folding the scale back in makes one metre mean one metre on every object, so
        // tiling and edge width finally read the same on a scaled prop and an unscaled
        // one. Staying in the object's frame (rather than going fully world-space) keeps
        // the pattern pinned to the surface, so a moving object does not swim through it.
        void GetRustSpace(float3 posOS, out float3 p, out float3 extents)
        {
            float3 center, extentsOS;
            GetObjectBounds(center, extentsOS);

            float3 scale = GetObjectScale();
            p       = (posOS - center) * scale;
            extents = max(abs(extentsOS) * scale, 1e-5);
        }

        // A normal transforms by the inverse scale, so the axis that dominates in rust
        // space is not necessarily the one that dominates in object space.
        float3 RustSpaceNormal(float3 normalOS)
        {
            return SafeNormalize(normalOS / GetObjectScale());
        }

        // The reference length the edge band is measured against: the MIDDLE of the three
        // half-extents, not the smallest. On a flat plate - the sign letters in this scene
        // are plates whose x-extent is nearly zero - the smallest extent is ~0, and a
        // width derived from it collapses and takes the rust with it. The median is the
        // plate's short in-plane dimension, which is the size the band actually wants.
        float RefExtent(float3 extents)
        {
            float lo  = min(extents.x, min(extents.y, extents.z));
            float hi  = max(extents.x, max(extents.y, extents.z));
            float mid = extents.x + extents.y + extents.z - lo - hi;
            return max(mid, 1e-4);
        }

        // Distance from a surface point to the nearest edge of the bounding box. Takes a
        // point already centred on the bounds, as GetRustSpace hands back.
        //
        // Per axis, d is how far the point sits from that pair of faces. On a surface
        // point the smallest d is ~0 (the face it lies on) and the largest is the axis
        // it is most interior to, which leaves the MIDDLE value as the distance to the
        // closest edge of that face. That is the field we fade the rust along.
        //
        // This deliberately reads no normal. Excluding the surface's own axis via the
        // triplanar weights looks more correct and is worse in practice: on corrugated
        // panels like this container's walls the rib normals sit near 45 degrees, so the
        // weights split across two axes, the exclusion leaks into both, and every distance
        // inflates until the mask dies everywhere. The median needs no such guess.
        //
        // Its one cost is that a recessed surface never reaches d=0 on its normal axis -
        // these walls sit ~0.6m inside the box staked out by the corner posts, so the
        // field floors near 0.6m across the wall. _EdgeWidth has to stay comfortably
        // wider than that inset for the wall to take any rust at all; at 1.0 (a full
        // median half-extent, ~1.68m here) the rim lands around 0.6 strength and still
        // falls to zero by mid-panel.
        float DistanceToNearestEdge(float3 p, float3 extents)
        {
            float3 d = max(extents - abs(p), 0.0);
            float dMin = min(d.x, min(d.y, d.z));
            float dMax = max(d.x, max(d.y, d.z));
            return d.x + d.y + d.z - dMin - dMax;
        }

        // 0 in the middle of a face, 1 hard on an edge.
        half EdgeMask(float3 p, float3 triW, float3 extents)
        {
            // Width is a fraction of the object's median half-extent rather than an
            // absolute distance, so the look survives whatever scale the model came in at.
            float width = max(_EdgeWidth * RefExtent(extents), 1e-4);

            half m = 1.0h - saturate(DistanceToNearestEdge(p, extents) / width);
            m = pow(m, max(_EdgeFalloff, 0.01h));

            // Break the band up so it does not read as a perfect ribbon. Reusing the
            // rust texture's own luminance avoids needing a second noise map.
            if (_EdgeNoise > 0.0h)
            {
                half n = SampleTriplanar(TEXTURE2D_ARGS(_RustMap, sampler_RustMap),
                                         p, triW, _EdgeNoiseTiling).g;
                m *= lerp(1.0h, saturate(n * 1.8h), _EdgeNoise);
            }

            return saturate(m * _RustStrength);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex   ForwardVert
            #pragma fragment ForwardFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                half3  normalWS   : TEXCOORD3;
                half4  tangentWS  : TEXCOORD4;
                half3  normalOS   : TEXCOORD5;
                half   fogFactor  : TEXCOORD6;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 7);
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ForwardVert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   nrm = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.positionOS = IN.positionOS.xyz;
                OUT.normalWS   = nrm.normalWS;
                OUT.tangentWS  = half4(nrm.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.normalOS   = normalize(IN.normalOS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BumpMap);
                OUT.fogFactor  = ComputeFogFactor(pos.positionCS.z);

                OUTPUT_LIGHTMAP_UV(IN.lightmapUV, unity_LightmapST, OUT.lightmapUV);
                OUTPUT_SH(nrm.normalWS, OUT.vertexSH);
                return OUT;
            }

            half4 ForwardFrag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // object frame, world units - see GetRustSpace
                float3 rustP, rustExtents;
                GetRustSpace(IN.positionOS, rustP, rustExtents);

                float3 triW = TriplanarWeights(RustSpaceNormal(IN.normalOS));

                // base surface comes straight from the material's own settings
                half3 albedo     = _BaseColor.rgb;
                half  metallic   = _Metallic;
                half  smoothness = _Smoothness;

                // rust, projected down all three axes so it never shows a seam
                half3 rust = SampleTriplanar(TEXTURE2D_ARGS(_RustMap, sampler_RustMap),
                                             rustP, triW, _RustTiling).rgb * _RustColor.rgb;

                half mask = EdgeMask(rustP, triW, rustExtents);

                albedo     = lerp(albedo, rust, mask);
                metallic   = lerp(metallic, _RustMetallic, mask);
                smoothness = lerp(smoothness, _RustSmoothness, mask);

                // normal
                half3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);

                half3 bitangent = IN.tangentWS.w * cross(IN.normalWS, IN.tangentWS.xyz);
                half3x3 tbn = half3x3(IN.tangentWS.xyz, bitangent, IN.normalWS);
                half3 normalWS = normalize(mul(normalTS, tbn));

                InputData inputData = (InputData)0;
                inputData.positionWS      = IN.positionWS;
                inputData.normalWS        = normalWS;
                inputData.viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(IN.positionWS));
                inputData.shadowCoord     = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord        = IN.fogFactor;
                inputData.vertexLighting  = half3(0, 0, 0);
                inputData.bakedGI         = SAMPLE_GI(IN.lightmapUV, IN.vertexSH, normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                inputData.shadowMask      = SAMPLE_SHADOWMASK(IN.lightmapUV);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo     = albedo;
                surfaceData.metallic   = metallic;
                surfaceData.smoothness = smoothness;
                surfaceData.normalTS   = normalTS;
                surfaceData.occlusion  = 1.0h;
                surfaceData.alpha      = 1.0h;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = 1.0h;
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings ShadowVert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);

                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDir = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDir = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDir));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                OUT.positionCS = positionCS;
                return OUT;
            }

            half4 ShadowFrag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthVert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 DepthFrag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex   DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half3  normalWS   : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 DepthNormalsFrag(Varyings IN) : SV_Target
            {
                return half4(NormalizeNormalPerPixel(IN.normalWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
