using UnityEngine;

/// <summary>
/// Generates tileable Perlin noise textures.
/// </summary>
public static class NoiseGenerator
{
    /// <summary>
    /// Generates a grayscale tileable noise texture using Perlin noise.
    /// </summary>
    /// <param name="width">Texture width.</param>
    /// <param name="height">Texture height.</param>
    /// <param name="offsetX">Global X offset for the noise pattern.</param>
    /// <param name="offsetY">Global Y offset for the noise pattern.</param>
    /// <param name="scale">Scale (frequency) of the noise. Lower values = larger features.</param>
    /// <returns>A Texture2D containing the generated Perlin noise.</returns>
    public static Texture2D GenerateNoiseTexture(int width, int height, float offsetX, float offsetY, float scale)
    {
        return GenerateNoiseTexture(width, height, offsetX, offsetY, scale, out float[] _);
    }

    /// <summary>
    /// Generates a grayscale tileable noise texture and a matching CPU sample array using Perlin noise.
    /// </summary>
    public static Texture2D GenerateNoiseTexture(int width, int height, float offsetX, float offsetY, float scale, out float[] samples)
    {
        return GenerateNoiseTexture(width, height, offsetX, offsetY, scale, false, out samples);
    }

    /// <summary>
    /// Generates a grayscale tileable noise texture and a matching CPU sample array using Perlin noise.
    /// When balanced is true, the samples are recentered so their average is 0.5.
    /// </summary>
    public static Texture2D GenerateNoiseTexture(int width, int height, float offsetX, float offsetY, float scale, bool balanced, out float[] samples)
    {
        Debug.Assert(width > 0, "Noise texture width must be positive.");
        Debug.Assert(height > 0, "Noise texture height must be positive.");
        Debug.Assert(scale > 0.0f, "Noise texture scale must be positive.");

        if (scale <= 0)
        {
            scale = 0.0001f;
        }

        Debug.Assert(SystemInfo.SupportsTextureFormat(TextureFormat.RHalf), "Water current noise requires RHalf texture support.");

        Texture2D noiseTexture = new Texture2D(width, height, TextureFormat.RHalf, false, true);
        noiseTexture.filterMode = FilterMode.Bilinear;
        noiseTexture.wrapMode = TextureWrapMode.Repeat;

        Color[] pixels = new Color[width * height];
        samples = new float[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float noiseValue = SampleTileablePerlinNoise(x, y, width, height, offsetX, offsetY, scale);
                int index = y * width + x;
                samples[index] = noiseValue;
            }
        }

        if (balanced)
        {
            BalanceSamplesAroundHalf(samples);
        }

        for (int i = 0; i < samples.Length; i++)
        {
            float noiseValue = Mathf.Clamp01(samples[i]);
            samples[i] = noiseValue;
            pixels[i] = new Color(noiseValue, noiseValue, noiseValue, 1.0f);
        }

        noiseTexture.SetPixels(pixels);
        noiseTexture.Apply(false);

        return noiseTexture;
    }

    private static float SampleTileablePerlinNoise(int x, int y, int width, int height, float offsetX, float offsetY, float scale)
    {
        float sampleX = (x + offsetX) / scale;
        float sampleY = (y + offsetY) / scale;
        float periodX = width / scale;
        float periodY = height / scale;
        float blendX = x / (float)width;
        float blendY = y / (float)height;

        float bottomLeft = Mathf.PerlinNoise(sampleX, sampleY);
        float bottomRight = Mathf.PerlinNoise(sampleX - periodX, sampleY);
        float topLeft = Mathf.PerlinNoise(sampleX, sampleY - periodY);
        float topRight = Mathf.PerlinNoise(sampleX - periodX, sampleY - periodY);

        float bottom = Mathf.Lerp(bottomLeft, bottomRight, blendX);
        float top = Mathf.Lerp(topLeft, topRight, blendX);
        return Mathf.Lerp(bottom, top, blendY);
    }

    private static void BalanceSamplesAroundHalf(float[] samples)
    {
        Debug.Assert(samples != null, "Noise samples cannot be null.");
        Debug.Assert(samples.Length > 0, "Noise samples cannot be empty.");

        float lowOffset = -1.0f;
        float highOffset = 1.0f;

        for (int iteration = 0; iteration < 32; iteration++)
        {
            float offset = (lowOffset + highOffset) * 0.5f;
            float mean = CalculateClampedMean(samples, offset);
            if (mean < 0.5f)
            {
                lowOffset = offset;
            }
            else
            {
                highOffset = offset;
            }
        }

        float finalOffset = (lowOffset + highOffset) * 0.5f;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Mathf.Clamp01(samples[i] + finalOffset);
        }
    }

    private static float CalculateClampedMean(float[] samples, float offset)
    {
        float sum = 0.0f;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += Mathf.Clamp01(samples[i] + offset);
        }

        return sum / samples.Length;
    }
}
