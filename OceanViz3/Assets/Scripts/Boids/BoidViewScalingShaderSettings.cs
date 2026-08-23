using UnityEngine;

namespace OceanViz3
{
    /// <summary>
    /// Centralizes view-slice scaling shader properties so all entity material paths stay in sync.
    /// </summary>
    public static class BoidViewScalingShaderSettings
    {
        public static readonly Vector4 DefaultViewScaleMultipliers = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        public const float ViewScaleBlendWidth = 0.035f;

        public const string ViewScaleMultipliersPropertyName = "_ViewScaleMultipliers";
        public const string ViewScaleBlendWidthPropertyName = "_ViewScaleBlendWidth";

        public static void ApplyTo(Material material)
        {
            ApplyTo(material, DefaultViewScaleMultipliers);
        }

        public static void ApplyTo(Material material, Vector4 viewScaleMultipliers)
        {
            Debug.Assert(material != null, "Boid view scaling requires a valid material.");
            Debug.Assert(material.HasProperty(ViewScaleMultipliersPropertyName),
                "Boid material is missing " + ViewScaleMultipliersPropertyName + ".");
            Debug.Assert(material.HasProperty(ViewScaleBlendWidthPropertyName),
                "Boid material is missing " + ViewScaleBlendWidthPropertyName + ".");

            material.SetVector(ViewScaleMultipliersPropertyName, viewScaleMultipliers);
            material.SetFloat(ViewScaleBlendWidthPropertyName, ViewScaleBlendWidth);
        }
    }
}
