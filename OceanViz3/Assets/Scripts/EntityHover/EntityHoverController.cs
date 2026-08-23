using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace OceanViz3
{
    /// <summary>
    /// Owns Simulation-mode pointer input and the species label beside the pointer.
    /// ECS jobs perform the spatial query. The species label follows the pointer and hides immediately after a miss.
    /// </summary>
    public sealed class EntityHoverController
    {
        private const float LabelCursorOffset = 30.0f;
        private const float MaximumHoverDistance = 75.0f;
        private const int QueryFrameInterval = 4;
        private const bool StaticEntityHoverEnabled = true;

        private MainScene mainScene;
        private SimulationModeManager simulationModeManager;
        private EntityManager entityManager;
        private EntityQuery requestQuery;
        private EntityQuery resultQuery;
        private Camera simulationCamera;
        private VisualElement hoverNameContainer;
        private Label hoverNameLabel;
        private Entity displayedEntity;
        private uint requestSequence;
        private uint minimumAcceptedSequence;
        private uint lastAppliedResultSequence;
        private bool requestWasActive;
        private bool labelVisible;

        public void Setup(
            MainScene owner,
            SimulationModeManager modeManager,
            VisualElement mainGui)
        {
            mainScene = owner;
            simulationModeManager = modeManager;
            entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
            simulationCamera = mainScene.mainCamera.GetComponent<Camera>();

            Debug.Assert(simulationCamera != null, "[EntityHoverController] Main camera requires a Camera component.");
            Debug.Assert(mainGui != null, "[EntityHoverController] Simulation UI root is required.");

            requestQuery = entityManager.CreateEntityQuery(typeof(EntityHoverRequest));
            if (requestQuery.CalculateEntityCount() == 0)
            {
                Entity requestEntity = entityManager.CreateEntity(typeof(EntityHoverRequest));
                entityManager.SetName(requestEntity, "Entity Hover Request");
            }

            resultQuery = entityManager.CreateEntityQuery(typeof(EntityHoverResult));
            if (resultQuery.CalculateEntityCount() == 0)
            {
                Entity resultEntity = entityManager.CreateEntity(typeof(EntityHoverResult));
                entityManager.SetName(resultEntity, "Entity Hover Result");
            }

            CreateNameLabel(mainGui);
            Clear();
        }

        public void Update(
            bool interactionAllowed,
            int viewCount,
            bool useSyntheticInput,
            Vector2 syntheticScreenPosition)
        {
            if (!interactionAllowed ||
                (!useSyntheticInput && !Application.isFocused) ||
                (!useSyntheticInput && UnityEngine.Cursor.lockState != CursorLockMode.None) ||
                (!useSyntheticInput && !UnityEngine.Cursor.visible))
            {
                DeactivateRequest();
                return;
            }

            Vector2 screenPosition = Input.mousePosition;
            if (useSyntheticInput)
            {
                screenPosition = syntheticScreenPosition;
            }
            else if (IsPointerOverBlockingUI(screenPosition))
            {
                DeactivateRequest();
                return;
            }

            PositionNameLabel(screenPosition);
            ApplyLatestResult();
            if (requestWasActive && Time.frameCount % QueryFrameInterval != 0)
            {
                return;
            }

            Debug.Assert(viewCount >= 1 && viewCount <= 4, "[EntityHoverController] View count must be in [1, 4].");
            if (viewCount < 1 || viewCount > 4)
            {
                DeactivateRequest();
                return;
            }

            Ray ray = simulationCamera.ScreenPointToRay(screenPosition);
            float normalizedScreenX = Mathf.Clamp01(screenPosition.x / Mathf.Max(1.0f, Screen.width));
            int viewIndex = Mathf.Min(viewCount - 1, Mathf.FloorToInt(normalizedScreenX * viewCount));

            requestSequence++;
            EntityHoverRequest request = new EntityHoverRequest
            {
                RayOrigin = ray.origin,
                RayDirection = math.normalizesafe((float3)ray.direction, new float3(0.0f, 0.0f, 1.0f)),
                MaximumDistance = Mathf.Min(MaximumHoverDistance, simulationCamera.farClipPlane),
                NormalizedScreenX = normalizedScreenX,
                ViewIndex = viewIndex,
                Sequence = requestSequence,
                Active = true,
                IncludeStaticEntities = StaticEntityHoverEnabled
            };
            requestQuery.SetSingleton(request);
            requestWasActive = true;
        }

        public void Clear()
        {
            displayedEntity = Entity.Null;
            if (!labelVisible)
            {
                return;
            }

            Debug.Assert(hoverNameContainer != null, "[EntityHoverController] Visible hover label requires its UI container.");
            hoverNameContainer.style.display = DisplayStyle.None;
            labelVisible = false;
        }

        public void Deactivate()
        {
            DeactivateRequest();
        }

        public void Dispose()
        {
            Deactivate();
            if (hoverNameContainer != null)
            {
                hoverNameContainer.RemoveFromHierarchy();
                hoverNameContainer = null;
                hoverNameLabel = null;
            }
            requestQuery.Dispose();
            resultQuery.Dispose();
        }

        private void ApplyLatestResult()
        {
            if (resultQuery.CalculateEntityCount() != 1)
            {
                Debug.Assert(false, "[EntityHoverController] Expected exactly one EntityHoverResult singleton.");
                Clear();
                return;
            }

            EntityHoverResult result = resultQuery.GetSingleton<EntityHoverResult>();
            if (result.RequestSequence < minimumAcceptedSequence ||
                result.RequestSequence <= lastAppliedResultSequence)
            {
                return;
            }
            lastAppliedResultSequence = result.RequestSequence;

            Entity nextEntity = result.Entity;
            if (nextEntity != Entity.Null && !entityManager.Exists(nextEntity))
            {
                nextEntity = Entity.Null;
            }

            if (nextEntity == Entity.Null)
            {
                Clear();
                return;
            }

            if (nextEntity == displayedEntity)
            {
                return;
            }

            displayedEntity = nextEntity;
            string speciesName = ResolveSpeciesName(result.Kind, result.GroupId);
            Debug.Assert(!string.IsNullOrEmpty(speciesName), "[EntityHoverController] Hovered entity has no matching species preset name.");
            hoverNameLabel.text = speciesName;
            hoverNameContainer.style.display = DisplayStyle.Flex;
            labelVisible = true;
        }

        private void PositionNameLabel(Vector2 screenPosition)
        {
            if (hoverNameContainer == null || hoverNameContainer.panel == null)
            {
                return;
            }

            Vector2 panelPosition = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            VisualElement panelRoot = hoverNameContainer.panel.visualTree;
            float panelWidth = panelRoot.resolvedStyle.width;
            float panelHeight = panelRoot.resolvedStyle.height;
            float labelWidth = hoverNameContainer.resolvedStyle.width;
            float labelHeight = hoverNameContainer.resolvedStyle.height;
            float left = Mathf.Clamp(
                panelPosition.x + LabelCursorOffset,
                0.0f,
                Mathf.Max(0.0f, panelWidth - labelWidth));
            float top = Mathf.Clamp(
                panelPosition.y - labelHeight * 0.5f,
                0.0f,
                Mathf.Max(0.0f, panelHeight - labelHeight));
            hoverNameContainer.style.left = left;
            hoverNameContainer.style.top = top;
        }

        private string ResolveSpeciesName(EntityHoverKind kind, int groupId)
        {
            if (kind == EntityHoverKind.Dynamic)
            {
                DynamicEntitiesGroup group =
                    simulationModeManager.dynamicEntitiesGroups.Find(candidate => candidate.DynamicEntityId == groupId);
                if (group != null && group.dynamicEntityPreset != null)
                {
                    return group.dynamicEntityPreset.name;
                }
                return string.Empty;
            }

            if (kind == EntityHoverKind.Static)
            {
                StaticEntitiesGroup group =
                    simulationModeManager.staticEntitiesGroups.Find(candidate => candidate.StaticEntitiesGroupId == groupId);
                if (group != null && group.staticEntityPreset != null)
                {
                    return group.staticEntityPreset.name;
                }
                return string.Empty;
            }

            return string.Empty;
        }

        private bool IsPointerOverBlockingUI(Vector2 screenPosition)
        {
            if (hoverNameContainer == null || hoverNameContainer.panel == null)
            {
                return true;
            }

            Vector2 panelPosition = new Vector2(screenPosition.x, Screen.height - screenPosition.y);
            VisualElement picked = hoverNameContainer.panel.Pick(panelPosition);
            VisualElement root = hoverNameContainer.panel.visualTree;
            while (picked != null && picked != root)
            {
                if (picked == hoverNameContainer)
                {
                    return false;
                }
                if (picked.name == "DataFeeder" ||
                    picked.focusable ||
                    picked is Button ||
                    picked is Slider ||
                    picked is DropdownField ||
                    picked is Toggle ||
                    picked is TextField)
                {
                    return true;
                }
                picked = picked.parent;
            }
            return false;
        }

        private void DeactivateRequest()
        {
            if (requestWasActive)
            {
                requestSequence++;
                minimumAcceptedSequence = requestSequence;
                requestQuery.SetSingleton(new EntityHoverRequest
                {
                    Sequence = requestSequence,
                    Active = false
                });
                requestWasActive = false;
            }
            Clear();
        }

        private void CreateNameLabel(VisualElement mainGui)
        {
            hoverNameContainer = new VisualElement
            {
                name = "EntityHoverNameContainer",
                pickingMode = PickingMode.Ignore
            };
            hoverNameContainer.style.position = Position.Absolute;
            hoverNameContainer.style.top = 0.0f;
            hoverNameContainer.style.left = 0.0f;

            hoverNameLabel = new Label
            {
                name = "EntityHoverNameLabel",
                pickingMode = PickingMode.Ignore
            };
            hoverNameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            hoverNameLabel.style.fontSize = 10.35f;
            hoverNameLabel.style.color = Color.white;
            hoverNameLabel.style.backgroundColor = new Color(0.05f, 0.08f, 0.1f, 0.82f);
            hoverNameLabel.style.paddingTop = 3.5f;
            hoverNameLabel.style.paddingRight = 6.0f;
            hoverNameLabel.style.paddingBottom = 3.5f;
            hoverNameLabel.style.paddingLeft = 6.0f;
            hoverNameLabel.style.borderTopLeftRadius = 2.0f;
            hoverNameLabel.style.borderTopRightRadius = 2.0f;
            hoverNameLabel.style.borderBottomRightRadius = 2.0f;
            hoverNameLabel.style.borderBottomLeftRadius = 2.0f;

            hoverNameContainer.Add(hoverNameLabel);
            hoverNameContainer.style.display = DisplayStyle.None;
            labelVisible = false;
            mainGui.Add(hoverNameContainer);
        }
    }
}
