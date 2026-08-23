float ViewSliceSigmoid(float x, float center, float width)
{
    float safeWidth = max(width, 0.0001);
    float normalized = (x - center) / safeWidth;
    return 1.0 / (1.0 + exp(-normalized * 6.0));
}

float ViewSliceWeight(float screenX, float start, float end, float blendWidth, float active)
{
    if (active < 0.5)
    {
        return 0.0;
    }

    float enterWeight = 1.0;
    if (start > 0.0001)
    {
        enterWeight = ViewSliceSigmoid(screenX, start, blendWidth);
    }

    float exitWeight = 0.0;
    if (end < 0.9999)
    {
        exitWeight = ViewSliceSigmoid(screenX, end, blendWidth);
    }

    return saturate(enterWeight * (1.0 - exitWeight));
}

void ApplyViewSliceScale_float(
    float3 Position,
    float4 ScreenDisplayStart,
    float4 ScreenDisplayEnd,
    float4 ViewScaleMultipliers,
    float ViewScaleBlendWidth,
    out float3 ScaledPosition)
{
    float4 clipPosition = TransformObjectToHClip(Position);
    float clipW = max(abs(clipPosition.w), 0.00001);
    float screenX = clipPosition.x / clipW * 0.5 + 0.5;
    float blendWidth = max(ViewScaleBlendWidth, 0.0001);
    float slicePadding = max(blendWidth * 1.5, 0.04);

    float start0 = ScreenDisplayStart.x - slicePadding;
    float start1 = ScreenDisplayStart.y - slicePadding;
    float start2 = ScreenDisplayStart.z - slicePadding;
    float start3 = ScreenDisplayStart.w - slicePadding;
    float end0 = ScreenDisplayEnd.x + slicePadding;
    float end1 = ScreenDisplayEnd.y + slicePadding;
    float end2 = ScreenDisplayEnd.z + slicePadding;
    float end3 = ScreenDisplayEnd.w + slicePadding;

    float active0 = step(0.00001, ScreenDisplayEnd.x - ScreenDisplayStart.x);
    float active1 = step(0.00001, ScreenDisplayEnd.y - ScreenDisplayStart.y);
    float active2 = step(0.00001, ScreenDisplayEnd.z - ScreenDisplayStart.z);
    float active3 = step(0.00001, ScreenDisplayEnd.w - ScreenDisplayStart.w);

    float weight0 = ViewSliceWeight(screenX, start0, end0, blendWidth, active0);
    float weight1 = ViewSliceWeight(screenX, start1, end1, blendWidth, active1);
    float weight2 = ViewSliceWeight(screenX, start2, end2, blendWidth, active2);
    float weight3 = ViewSliceWeight(screenX, start3, end3, blendWidth, active3);

    float totalWeight = weight0 + weight1 + weight2 + weight3;
    float scale = 1.0;
    if (totalWeight > 0.00001)
    {
        scale = dot(float4(weight0, weight1, weight2, weight3), ViewScaleMultipliers) / totalWeight;
    }

    ScaledPosition = Position * scale;
}
