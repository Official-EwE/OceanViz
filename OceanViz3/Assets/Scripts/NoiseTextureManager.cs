using System.Collections.Generic;
using Unity.Assertions;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace OceanViz3
{
    public struct WaterCurrentNoiseBlob
    {
        public int Size;
        public BlobArray<float> Samples;
    }

    public struct WaterCurrentSettings : IComponentData
    {
        public int Enabled;
        public int NoiseTextureSize;
        public float NoiseScale;
        public float NoiseSpeed;
        public float CurrentStrengthMultiplier;
        public float MaxAffectedBoidSize;
        public float2 HorizontalDirection;
        public BlobAssetReference<WaterCurrentNoiseBlob> NoiseBlob;
    }

    public static class WaterCurrentUtility
    {
        public static float3 SampleHorizontalCurrentVelocity(
            in WaterCurrentSettings settings,
            float3 worldPosition,
            float3 localScale,
            float elapsedTime,
            float defaultMoveSpeed,
            float meshLargestDimension,
            float scaleMin,
            float waterCurrentInfluence,
            bool predator)
        {
            if (settings.Enabled == 0)
            {
                return float3.zero;
            }

            if (predator)
            {
                return float3.zero;
            }

            if (defaultMoveSpeed <= 0.0f)
            {
                return float3.zero;
            }

            if (meshLargestDimension <= 0.0f)
            {
                return float3.zero;
            }

            float maxAffectedSize = math.max(0.0f, settings.MaxAffectedBoidSize);
            if (meshLargestDimension * scaleMin > maxAffectedSize)
            {
                return float3.zero;
            }

            float largestScale = math.cmax(math.abs(localScale));
            float actualBoidSize = meshLargestDimension * largestScale;
            if (actualBoidSize > maxAffectedSize)
            {
                return float3.zero;
            }

            float speciesInfluence = ResolveSpeciesWaterCurrentInfluence(waterCurrentInfluence);
            if (speciesInfluence <= 0.0f)
            {
                return float3.zero;
            }

            Assert.IsTrue(settings.NoiseBlob.IsCreated, "Water current is enabled but the shared noise blob is missing.");
            if (settings.NoiseBlob.IsCreated == false)
            {
                return float3.zero;
            }

            float noiseScale = math.max(0.0001f, settings.NoiseScale);
            float2 direction = NormalizeDirection(settings.HorizontalDirection);
            float2 uv = (new float2(worldPosition.x, worldPosition.z) / noiseScale) + direction * elapsedTime * settings.NoiseSpeed;

            float noiseValue = SampleNoise01(settings.NoiseBlob, uv);
            float signedForce = (noiseValue - 0.5f) * 2.0f;
            float sizeStrengthMultiplier = CalculateSmallBoidCurrentMultiplier(actualBoidSize, maxAffectedSize);
            float currentSpeed = defaultMoveSpeed * math.max(0.0f, settings.CurrentStrengthMultiplier) * sizeStrengthMultiplier * speciesInfluence;
            float2 velocity = direction * signedForce * currentSpeed;
            return new float3(velocity.x, 0.0f, velocity.y);
        }

        private static float ResolveSpeciesWaterCurrentInfluence(float waterCurrentInfluence)
        {
            if (waterCurrentInfluence < -1.0f)
            {
                Assert.IsTrue(false, "Water current influence must be -1 or non-negative.");
                return 0.0f;
            }

            if (waterCurrentInfluence < 0.0f)
            {
                return 1.0f;
            }

            return waterCurrentInfluence;
        }

        private static float CalculateSmallBoidCurrentMultiplier(float actualBoidSize, float maxAffectedSize)
        {
            Assert.IsTrue(maxAffectedSize > 0.0f, "Water current max affected boid size must be positive before sampling current.");

            float normalizedSize = math.saturate(actualBoidSize / maxAffectedSize);
            return math.lerp(2.0f, 1.0f, normalizedSize);
        }

        private static float2 NormalizeDirection(float2 direction)
        {
            float lengthSquared = math.lengthsq(direction);
            if (lengthSquared <= 0.000001f)
            {
                return new float2(1.0f, 0.0f);
            }

            return direction * math.rsqrt(lengthSquared);
        }

        private static float SampleNoise01(BlobAssetReference<WaterCurrentNoiseBlob> noiseBlob, float2 uv)
        {
            ref WaterCurrentNoiseBlob noise = ref noiseBlob.Value;
            Assert.IsTrue(noise.Size > 1, "Water current noise texture size must be greater than 1.");
            Assert.IsTrue(noise.Samples.Length == noise.Size * noise.Size, "Water current noise sample count does not match texture size.");

            if (noise.Size <= 1 || noise.Samples.Length != noise.Size * noise.Size)
            {
                return 0.0f;
            }

            float2 wrapped = uv - math.floor(uv);
            float sampleX = (wrapped.x * noise.Size) - 0.5f;
            float sampleY = (wrapped.y * noise.Size) - 0.5f;
            int x0 = (int)math.floor(sampleX);
            int y0 = (int)math.floor(sampleY);
            float tx = sampleX - x0;
            float ty = sampleY - y0;

            x0 = WrapNoiseIndex(x0, noise.Size);
            y0 = WrapNoiseIndex(y0, noise.Size);
            int x1 = WrapNoiseIndex(x0 + 1, noise.Size);
            int y1 = WrapNoiseIndex(y0 + 1, noise.Size);

            float v00 = noise.Samples[y0 * noise.Size + x0];
            float v10 = noise.Samples[y0 * noise.Size + x1];
            float v01 = noise.Samples[y1 * noise.Size + x0];
            float v11 = noise.Samples[y1 * noise.Size + x1];
            float vx0 = math.lerp(v00, v10, tx);
            float vx1 = math.lerp(v01, v11, tx);
            return math.lerp(vx0, vx1, ty);
        }

        private static int WrapNoiseIndex(int index, int size)
        {
            int wrapped = index % size;
            if (wrapped < 0)
            {
                wrapped += size;
            }

            return wrapped;
        }
    }

    public static class WaterCurrentShaderProperties
    {
        public const string NoiseTextureName = "_WaterCurrentNoiseTexture";
        public const string ParamsName = "_WaterCurrentParams";
        public const string DirectionName = "_WaterCurrentDirection";
        public const string StaticParamsName = "_WaterCurrentStaticParams";
        public const string SecondaryParamsName = "_WaterCurrentSecondaryParams";
        public const string TimeName = "_WaterCurrentTime";

        public static readonly int NoiseTexture = Shader.PropertyToID(NoiseTextureName);
        public static readonly int Params = Shader.PropertyToID(ParamsName);
        public static readonly int Direction = Shader.PropertyToID(DirectionName);
        public static readonly int StaticParams = Shader.PropertyToID(StaticParamsName);
        public static readonly int SecondaryParams = Shader.PropertyToID(SecondaryParamsName);
        public static readonly int Time = Shader.PropertyToID(TimeName);
    }

    public class NoiseTextureManager : MonoBehaviour
    {
        private static NoiseTextureManager instance;
        public static NoiseTextureManager Instance
        {
            get
            {
                if (instance == null)
                {
                    var go = new GameObject("NoiseTextureManager");
                    instance = go.AddComponent<NoiseTextureManager>();
                    DontDestroyOnLoad(go);
                }
                return instance;
            }
        }

        [SerializeField]
        private Texture2D sharedNoiseTexture;
        [SerializeField]
        private bool waterCurrentEnabled = true;
        [SerializeField]
        private int noiseTextureSize = 512;
        [SerializeField]
        private float noiseTextureFeatureScale = 50.0f;
        [SerializeField]
        private float waterCurrentNoiseScale = 400.0f;
        [SerializeField]
        private float waterCurrentNoiseSpeed = 0.02f;
        [SerializeField]
        private float waterCurrentBoidStrengthMultiplier = 5.0f;
        [SerializeField]
        private float waterCurrentStaticShaderStrengthMultiplier = 4.0f;
        [SerializeField]
        private float waterCurrentStaticLongPlantDamping = 0.02f;
        [SerializeField]
        private float waterCurrentSecondaryNoiseScale = 1.0f;
        [SerializeField]
        private float waterCurrentSecondaryNoiseSpeed = 0.24f;
        [SerializeField]
        private float waterCurrentSecondaryNoiseStrength = 0.34f;
        [SerializeField]
        private float waterCurrentSecondaryNoiseSeed = 17.0f;
        [SerializeField]
        private float maxAffectedBoidSize = 1.0f;
        [SerializeField]
        private Vector2 horizontalDirection = new Vector2(1.0f, -2.17f);

        private float[] sharedNoiseSamples;
        private int generatedNoiseTextureSize = -1;
        private float generatedNoiseTextureFeatureScale = -1.0f;
        private BlobAssetReference<WaterCurrentNoiseBlob> currentNoiseBlob;
        private readonly List<BlobAssetReference<WaterCurrentNoiseBlob>> ownedNoiseBlobs = new List<BlobAssetReference<WaterCurrentNoiseBlob>>();
        private World syncedWorld;
        private Entity waterCurrentEntity = Entity.Null;

        private void Awake()
        {
            if (instance == null)
            {
                instance = this;
                DontDestroyOnLoad(gameObject);
                EnsureSharedNoise();
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void Update()
        {
            EnsureSharedNoise();
            ApplyShaderGlobals();
            UpdateEcsSingleton();
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                DisposeOwnedNoiseBlobs();
                instance = null;
            }
        }

        public void ApplyLocationWaterCurrentSettings(
            bool enabled,
            int textureSize,
            float textureFeatureScale,
            float noiseScale,
            float noiseSpeed,
            float boidStrengthMultiplier,
            float staticShaderStrengthMultiplier,
            float staticLongPlantDamping,
            float secondaryNoiseScale,
            float secondaryNoiseSpeed,
            float secondaryNoiseStrength,
            float secondaryNoiseSeed,
            float maxAffectedSize,
            Vector2 direction)
        {
            Assert.IsTrue(textureSize > 1, "[NoiseTextureManager] Water current texture size must be greater than 1.");
            Assert.IsTrue(textureFeatureScale > 0.0f, "[NoiseTextureManager] Water current texture feature scale must be positive.");
            Assert.IsTrue(noiseScale > 0.0f, "[NoiseTextureManager] Water current noise scale must be positive.");
            Assert.IsTrue(noiseSpeed >= 0.0f, "[NoiseTextureManager] Water current noise speed cannot be negative.");
            Assert.IsTrue(boidStrengthMultiplier >= 0.0f, "[NoiseTextureManager] Water current boid strength cannot be negative.");
            Assert.IsTrue(staticShaderStrengthMultiplier >= 0.0f, "[NoiseTextureManager] Water current static shader strength cannot be negative.");
            Assert.IsTrue(staticLongPlantDamping >= 0.0f, "[NoiseTextureManager] Water current static long plant damping cannot be negative.");
            Assert.IsTrue(secondaryNoiseScale > 0.0f, "[NoiseTextureManager] Water current secondary noise scale must be positive.");
            Assert.IsTrue(secondaryNoiseSpeed >= 0.0f, "[NoiseTextureManager] Water current secondary noise speed cannot be negative.");
            Assert.IsTrue(secondaryNoiseStrength >= 0.0f, "[NoiseTextureManager] Water current secondary noise strength cannot be negative.");
            Assert.IsTrue(maxAffectedSize >= 0.0f, "[NoiseTextureManager] Water current max affected boid size cannot be negative.");
            Assert.IsTrue(direction.sqrMagnitude > 0.000001f, "[NoiseTextureManager] Water current direction cannot be zero.");

            waterCurrentEnabled = enabled;
            noiseTextureSize = math.max(2, textureSize);
            noiseTextureFeatureScale = math.max(0.0001f, textureFeatureScale);
            waterCurrentNoiseScale = math.max(0.0001f, noiseScale);
            waterCurrentNoiseSpeed = math.max(0.0f, noiseSpeed);
            waterCurrentBoidStrengthMultiplier = math.max(0.0f, boidStrengthMultiplier);
            waterCurrentStaticShaderStrengthMultiplier = math.max(0.0f, staticShaderStrengthMultiplier);
            waterCurrentStaticLongPlantDamping = math.max(0.0f, staticLongPlantDamping);
            waterCurrentSecondaryNoiseScale = math.max(0.0001f, secondaryNoiseScale);
            waterCurrentSecondaryNoiseSpeed = math.max(0.0f, secondaryNoiseSpeed);
            waterCurrentSecondaryNoiseStrength = math.max(0.0f, secondaryNoiseStrength);
            waterCurrentSecondaryNoiseSeed = secondaryNoiseSeed;
            maxAffectedBoidSize = math.max(0.0f, maxAffectedSize);
            horizontalDirection = direction.normalized;

            EnsureSharedNoise();
            ApplyShaderGlobals();
            UpdateEcsSingleton();
        }

        private void EnsureSharedNoise()
        {
            Assert.IsTrue(noiseTextureSize > 1, "[NoiseTextureManager] Noise texture size must be greater than 1.");
            Assert.IsTrue(noiseTextureFeatureScale > 0.0f, "[NoiseTextureManager] Noise texture feature scale must be positive.");

            int validatedTextureSize = math.max(2, noiseTextureSize);
            if (sharedNoiseTexture != null &&
                sharedNoiseSamples != null &&
                generatedNoiseTextureSize == validatedTextureSize &&
                Mathf.Approximately(generatedNoiseTextureFeatureScale, noiseTextureFeatureScale) &&
                currentNoiseBlob.IsCreated)
            {
                return;
            }

            sharedNoiseTexture = NoiseGenerator.GenerateNoiseTexture(
                validatedTextureSize,
                validatedTextureSize,
                0.0f,
                0.0f,
                noiseTextureFeatureScale,
                true,
                out sharedNoiseSamples);
            generatedNoiseTextureSize = validatedTextureSize;
            generatedNoiseTextureFeatureScale = noiseTextureFeatureScale;
            currentNoiseBlob = CreateNoiseBlob(sharedNoiseSamples, validatedTextureSize);
            ownedNoiseBlobs.Add(currentNoiseBlob);
        }

        private BlobAssetReference<WaterCurrentNoiseBlob> CreateNoiseBlob(float[] samples, int textureSize)
        {
            Assert.IsTrue(samples != null, "[NoiseTextureManager] Water current samples cannot be null.");
            Assert.IsTrue(samples.Length == textureSize * textureSize, "[NoiseTextureManager] Water current sample count must match texture size.");

            BlobBuilder builder = new BlobBuilder(Allocator.Temp);
            ref WaterCurrentNoiseBlob root = ref builder.ConstructRoot<WaterCurrentNoiseBlob>();
            root.Size = textureSize;
            BlobBuilderArray<float> blobSamples = builder.Allocate(ref root.Samples, samples.Length);
            for (int i = 0; i < samples.Length; i++)
            {
                blobSamples[i] = samples[i];
            }

            BlobAssetReference<WaterCurrentNoiseBlob> blob = builder.CreateBlobAssetReference<WaterCurrentNoiseBlob>(Allocator.Persistent);
            builder.Dispose();
            return blob;
        }

        private void ApplyShaderGlobals()
        {
            float enabledValue = 0.0f;
            if (waterCurrentEnabled)
            {
                enabledValue = 1.0f;
            }

            Vector2 direction = GetNormalizedDirection();
            Shader.SetGlobalTexture(WaterCurrentShaderProperties.NoiseTexture, sharedNoiseTexture);
            Shader.SetGlobalVector(
                WaterCurrentShaderProperties.Params,
                new Vector4(waterCurrentNoiseScale, waterCurrentNoiseSpeed, waterCurrentStaticShaderStrengthMultiplier, enabledValue));
            Shader.SetGlobalVector(
                WaterCurrentShaderProperties.Direction,
                new Vector4(direction.x, 0.0f, direction.y, maxAffectedBoidSize));
            Shader.SetGlobalVector(
                WaterCurrentShaderProperties.StaticParams,
                new Vector4(waterCurrentStaticLongPlantDamping, 1.0f, 0.0f, 0.0f));
            Shader.SetGlobalVector(
                WaterCurrentShaderProperties.SecondaryParams,
                new Vector4(
                    waterCurrentSecondaryNoiseScale,
                    waterCurrentSecondaryNoiseSpeed,
                    waterCurrentSecondaryNoiseStrength,
                    waterCurrentSecondaryNoiseSeed));
            Shader.SetGlobalFloat(WaterCurrentShaderProperties.Time, GetSyncedWaterCurrentTime());
        }

        private float GetSyncedWaterCurrentTime()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || world.IsCreated == false)
            {
                return Time.time;
            }

            EntityManager entityManager = world.EntityManager;
            EntityQuery fixedStepTimeQuery = entityManager.CreateEntityQuery(ComponentType.ReadOnly<BoidFixedStepTime>());
            int fixedStepTimeCount = fixedStepTimeQuery.CalculateEntityCount();
            Assert.IsTrue(fixedStepTimeCount <= 1, "[NoiseTextureManager] Expected at most one BoidFixedStepTime singleton.");

            float syncedTime = Time.time;
            if (fixedStepTimeCount == 1)
            {
                BoidFixedStepTime fixedStepTime = fixedStepTimeQuery.GetSingleton<BoidFixedStepTime>();
                syncedTime = fixedStepTime.FixedElapsedTime;
            }

            fixedStepTimeQuery.Dispose();
            return syncedTime;
        }

        private void UpdateEcsSingleton()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || world.IsCreated == false)
            {
                return;
            }

            EntityManager entityManager = world.EntityManager;
            if (syncedWorld != world || waterCurrentEntity == Entity.Null || entityManager.Exists(waterCurrentEntity) == false)
            {
                syncedWorld = world;
                waterCurrentEntity = entityManager.CreateEntity(typeof(WaterCurrentSettings));
                entityManager.SetName(waterCurrentEntity, "Water Current Settings");
            }

            Vector2 direction = GetNormalizedDirection();
            int enabledValue = 0;
            if (waterCurrentEnabled)
            {
                enabledValue = 1;
            }

            entityManager.SetComponentData(waterCurrentEntity, new WaterCurrentSettings
            {
                Enabled = enabledValue,
                NoiseTextureSize = generatedNoiseTextureSize,
                NoiseScale = waterCurrentNoiseScale,
                NoiseSpeed = waterCurrentNoiseSpeed,
                CurrentStrengthMultiplier = waterCurrentBoidStrengthMultiplier,
                MaxAffectedBoidSize = maxAffectedBoidSize,
                HorizontalDirection = new float2(direction.x, direction.y),
                NoiseBlob = currentNoiseBlob
            });
        }

        private Vector2 GetNormalizedDirection()
        {
            if (horizontalDirection.sqrMagnitude <= 0.000001f)
            {
                return Vector2.right;
            }

            return horizontalDirection.normalized;
        }

        private void DisposeOwnedNoiseBlobs()
        {
            World world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated)
            {
                world.EntityManager.CompleteAllTrackedJobs();
            }

            for (int i = 0; i < ownedNoiseBlobs.Count; i++)
            {
                BlobAssetReference<WaterCurrentNoiseBlob> blob = ownedNoiseBlobs[i];
                if (blob.IsCreated)
                {
                    blob.Dispose();
                }
            }

            ownedNoiseBlobs.Clear();
        }

        /// <summary>
        /// Samples the generated noise texture at given world coordinates using a specific scale.
        /// </summary>
        /// <param name="worldX">World X coordinate.</param>
        /// <param name="worldY">World Y coordinate (used for Z in noise sampling).</param>
        /// <param name="offset">2D offset applied before scaling.</param>
        public float SampleNoise(float x, float y, Vector2 offset)
        {
            EnsureSharedNoise();
            float scaledX = (x + offset.x);
            float scaledY = (y + offset.y);
            return sharedNoiseTexture.GetPixelBilinear(scaledX, scaledY).r;
        }
    }
} 
