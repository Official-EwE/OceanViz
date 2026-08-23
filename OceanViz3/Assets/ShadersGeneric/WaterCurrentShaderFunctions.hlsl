TEXTURE2D(_WaterCurrentNoiseTexture);
SAMPLER(sampler_WaterCurrentNoiseTexture);

float4 _WaterCurrentParams;
float4 _WaterCurrentDirection;
float4 _WaterCurrentStaticParams;
float4 _WaterCurrentSecondaryParams;
float _WaterCurrentTime;

float WaterCurrentHash13(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

float WaterCurrentValueNoise3D(float3 p)
{
    float3 cell = floor(p);
    float3 local = frac(p);
    float3 fade = local * local * local * (local * (local * 6.0 - 15.0) + 10.0);

    float n000 = WaterCurrentHash13(cell + float3(0.0, 0.0, 0.0));
    float n100 = WaterCurrentHash13(cell + float3(1.0, 0.0, 0.0));
    float n010 = WaterCurrentHash13(cell + float3(0.0, 1.0, 0.0));
    float n110 = WaterCurrentHash13(cell + float3(1.0, 1.0, 0.0));
    float n001 = WaterCurrentHash13(cell + float3(0.0, 0.0, 1.0));
    float n101 = WaterCurrentHash13(cell + float3(1.0, 0.0, 1.0));
    float n011 = WaterCurrentHash13(cell + float3(0.0, 1.0, 1.0));
    float n111 = WaterCurrentHash13(cell + float3(1.0, 1.0, 1.0));

    float nx00 = lerp(n000, n100, fade.x);
    float nx10 = lerp(n010, n110, fade.x);
    float nx01 = lerp(n001, n101, fade.x);
    float nx11 = lerp(n011, n111, fade.x);
    float nxy0 = lerp(nx00, nx10, fade.y);
    float nxy1 = lerp(nx01, nx11, fade.y);
    return lerp(nxy0, nxy1, fade.z);
}

void SampleWaterCurrentNoise_float(float3 WorldPosition, out float Noise)
{
    float noiseScale = max(_WaterCurrentParams.x, 0.0001);
    float noiseSpeed = max(_WaterCurrentParams.y, 0.0);
    float2 direction = _WaterCurrentDirection.xz;
    float directionLengthSquared = dot(direction, direction);
    if (directionLengthSquared <= 0.000001)
    {
        direction = float2(1.0, 0.0);
    }
    else
    {
        direction *= rsqrt(directionLengthSquared);
    }

    float2 uv = WorldPosition.xz / noiseScale + direction * _WaterCurrentTime * noiseSpeed;
    float staticStrength = max(_WaterCurrentParams.z, 0.0);
    float enabled = step(0.5, _WaterCurrentParams.w);
    float noiseValue = SAMPLE_TEXTURE2D_LOD(_WaterCurrentNoiseTexture, sampler_WaterCurrentNoiseTexture, frac(uv), 0.0).r;
    Noise = 0.5 + (noiseValue - 0.5) * staticStrength * enabled;
}

void SampleWaterCurrentObjectOffset_float(float3 WorldPosition, float ObjectPositionY, float SecondaryMask, out float3 Offset)
{
    float noiseScale = max(_WaterCurrentParams.x, 0.0001);
    float noiseSpeed = max(_WaterCurrentParams.y, 0.0);
    float staticStrength = max(_WaterCurrentParams.z, 0.0);
    float enabled = step(0.5, _WaterCurrentParams.w);
    float meshMinY = _WaterCurrentMeshBoundsY.x;
    float meshHeight = max(_WaterCurrentMeshBoundsY.y, 0.0001);
    float rigidityInfluence = saturate(_WaterCurrentMeshBoundsY.z);
    float meshLargestDimension = max(_WaterCurrentMeshBoundsY.w, meshHeight);
    float heightInfluence = saturate((ObjectPositionY - meshMinY) / meshHeight);
    float longPlantDamping = max(_WaterCurrentStaticParams.x, 0.0);
    float longPlantReferenceHeight = max(_WaterCurrentStaticParams.y, 0.0001);
    float objectScaleX = length(TransformObjectToWorldDir(float3(1.0, 0.0, 0.0), false));
    float objectScaleY = length(TransformObjectToWorldDir(float3(0.0, 1.0, 0.0), false));
    float objectScaleZ = length(TransformObjectToWorldDir(float3(0.0, 0.0, 1.0), false));
    float objectLargestScale = max(objectScaleX, max(objectScaleY, objectScaleZ));
    float worldLargestDimension = meshLargestDimension * max(objectLargestScale, 0.0001);
    float smallPlantSizeInfluence = saturate(worldLargestDimension / longPlantReferenceHeight);
    float extraSize = max(worldLargestDimension - longPlantReferenceHeight, 0.0);
    float meshLengthInfluence = smallPlantSizeInfluence * rcp(1.0 + extraSize * longPlantDamping);

    float2 direction = _WaterCurrentDirection.xz;
    float directionLengthSquared = dot(direction, direction);
    if (directionLengthSquared <= 0.000001)
    {
        direction = float2(1.0, 0.0);
    }
    else
    {
        direction *= rsqrt(directionLengthSquared);
    }

    float2 uv = WorldPosition.xz / noiseScale + direction * _WaterCurrentTime * noiseSpeed;
    float noiseValue = SAMPLE_TEXTURE2D_LOD(_WaterCurrentNoiseTexture, sampler_WaterCurrentNoiseTexture, frac(uv), 0.0).r;
    float signedForce = (noiseValue - 0.5) * 2.0;
    float secondaryNoiseScale = max(_WaterCurrentSecondaryParams.x, 0.0001);
    float secondaryNoiseSpeed = max(_WaterCurrentSecondaryParams.y, 0.0);
    float secondaryNoiseStrength = max(_WaterCurrentSecondaryParams.z, 0.0);
    float secondaryNoiseSeed = _WaterCurrentSecondaryParams.w;
    float3 secondarySeedOffset = float3(
        secondaryNoiseSeed * 0.754877666,
        secondaryNoiseSeed * 0.569840296,
        secondaryNoiseSeed * 0.411840296);
    float3 secondaryScrollDirection = normalize(float3(direction.x, 0.37, direction.y));
    float3 secondaryNoisePosition = (WorldPosition / secondaryNoiseScale) + secondaryScrollDirection * _WaterCurrentTime * secondaryNoiseSpeed + secondarySeedOffset;
    float secondaryNoiseValue = WaterCurrentValueNoise3D(secondaryNoisePosition);
    float secondarySignedForce = (secondaryNoiseValue - 0.5) * 2.0 * saturate(SecondaryMask);
    float combinedSignedForce = signedForce + secondarySignedForce * secondaryNoiseStrength;
    float3 worldOffset = float3(direction.x, 0.0, direction.y) * combinedSignedForce * staticStrength * rigidityInfluence * heightInfluence * meshLengthInfluence * enabled;
    Offset = TransformWorldToObjectDir(worldOffset, false);
}
