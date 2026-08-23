using System;

namespace OceanViz3.Benchmarking
{
    /// <summary>
    /// Provides fixed dynamic boid presets for CPU benchmarks so benchmark behavior does not drift with StreamingAssets JSON.
    /// </summary>
    public static class FixedBenchmarkDynamicPresets
    {
        public static bool TryCreate(string presetName, out DynamicEntityPreset preset)
        {
            if (string.Equals(presetName, "Sea Bass", StringComparison.Ordinal))
            {
                preset = CreateSeaBass();
                return true;
            }

            if (string.Equals(presetName, "Atlantic Blue Marlin", StringComparison.Ordinal))
            {
                preset = CreateAtlanticBlueMarlin();
                return true;
            }

            if (string.Equals(presetName, "Green Crab", StringComparison.Ordinal))
            {
                preset = CreateGreenCrab();
                return true;
            }

            preset = null;
            return false;
        }

        private static DynamicEntityPreset CreateSeaBass()
        {
            DynamicEntityPreset preset = new DynamicEntityPreset();
            preset.name = "Sea Bass";
            preset.maxPopulation = 2000;
            preset.habitats = new string[] { "Coastal Zone" };
            preset.seabed_bound = false;
            preset.predator = false;
            preset.prey = true;
            preset.cell_radius = 4.0f;
            preset.state_transition_speed = 3.0f;
            preset.state_change_timer_min = 2.0f;
            preset.state_change_timer_max = 4.0f;
            preset.separation_weight = 0.01f;
            preset.alignment_weight = 0.6f;
            preset.target_weight = 0.3f;
            preset.obstacle_aversion_distance = 2.0f;
            preset.max_vertical_angle = 30.0f;
            preset.max_turn_rate = 1.0f;
            preset.move_speed = 0.6f;
            preset.scale_min = 0.45f;
            preset.scale_max = 1.85f;
            preset.speed_modifier_min = 0.3f;
            preset.speed_modifier_max = 3.0f;
            preset.positive_y_clip = 1.0f;
            preset.negative_y_clip = 0.55f;
            preset.animation_speed = 7.0f;
            preset.sine_wavelength = 0.2f;
            preset.sine_deformation_amplitude = CreateVector(0.06f, 0.0f, 0.0f);
            preset.secondary1_animation_amplitude = 0.5f;
            preset.invert_secondary1_animation = 1.0f;
            preset.secondary2_animation_amplitude = CreateVector(0.05f, 0.0f, 0.0f);
            preset.invert_secondary2_animation = -1.0f;
            preset.side_to_side_amplitude = CreateVector(0.005f, 0.0f, 0.0f);
            preset.yaw_amplitude = CreateVector(0.0f, 0.0f, 0.03f);
            preset.rolling_spine_amplitude = CreateVector(0.0f, 0.05f, 0.0f);
            preset.bone_animated = false;
            preset.spawn_clustering = 0.8f;
            return preset;
        }

        private static DynamicEntityPreset CreateAtlanticBlueMarlin()
        {
            DynamicEntityPreset preset = new DynamicEntityPreset();
            preset.name = "Atlantic Blue Marlin";
            preset.maxPopulation = 20;
            preset.habitats = new string[] { "Coastal Zone" };
            preset.seabed_bound = false;
            preset.predator = true;
            preset.prey = false;
            preset.cell_radius = 8.0f;
            preset.state_transition_speed = 0.5f;
            preset.state_change_timer_min = 1.0f;
            preset.state_change_timer_max = 10.0f;
            preset.separation_weight = 2.5f;
            preset.alignment_weight = 0.0f;
            preset.target_weight = 2.0f;
            preset.obstacle_aversion_distance = 2.0f;
            preset.max_vertical_angle = 30.0f;
            preset.max_turn_rate = 1.0f;
            preset.move_speed = 3.0f;
            preset.scale_min = 0.45f;
            preset.scale_max = 1.85f;
            preset.speed_modifier_min = 0.5f;
            preset.speed_modifier_max = 1.5f;
            preset.positive_y_clip = 1.0f;
            preset.negative_y_clip = 0.362f;
            preset.animation_speed = 5.0f;
            preset.sine_wavelength = 1.5f;
            preset.sine_deformation_amplitude = CreateVector(0.6f, 0.0f, 0.0f);
            preset.secondary1_animation_amplitude = 0.5f;
            preset.invert_secondary1_animation = -1.0f;
            preset.secondary2_animation_amplitude = CreateVector(0.7f, 0.0f, 0.2f);
            preset.invert_secondary2_animation = -1.0f;
            preset.side_to_side_amplitude = CreateVector(0.02f, 0.0f, 0.0f);
            preset.yaw_amplitude = CreateVector(0.0f, 0.0f, 0.025f);
            preset.rolling_spine_amplitude = CreateVector(0.0f, 0.04f, 0.0f);
            preset.bone_animated = false;
            preset.spawn_clustering = 0.8f;
            return preset;
        }

        private static DynamicEntityPreset CreateGreenCrab()
        {
            DynamicEntityPreset preset = new DynamicEntityPreset();
            preset.name = "Green Crab";
            preset.maxPopulation = 100;
            preset.habitats = new string[] { "Coastal Zone" };
            preset.seabed_bound = true;
            preset.predator = false;
            preset.prey = false;
            preset.cell_radius = 1.0f;
            preset.separation_weight = 1.0f;
            preset.alignment_weight = 0.0f;
            preset.target_weight = 0.01f;
            preset.obstacle_aversion_distance = 0.01f;
            preset.max_vertical_angle = 30.0f;
            preset.max_turn_rate = 1.0f;
            preset.move_speed = 0.3f;
            preset.scale_min = 0.45f;
            preset.scale_max = 1.85f;
            preset.speed_modifier_min = 0.5f;
            preset.speed_modifier_max = 1.5f;
            preset.positive_y_clip = 1.0f;
            preset.negative_y_clip = 0.55f;
            preset.animation_speed = 8.0f;
            preset.sine_wavelength = 0.03f;
            preset.sine_deformation_amplitude = CreateVector(0.0f, 0.015f, 0.0f);
            preset.secondary1_animation_amplitude = 0.0f;
            preset.invert_secondary1_animation = 1.0f;
            preset.secondary2_animation_amplitude = CreateVector(0.0f, 0.08f, 0.0f);
            preset.invert_secondary2_animation = 1.0f;
            preset.side_to_side_amplitude = CreateVector(0.0f, 0.0f, 0.005f);
            preset.yaw_amplitude = CreateVector(0.02f, 0.0f, 0.02f);
            preset.rolling_spine_amplitude = CreateVector(0.0f, 0.0f, 0.04f);
            preset.bone_animated = false;
            preset.spawn_clustering = 0.8f;
            return preset;
        }

        private static Vector3Data CreateVector(float x, float y, float z)
        {
            Vector3Data vector = new Vector3Data();
            vector.x = x;
            vector.y = y;
            vector.z = z;
            return vector;
        }
    }
}
