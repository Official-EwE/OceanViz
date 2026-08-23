Shader "Hidden/OceanViz3/BloomDepth"
{
    Properties
    {
        _BloomCutoffView0 ("Bloom Cutoff View 0", Range(0, 1)) = 0.01
        _BloomCutoffView1 ("Bloom Cutoff View 1", Range(0, 1)) = 0.01
        _BloomCutoffView2 ("Bloom Cutoff View 2", Range(0, 1)) = 0.01
        _BloomCutoffView3 ("Bloom Cutoff View 3", Range(0, 1)) = 0.01

        _BloomOffsetView0 ("Bloom Offset View 0", Float) = 0.47
        _BloomOffsetView1 ("Bloom Offset View 1", Float) = 0.47
        _BloomOffsetView2 ("Bloom Offset View 2", Float) = 0.47
        _BloomOffsetView3 ("Bloom Offset View 3", Float) = 0.47
        _BloomSampleCount ("Bloom Sample Count", Int) = 4

        _BloomWeightView0 ("Bloom Weight View 0", Float) = 1
        _BloomWeightView1 ("Bloom Weight View 1", Float) = 1
        _BloomWeightView2 ("Bloom Weight View 2", Float) = 1
        _BloomWeightView3 ("Bloom Weight View 3", Float) = 1

        _MaskView0 ("Mask View 0", Vector) = (0, 1, 0, 0)
        _MaskView1 ("Mask View 1", Vector) = (0, 0, 0, 0)
        _MaskView2 ("Mask View 2", Vector) = (0, 0, 0, 0)
        _MaskView3 ("Mask View 3", Vector) = (0, 0, 0, 0)

        _DepthBloomEnabled ("Depth Bloom Enabled", Range(0, 1)) = 1
        _DepthBloomNear ("Depth Bloom Near", Range(0, 1)) = 0
        _DepthBloomFar ("Depth Bloom Far", Range(0, 1)) = 1
        _DepthBloomWeightNear ("Depth Bloom Weight Near", Float) = 0
        _DepthBloomWeightFar ("Depth Bloom Weight Far", Float) = 0.25
        _DepthBloomExponent ("Depth Bloom Exponent", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "BloomDepth"
            ZTest Always
            ZWrite Off
            Cull Off
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float _BloomCutoffView0;
            float _BloomCutoffView1;
            float _BloomCutoffView2;
            float _BloomCutoffView3;
            float _BloomOffsetView0;
            float _BloomOffsetView1;
            float _BloomOffsetView2;
            float _BloomOffsetView3;
            int _BloomSampleCount;
            float _BloomWeightView0;
            float _BloomWeightView1;
            float _BloomWeightView2;
            float _BloomWeightView3;
            float4 _MaskView0;
            float4 _MaskView1;
            float4 _MaskView2;
            float4 _MaskView3;

            float _DepthBloomEnabled;
            float _DepthBloomNear;
            float _DepthBloomFar;
            float _DepthBloomWeightNear;
            float _DepthBloomWeightFar;
            float _DepthBloomExponent;

            float ViewMask(float x, float2 minMax)
            {
                float inMin = step(minMax.x, x);
                float inMax = step(x, minMax.y);
                return inMin * inMax;
            }

            void ResolveViewParams(float2 uv, out float2 viewMinMax, out float bloomCutoff, out float bloomOffset, out float bloomWeight)
            {
                float mask0 = ViewMask(uv.x, _MaskView0.xy);
                float mask1 = ViewMask(uv.x, _MaskView1.xy);
                float mask2 = ViewMask(uv.x, _MaskView2.xy);
                float mask3 = ViewMask(uv.x, _MaskView3.xy);

                if (mask0 > 0.5)
                {
                    viewMinMax = _MaskView0.xy;
                    bloomCutoff = _BloomCutoffView0;
                    bloomOffset = _BloomOffsetView0;
                    bloomWeight = _BloomWeightView0;
                    return;
                }

                if (mask1 > 0.5)
                {
                    viewMinMax = _MaskView1.xy;
                    bloomCutoff = _BloomCutoffView1;
                    bloomOffset = _BloomOffsetView1;
                    bloomWeight = _BloomWeightView1;
                    return;
                }

                if (mask2 > 0.5)
                {
                    viewMinMax = _MaskView2.xy;
                    bloomCutoff = _BloomCutoffView2;
                    bloomOffset = _BloomOffsetView2;
                    bloomWeight = _BloomWeightView2;
                    return;
                }

                if (mask3 > 0.5)
                {
                    viewMinMax = _MaskView3.xy;
                    bloomCutoff = _BloomCutoffView3;
                    bloomOffset = _BloomOffsetView3;
                    bloomWeight = _BloomWeightView3;
                    return;
                }

                viewMinMax = float2(0.0, 1.0);
                bloomCutoff = _BloomCutoffView0;
                bloomOffset = _BloomOffsetView0;
                bloomWeight = 0.0;
            }

            float ComputeDepthWeight(float2 uv)
            {
                float rawDepth = SampleSceneDepth(uv);
                float linearDepth = Linear01Depth(rawDepth, _ZBufferParams);

                float depthRange = max(1e-5, _DepthBloomFar - _DepthBloomNear);
                float depthT = saturate((linearDepth - _DepthBloomNear) / depthRange);
                float exponent = max(0.01, _DepthBloomExponent);
                depthT = pow(depthT, exponent);
                float depthWeight = lerp(_DepthBloomWeightNear, _DepthBloomWeightFar, depthT);

                return lerp(1.0, depthWeight, saturate(_DepthBloomEnabled));
            }

            float3 SampleSceneColor(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            }

            float2 ClampUvToView(float2 uv, float2 viewMinMax, float2 texelSize)
            {
                float minX = viewMinMax.x + (texelSize.x * 0.5);
                float maxX = viewMinMax.y - (texelSize.x * 0.5);

                if (maxX < minX)
                {
                    float centerX = (viewMinMax.x + viewMinMax.y) * 0.5;
                    minX = centerX;
                    maxX = centerX;
                }

                float minY = texelSize.y * 0.5;
                float maxY = 1.0 - (texelSize.y * 0.5);
                float clampedX = clamp(uv.x, minX, maxX);
                float clampedY = clamp(uv.y, minY, maxY);
                return float2(clampedX, clampedY);
            }

            float3 ExtractBloom(float2 uv, float2 offset, float bloomCutoff, float bloomWeight, float2 viewMinMax, float2 texelSize)
            {
                float2 sampleUv = ClampUvToView(uv + offset, viewMinMax, texelSize);
                float3 sceneColor = SampleSceneColor(sampleUv);
                float3 bright = saturate(sceneColor - bloomCutoff.xxx);
                float denominator = max(1e-5, 1.0 - bloomCutoff);
                bright = bright / denominator;
                return bright * bloomWeight;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                float2 texelSize = 1.0 / _ScreenSize.xy;
                float2 viewMinMax;
                float bloomCutoff;
                float bloomOffset;
                float bloomWeight;
                ResolveViewParams(uv, viewMinMax, bloomCutoff, bloomOffset, bloomWeight);

                if (bloomWeight <= 0.0)
                {
                    return float4(SampleSceneColor(uv), 1.0);
                }

                float2 baseOffset = texelSize * bloomOffset;
                int bloomSampleCount = _BloomSampleCount;
                if (bloomSampleCount < 1)
                {
                    bloomSampleCount = 1;
                }

                float3 sourceColor = SampleSceneColor(uv);
                float3 bloom = 0.0;

                [loop]
                for (int sampleIndex = 1; sampleIndex <= bloomSampleCount; sampleIndex++)
                {
                    float sampleMultiplier = (float)sampleIndex;
                    bloom += ExtractBloom(uv, float2(baseOffset.x * sampleMultiplier, 0.0), bloomCutoff, bloomWeight, viewMinMax, texelSize);
                    bloom += ExtractBloom(uv, float2(-baseOffset.x * sampleMultiplier, 0.0), bloomCutoff, bloomWeight, viewMinMax, texelSize);
                    bloom += ExtractBloom(uv, float2(0.0, baseOffset.y * sampleMultiplier), bloomCutoff, bloomWeight, viewMinMax, texelSize);
                    bloom += ExtractBloom(uv, float2(0.0, -baseOffset.y * sampleMultiplier), bloomCutoff, bloomWeight, viewMinMax, texelSize);
                }
                bloom *= 1.0 / (4.0 * bloomSampleCount);

                float depthWeight = ComputeDepthWeight(uv);

                float3 outputColor = sourceColor + (bloom * depthWeight);
                return float4(outputColor, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
