using System.Collections.Generic;
using System.Globalization;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.InputSystem;
using UnityEngine.Scripting;
using SeenBetterDays.Data;
using SeenBetterDays.Geometry;
using SeenBetterDays.Rendering;
using Transform = Game.Objects.Transform;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// The whole driver for the rendering proof of concept: a handful of hotkeys that apply,
    /// vary and remove weathering overlays on individual buildings, plus the diagnostics needed
    /// to tell a real result from a coincidence.
    ///
    /// There is deliberately no user interface. Everything here is a developer harness and is
    /// expected to be thrown away once the rendering question is settled; the part meant to
    /// survive is <see cref="IBuildingOverlayRenderer"/> and what sits behind it.
    ///
    /// Runs at ModificationEnd - see Mod.OnLoad for why that phase and no other. It makes
    /// structural entity changes straight from the main thread, which is fine for a few entities
    /// driven by a keypress and is still not what the real mod should do; that one belongs behind
    /// one of the modification barriers.
    ///
    /// Reading the selection here means reading what the UI published last frame, which for a
    /// keypress-driven harness is indistinguishable from reading it live.
    /// </summary>
    public partial class BuildingOverlayTestSystem : GameSystemBase
    {
        private PrefabSystem m_PrefabSystem;
        private SelectedInfoUISystem m_SelectedInfoUISystem;
        private Game.Rendering.CameraUpdateSystem m_CameraUpdateSystem;
        private Game.Tools.ToolRaycastSystem m_ToolRaycastSystem;

        private EntityQuery m_ObjectPrefabQuery;
        private EntityQuery m_GrowableQuery;
        private EntityQuery m_OverlayQuery;
        private EntityQuery m_OverriddenOverlayQuery;
        private EntityQuery m_PlacedObjectQuery;

        private DecalPrefabCatalog m_Catalog;
        private DecalObjectOverlayRenderer m_Renderer;

        /// <summary>The cheap whole-building colour backend. A/S/D/X drive this one; the decal
        /// hotkeys drive the separate, now verified, mesh-surface detail backend.</summary>
        private MeshColorOverlayRenderer m_ColourRenderer;

        private bool m_InGame;
        private bool m_CatalogueBuilt;
        private int m_ForcedDecalIndex = -1;

        private readonly List<Entity> m_Scratch = new List<Entity>();

        /// <summary>Building whose elements to describe once the game's own systems have had a
        /// few frames to fill in CullingInfo. Reading it straight after the command buffer plays
        /// back gives all zeroes and tells you nothing.</summary>
        private Entity m_PendingDiagnosis;
        private int m_DiagnosisCountdown;

        /// <summary>Frames to wait before another hotkey is accepted. Without it a key held for
        /// a fraction of a second re-applies five times and buries the log.</summary>
        private int m_InputCooldown;

        /// <summary>Total overlays un-hidden so far, so the log can say it happened without
        /// saying it once per frame.</summary>
        private int m_UnhiddenTotal;

        /// <summary>Rate limiter for the once-per-second heartbeat, so nothing here can spam
        /// the log frame by frame.</summary>

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // Every object prefab. The catalogue filters this down to decals itself; asking the
            // query for "decals" is not possible because decal-ness lives on the mesh prefab.
            m_ObjectPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<ObjectData>(),
                ComponentType.ReadOnly<SubMesh>());

            m_GrowableQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            // Every placed static object. Used to find decals the player put down by hand, so
            // their transform can be measured instead of guessed - see MeasureNearbyDecals.
            m_PlacedObjectQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Object>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<WeatheringOverlay>(),
                },
            });

            m_OverlayQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<WeatheringOverlay>() },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });

            // Overlays the game has decided to hide, because they intersect the building they
            // are drawn on. See KeepOverlaysVisible.
            m_OverriddenOverlayQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<WeatheringOverlay>(),
                    ComponentType.ReadOnly<Overridden>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });

            m_Catalog = new DecalPrefabCatalog();
            m_Renderer = new DecalObjectOverlayRenderer(EntityManager, m_Catalog, m_OverlayQuery, Mod.Log);
            m_ColourRenderer = new MeshColorOverlayRenderer(EntityManager, Mod.Log);

            Mod.Log.Info("Seen Better Days: overlay test system created.");
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            m_InGame = mode == GameMode.Game;
            m_CatalogueBuilt = false;

            // Overlays are runtime-only. Anything still tracked belongs to the world we just
            // left, so drop it rather than carry stale entity ids into the new one. Sweep the
            // world too: the serializable marker is a recovery signature for an overlay object
            // that survived an interrupted save after its in-memory record was lost.
            m_Renderer.RemoveAll();
            int recovered = m_Renderer.SweepStrayOverlays();

            if (recovered > 0)
            {
                Mod.Log.Info("Seen Better Days: removed " + recovered
                           + " weathering decal entit(ies) recovered while loading.");
            }

            Mod.Log.Info("Seen Better Days: game loading complete (purpose=" + purpose + ", mode=" + mode
                       + "), overlay harness " + (m_InGame ? "armed." : "idle."));
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!m_InGame)
            {
                return;
            }

            if (!m_CatalogueBuilt)
            {
                BuildCatalogue();
            }

            // Test E: an overlay must never outlive the building it belongs to. Sweeping the
            // whole tracked set every frame is fine for a handful of test buildings; the real
            // mod has to react to the building's deletion instead.
            m_Renderer.PruneOrphans();
            m_ColourRenderer.PruneOrphans();
            KeepOverlaysVisible();

            if (m_DiagnosisCountdown > 0 && --m_DiagnosisCountdown <= 0)
            {
                Mod.Log.Info("Seen Better Days: " + m_Renderer.DescribeElements(m_PendingDiagnosis));
            }

            HandleInput();
        }

        /// <summary>
        /// Takes <see cref="Overridden"/> back off our overlay entities.
        ///
        /// The game hides objects that intersect something they should not: that is how a prop
        /// under a building disappears. Our decals are deliberately placed *into* a building's
        /// volume, so <c>Game.Objects.OverrideSystem</c> flags every one of them, and
        /// <c>PreCullingSystem.InitializeCullingJob</c> then leaves their bounds mask as
        /// <c>BoundsMask.Debug</c> alone - which is only ever visible in a debug view. The entity
        /// is initialised, sized and positioned correctly, and simply never drawn. That is
        /// exactly what the second in-game run showed: mask=Debug, passed=0.
        ///
        /// Removing the component and re-tagging <see cref="Updated"/> makes the culling system
        /// recompute the mask without the override, which restores NormalLayers. This is the same
        /// remedy Anarchy uses for hand-placed props (RemoveOverridenSystem), so it is a known
        /// working pattern rather than a guess - and the override system will keep re-applying
        /// the component, so this has to keep running.
        /// </summary>
        private void KeepOverlaysVisible()
        {
            if (m_OverriddenOverlayQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> hidden = m_OverriddenOverlayQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                EntityCommandBuffer commandBuffer = new EntityCommandBuffer(Allocator.Temp);
                try
                {
                    for (int i = 0; i < hidden.Length; i++)
                    {
                        commandBuffer.RemoveComponent<Overridden>(hidden[i]);
                        commandBuffer.AddComponent<Updated>(hidden[i]);
                    }

                    commandBuffer.Playback(EntityManager);
                }
                finally
                {
                    commandBuffer.Dispose();
                }

                int before = m_UnhiddenTotal;
                m_UnhiddenTotal += hidden.Length;

                // The override system keeps re-applying the component every time we re-tag the
                // entity as Updated, so this runs continuously by design. Log the first one and
                // then only rarely, or it drowns everything else.
                if (before == 0 || (before / 5000) != (m_UnhiddenTotal / 5000))
                {
                    Mod.Log.Info("Seen Better Days: un-hid " + hidden.Length
                               + " overlay element(s) the game had marked Overridden ("
                               + m_UnhiddenTotal + " so far this session).");
                }
            }
            finally
            {
                hidden.Dispose();
            }
        }

        private void BuildCatalogue()
        {
            m_Catalog.Rebuild(EntityManager, m_PrefabSystem, m_ObjectPrefabQuery);
            m_CatalogueBuilt = true;

            Mod.Log.Info("Seen Better Days: " + m_Catalog.Describe(24));

            if (!m_Renderer.IsAvailable)
            {
                Mod.Log.Warn("Seen Better Days: the decal backend has nothing to draw with - "
                           + m_Renderer.UnavailableReason
                           + ". Press Ctrl+Alt+C for the full catalogue.");
            }
        }

        private void HandleInput()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            // Ctrl+Alt+<key> throughout: the game binds plain letters heavily, and a debug
            // harness that steals a vanilla shortcut wastes an evening of testing.
            if (!keyboard.ctrlKey.isPressed || !keyboard.altKey.isPressed)
            {
                return;
            }

            if (SaveMutationGate.IsBlocked)
            {
                // Report a chord that was deliberately refused. The allKeys walk is defensive for
                // the same reason as the normal read-out below: some keyboard layouts expose
                // controls whose keyCode accessor is not backed.
                try
                {
                    foreach (UnityEngine.InputSystem.Controls.KeyControl key in keyboard.allKeys)
                    {
                        if (key == null || !key.wasPressedThisFrame)
                        {
                            continue;
                        }

                        UnityEngine.InputSystem.Key code = key.keyCode;
                        if (code != UnityEngine.InputSystem.Key.LeftAlt
                            && code != UnityEngine.InputSystem.Key.RightAlt
                            && code != UnityEngine.InputSystem.Key.LeftCtrl
                            && code != UnityEngine.InputSystem.Key.RightCtrl)
                        {
                            Mod.Log.Warn("Seen Better Days: ignored Ctrl+Alt+" + code
                                       + " while a save is finishing; try again in three seconds.");
                            break;
                        }
                    }
                }
                catch
                {
                    // Input diagnostics must not make the save-safety path unsafe.
                }

                return;
            }

            // Only counts down while the modifiers are held, and is only armed once an action
            // has actually fired - so the first press is never swallowed, but a key held a
            // fraction of a second does not re-apply five times and bury the log.
            if (m_InputCooldown > 0)
            {
                m_InputCooldown--;
                return;
            }

            // Name every Ctrl+Alt chord we receive, handled or not. Without this a hotkey that
            // produces no log line is ambiguous between "the handler bailed", "the branch is
            // unreachable" and "the keystroke never reached the game at all" - and telling those
            // apart by guessing has already cost a round.
            // Every key pressed this frame while the modifiers are held, and the modifiers
            // themselves skipped. The previous version gated on anyKey.wasPressedThisFrame, which
            // only fires on the transition from no-keys-down: with Ctrl+Alt already held it never
            // fired again, so it reported the Alt press and nothing else - hiding exactly the
            // information it existed to provide. Defensive throughout: allKeys can contain
            // entries this layout does not back, and reading keyCode off one of those already
            // crashed the game once.
            try
            {
                foreach (UnityEngine.InputSystem.Controls.KeyControl key in keyboard.allKeys)
                {
                    if (key == null || !key.wasPressedThisFrame)
                    {
                        continue;
                    }

                    UnityEngine.InputSystem.Key code = key.keyCode;
                    if (code == UnityEngine.InputSystem.Key.LeftAlt || code == UnityEngine.InputSystem.Key.RightAlt
                        || code == UnityEngine.InputSystem.Key.LeftCtrl || code == UnityEngine.InputSystem.Key.RightCtrl)
                    {
                        continue;
                    }

                    Mod.Log.Info("Seen Better Days: received Ctrl+Alt+" + code + ".");
                }
            }
            catch
            {
                // A read-out must never be able to take the phase down. It already did once.
            }

            if (keyboard.aKey.wasPressedThisFrame)
            {
                ApplyToTarget(VisualState.Decayed, useAlternateSeed: false);
            }
            else if (keyboard.sKey.wasPressedThisFrame)
            {
                ApplyToTarget(VisualState.Decayed, useAlternateSeed: true);
            }
            else if (keyboard.dKey.wasPressedThisFrame)
            {
                RemoveFromTarget();
            }
            else if (keyboard.xKey.wasPressedThisFrame)
            {
                RemoveEverything();
            }
            else if (keyboard.fKey.wasPressedThisFrame)
            {
                CycleFacade();
            }
            else if (keyboard.nKey.wasPressedThisFrame)
            {
                CycleForcedDecal();
            }
            else if (keyboard.cKey.wasPressedThisFrame)
            {
                Mod.Log.Info("Seen Better Days: " + m_Catalog.Describe(int.MaxValue));
                LogStatus();
            }
            else if (keyboard.iKey.wasPressedThisFrame)
            {
                DescribeTarget();
            }
            else if (keyboard.tKey.wasPressedThisFrame)
            {
                ApplyTestDecalToTarget();
            }
            else if (keyboard.kKey.wasPressedThisFrame)
            {
                // K, not R: NVIDIA's overlay owns Alt+R and pops up over the game.
                m_Renderer.InvertProjection = !m_Renderer.InvertProjection;
                Mod.Log.Info("Seen Better Days: projection direction inverted="
                           + m_Renderer.InvertProjection + ". Re-apply (Ctrl+Alt+T or A) to see it.");
            }
            else if (keyboard.gKey.wasPressedThisFrame)
            {
                // Metres now, not a fraction of the projector depth: the old range topped out at
                // a quarter of a metre, far too small to reach a wall set back behind the
                // building's bounding box.
                float next = m_Renderer.NormalOffsetMetres - 0.5f;
                m_Renderer.NormalOffsetMetres = next < -4.01f ? 0.5f : next;
                Mod.Log.Info("Seen Better Days: normal offset is now "
                           + m_Renderer.NormalOffsetMetres.ToString("0.00")
                           + " m along the facade normal (negative = into the building). Re-apply to see it.");
            }
            else if (keyboard.pKey.wasPressedThisFrame)
            {
                MeasureNearbyDecals();
            }
            else if (keyboard.zKey.wasPressedThisFrame)
            {
                BuildingWeatheringSystem weathering =
                    World.GetExistingSystemManaged<BuildingWeatheringSystem>();

                if (weathering == null)
                {
                    Mod.Log.Info("Seen Better Days: no weathering system.");
                }
                else
                {
                    weathering.Enabled = false;
                    int cleared = weathering.PurgeAllCustomColours();
                    Mod.Log.Info("Seen Better Days: stripped the custom colour from " + cleared
                               + " growable(s) - this mod's leftovers AND any the player set with "
                               + "Recolor, because a saved CustomMeshColor carries no record of who "
                               + "wrote it. Automatic weathering has been switched off.");
                }
            }
            else if (keyboard.digit1Key.wasPressedThisFrame)
            {
                SetWeatheringResponse(WeatheringResponse.DarknessOnly);
            }
            else if (keyboard.digit2Key.wasPressedThisFrame)
            {
                SetWeatheringResponse(WeatheringResponse.Fading);
            }
            else if (keyboard.digit3Key.wasPressedThisFrame)
            {
                SetWeatheringResponse(WeatheringResponse.Full);
            }
            else if (keyboard.qKey.wasPressedThisFrame)
            {
                BuildingWeatheringSystem census =
                    World.GetExistingSystemManaged<BuildingWeatheringSystem>();
                Mod.Log.Info("Seen Better Days: " + (census == null ? "no weathering system" : census.Census()));
            }
            else if (keyboard.wKey.wasPressedThisFrame)
            {
                ToggleAutomaticWeathering();
            }
            else if (keyboard.jKey.wasPressedThisFrame)
            {
                // J, not M: Ctrl+Alt+M never reaches the game on this machine - something
                // outside it swallows the chord - while J is in the logs working. J used to
                // drive Surface.m_Dirtyness, which is ruled out and gone.
                CycleMeshColourWeathering();
            }
            else if (keyboard.f1Key.wasPressedThisFrame)
            {
                SetWeatheringByHand(VisualState.Maintained, 0f);
            }
            else if (keyboard.f2Key.wasPressedThisFrame)
            {
                SetWeatheringByHand(VisualState.Aged, 0.22f);
            }
            else if (keyboard.f3Key.wasPressedThisFrame)
            {
                SetWeatheringByHand(VisualState.Worn, 0.47f);
            }
            else if (keyboard.f4Key.wasPressedThisFrame)
            {
                SetWeatheringByHand(VisualState.Neglected, 0.72f);
            }
            else if (keyboard.f5Key.wasPressedThisFrame)
            {
                SetWeatheringByHand(VisualState.Decayed, 0.95f);
            }
            else if (keyboard.f6Key.wasPressedThisFrame)
            {
                ReleaseWeatheringByHand();
            }
            else if (keyboard.uKey.wasPressedThisFrame)
            {
                DecalDetailSystem detail = World.GetExistingSystemManaged<DecalDetailSystem>();
                if (detail == null)
                {
                    Mod.Log.Info("Seen Better Days: no decal detail system.");
                }
                else
                {
                    detail.Enabled = !detail.Enabled;
                    if (detail.Enabled)
                    {
                        Mod.Log.Info("Seen Better Days: the decal detail layer is ON. Weathered "
                                   + "buildings within about 200m of the camera pick up marks as "
                                   + "their meshes load, and drop them again past 280m.");
                    }
                    else
                    {
                        int cleared = m_Renderer.RemoveAll();
                        Mod.Log.Info("Seen Better Days: the decal detail layer is OFF; cleared "
                                   + cleared + " building(s).");
                    }
                }
            }
            else if (keyboard.vKey.wasPressedThisFrame)
            {
                ApplyDecalsToTarget();
            }
            else if (keyboard.hKey.wasPressedThisFrame)
            {
                PlaceDecalAtCursor();
            }
            else if (keyboard.yKey.wasPressedThisFrame)
            {
                DropGroundProbe();
            }
            else if (keyboard.bKey.wasPressedThisFrame)
            {
                m_Renderer.AllowNonBuildingDecals = !m_Renderer.AllowNonBuildingDecals;
                Mod.Log.Info("Seen Better Days: decals that do not declare DecalLayers.Buildings are now "
                           + (m_Renderer.AllowNonBuildingDecals ? "allowed" : "excluded")
                           + ". They will not paint a facade; this is a diagnostic only.");
                LogStatus();
            }
            else
            {
                return;
            }

            m_InputCooldown = 15;
        }

        /// <summary>
        /// The building the tester means: whatever is selected, or - so the harness is usable
        /// on a fresh city without hunting for something clickable - the first eligible growable
        /// in the world.
        /// </summary>
        private bool TryResolveTarget(out Entity building, out BuildingCategory category, out Entity prefabEntity, out int level)
        {
            category = BuildingCategory.NotABuilding;
            prefabEntity = Entity.Null;
            level = 0;

            building = SelectedEntity;
            string reason;

            if (building != Entity.Null)
            {
                category = BuildingClassifier.Classify(EntityManager, building, out prefabEntity, out level, out reason);
                if (category.IsEligible())
                {
                    Mod.Log.Info("Seen Better Days: target is the SELECTED building, " + Describe(building) + ".");
                    return true;
                }

                Mod.Log.Warn("Seen Better Days: selected entity " + Describe(building)
                           + " is not an eligible growable (" + reason + "). Falling back to the first one in the city.");
            }
            else
            {
                // Worth shouting about. selectedEntity is Entity.Null whenever the info panel is
                // closed, so clicking a building and then closing the panel - or never opening it -
                // silently sends the whole test to some other building on the far side of the map,
                // which looks exactly like the mod doing nothing.
                Mod.Log.Warn("Seen Better Days: NOTHING IS SELECTED (the building info panel is not "
                           + "open), so the test is falling back to the first eligible growable in "
                           + "the city - which is almost certainly not the building you are looking "
                           + "at. The camera will be moved to it.");
            }

            return TryFindFirstEligibleGrowable(out building, out category, out prefabEntity, out level);
        }

        private bool TryFindFirstEligibleGrowable(out Entity building, out BuildingCategory category, out Entity prefabEntity, out int level)
        {
            building = Entity.Null;
            category = BuildingCategory.NotABuilding;
            prefabEntity = Entity.Null;
            level = 0;

            NativeArray<Entity> candidates = m_GrowableQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    string reason;
                    Entity candidatePrefab;
                    int candidateLevel;
                    BuildingCategory candidateCategory = BuildingClassifier.Classify(
                        EntityManager, candidates[i], out candidatePrefab, out candidateLevel, out reason);

                    if (!candidateCategory.IsEligible())
                    {
                        continue;
                    }

                    building = candidates[i];
                    category = candidateCategory;
                    prefabEntity = candidatePrefab;
                    level = candidateLevel;
                    return true;
                }
            }
            finally
            {
                candidates.Dispose();
            }

            Mod.Log.Warn("Seen Better Days: no eligible growable found in this city.");
            return false;
        }

        private void ApplyToTarget(VisualState state, bool useAlternateSeed)
        {
            Entity building;
            BuildingCategory category;
            Entity prefabEntity;
            int level;

            if (!TryResolveTarget(out building, out category, out prefabEntity, out level))
            {
                return;
            }

            // Two different seeds on demand is the whole of test G: the same prefab, two
            // instances, two independent appearances.
            uint seed = (uint)building.Index * 2654435761u + 1u;
            if (useAlternateSeed)
            {
                seed = seed * 1103515245u + 12345u;
            }

            BuildingVisualProfile profile = BuildingVisualProfile.Debug(state, seed);

            int placed;
            string failure;
            bool ok = m_ColourRenderer.Apply(building, profile, out placed, out failure);

            if (!ok)
            {
                Mod.Log.Warn("Seen Better Days: could not weather " + Describe(building) + " - " + failure);
                return;
            }

            Transform transform = EntityManager.GetComponentData<Transform>(building);
            Mod.Log.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Seen Better Days: applied {0} element(s) to {1} | category={2} level={3} facade={4} backend={5}\n"
              + "             profile: {6}\n"
              + "             position=({7:0.0},{8:0.0},{9:0.0}) rotation=({10:0.000},{11:0.000},{12:0.000},{13:0.000})",
                placed, Describe(building), category, level, m_Renderer.Side, m_ColourRenderer.Name,
                profile,
                transform.m_Position.x, transform.m_Position.y, transform.m_Position.z,
                transform.m_Rotation.value.x, transform.m_Rotation.value.y,
                transform.m_Rotation.value.z, transform.m_Rotation.value.w));

            JumpCameraTo(building);
            ScheduleDiagnosis(building);
            LogStatus();
        }

        /// <summary>
        /// One decal, six heights up the facade. See ApplyHeightSweep for why.
        /// </summary>
        private void ApplyTestDecalToTarget()
        {
            Entity building;
            BuildingCategory category;
            Entity prefabEntity;
            int level;

            if (!TryResolveTarget(out building, out category, out prefabEntity, out level))
            {
                return;
            }

            DecalPrefabInfo decal = m_Renderer.ForcedDecal;

            if (decal == null)
            {
                // One decal, the most legible one available: high contrast, real base colour,
                // big enough to see from the street.
                List<DecalPrefabInfo> candidates = m_Catalog.PickTestSheet(1, 8f, requireBaseColor: true);
                if (candidates.Count == 0)
                {
                    candidates = m_Catalog.PickTestSheet(1, 8f, requireBaseColor: false);
                    Mod.Log.Warn("Seen Better Days: no decal with a base colour map was found; "
                               + "falling back to whatever is available.");
                }

                if (candidates.Count > 0)
                {
                    decal = candidates[0];
                }
            }

            if (decal == null)
            {
                Mod.Log.Warn("Seen Better Days: no building-capable decal to test with.");
                return;
            }

            int placed;
            string report;
            string failure;
            if (!m_Renderer.ApplyHeightSweep(building, decal, out placed, out report, out failure))
            {
                Mod.Log.Warn("Seen Better Days: height sweep failed on " + Describe(building) + " - " + failure);
                return;
            }

            Mod.Log.Info("Seen Better Days: HEIGHT SWEEP, " + placed + " copies of " + decal
                       + " up the " + m_Renderer.Side + " facade of " + Describe(building)
                       + " (category=" + category + "), each higher than the last:" + report);

            JumpCameraTo(building);
            ScheduleDiagnosis(building);
            LogStatus();
        }

        /// <summary>
        /// Puts the camera on the building we just worked on.
        ///
        /// This exists because the harness can silently target a building on the other side of
        /// the city - see TryResolveTarget - and "I see no difference" is indistinguishable from
        /// "the decals are four kilometres away". Only the pivot moves; zoom and angle stay as
        /// the player left them, the same courtesy Seety's camera jumps use.
        /// </summary>
        private void JumpCameraTo(Entity building)
        {
            if (m_CameraUpdateSystem == null)
            {
                m_CameraUpdateSystem = World.GetExistingSystemManaged<Game.Rendering.CameraUpdateSystem>();
            }

            if (m_CameraUpdateSystem == null
                || m_CameraUpdateSystem.activeCameraController == null
                || !EntityManager.HasComponent<Transform>(building))
            {
                Mod.Log.Warn("Seen Better Days: could not move the camera to " + Describe(building) + ".");
                return;
            }

            Transform transform = EntityManager.GetComponentData<Transform>(building);
            m_CameraUpdateSystem.activeCameraController.pivot =
                new UnityEngine.Vector3(transform.m_Position.x, transform.m_Position.y, transform.m_Position.z);

            Mod.Log.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Seen Better Days: camera moved to {0} at ({1:0.0},{2:0.0},{3:0.0}).",
                Describe(building), transform.m_Position.x, transform.m_Position.y, transform.m_Position.z));
        }

        /// <summary>How weathered the selected building's colours are. Cycled by Ctrl+Alt+M.</summary>
        private int m_ColourStep;

        /// <summary>
        /// Weathers the selected building by overriding its own colours, per instance.
        ///
        /// This is the second backend, and the reason for trying it is that its mechanism is
        /// already proven by a shipping mod: Recolor repaints individual buildings through
        /// <c>Game.Rendering.CustomMeshColor</c>, a per-instance buffer serialized with the save.
        /// <c>MeshColorSystem.ApplyCustomMeshColors</c> copies its ColorSet over the building's own
        /// <c>MeshColor</c> whenever the buffer is present and enabled.
        ///
        /// Crucially, and unlike the dirt channel, the refresh is ours to trigger: MeshColorSystem
        /// re-runs for any entity matching <c>All=[MeshColor], Any=[Updated, BatchesUpdated]</c>.
        /// No race with a simulation system on a 256-tick interval.
        ///
        /// What it can say: a building growing greyer, duller and darker as it is neglected -
        /// legible at any distance, in any light, on every asset, for the cost of one buffer.
        /// What it cannot say: graffiti here, a crack there. Those stay a later decal layer.
        ///
        /// A probe for now. If it reads well it gets promoted to a proper
        /// IBuildingOverlayRenderer beside the decal one, which is what that interface is for.
        /// </summary>
        private void CycleMeshColourWeathering()
        {
            Entity building;
            BuildingCategory category;
            Entity prefabEntity;
            int level;

            if (!TryResolveTarget(out building, out category, out prefabEntity, out level))
            {
                return;
            }

            if (!EntityManager.HasBuffer<Game.Rendering.MeshColor>(building))
            {
                Mod.Log.Warn("Seen Better Days: " + Describe(building) + " has no MeshColor buffer - "
                           + "this asset declares no recolourable channels, so the colour route "
                           + "cannot touch it. That is a real limit of the approach, not a bug.");
                return;
            }

            m_ColourStep = (m_ColourStep + 1) & 3;

            if (m_ColourStep == 0)
            {
                if (EntityManager.HasBuffer<Game.Rendering.CustomMeshColor>(building))
                {
                    EntityManager.RemoveComponent<Game.Rendering.CustomMeshColor>(building);
                }

                // The baseline goes with it: next time we weather this building we want to
                // capture whatever its colours are then, not whatever they were the first time.
                if (EntityManager.HasBuffer<PristineMeshColor>(building))
                {
                    EntityManager.RemoveComponent<PristineMeshColor>(building);
                }

                Touch(building);
                Mod.Log.Info("Seen Better Days: COLOUR on " + Describe(building)
                           + " reset to the prefab's own colours.");
                JumpCameraTo(building);
                return;
            }

            // Darkness and desaturation together: grime removes saturation as much as light.
            float darkness = 1f - 0.18f * m_ColourStep;
            float desaturation = 0.25f * m_ColourStep;

            DynamicBuffer<Game.Rendering.MeshColor> meshColors =
                EntityManager.GetBuffer<Game.Rendering.MeshColor>(building, true);

            if (meshColors.Length == 0)
            {
                Mod.Log.Warn("Seen Better Days: " + Describe(building) + " has an empty MeshColor buffer.");
                return;
            }

            // One element per submesh of the prefab, not one overall. Recolor - which does this
            // for a living - sizes the buffer to the prefab's SubMesh buffer length and seeds each
            // entry from the matching MeshColor, falling back to MeshColor[0] when there are fewer
            // colours than submeshes.
            int subMeshCount = 1;
            if (EntityManager.HasBuffer<SubMesh>(prefabEntity))
            {
                subMeshCount = math.max(1, EntityManager.GetBuffer<SubMesh>(prefabEntity, true).Length);
            }

            // Capture the untouched colours once, and weather from those ever after. Reading
            // MeshColor each time instead compounds the effect, because MeshColor is what the
            // previous pass overwrote - the first in-game run turned x0.82, x0.64, x0.46 into
            // x0.24 and a nearly black building. See PristineMeshColor.
            if (!EntityManager.HasBuffer<PristineMeshColor>(building))
            {
                DynamicBuffer<PristineMeshColor> fresh = EntityManager.AddBuffer<PristineMeshColor>(building);
                for (int i = 0; i < subMeshCount; i++)
                {
                    fresh.Add(new PristineMeshColor(
                        meshColors.Length > i ? meshColors[i].m_ColorSet : meshColors[0].m_ColorSet));
                }
            }

            DynamicBuffer<PristineMeshColor> pristine =
                EntityManager.GetBuffer<PristineMeshColor>(building, true);

            Game.Rendering.ColorSet source = pristine[0].m_ColorSet;
            Game.Rendering.ColorSet weathered = new Game.Rendering.ColorSet
            {
                m_Channel0 = Weather(source.m_Channel0, darkness, desaturation),
                m_Channel1 = Weather(source.m_Channel1, darkness, desaturation),
                m_Channel2 = Weather(source.m_Channel2, darkness, desaturation),
            };

            DynamicBuffer<Game.Rendering.CustomMeshColor> custom =
                EntityManager.HasBuffer<Game.Rendering.CustomMeshColor>(building)
                    ? EntityManager.GetBuffer<Game.Rendering.CustomMeshColor>(building)
                    : EntityManager.AddBuffer<Game.Rendering.CustomMeshColor>(building);

            custom.ResizeUninitialized(subMeshCount);
            for (int i = 0; i < subMeshCount; i++)
            {
                Game.Rendering.ColorSet basis = pristine.Length > i ? pristine[i].m_ColorSet : source;
                custom[i] = new Game.Rendering.CustomMeshColor
                {
                    m_ColorSet = new Game.Rendering.ColorSet
                    {
                        m_Channel0 = Weather(basis.m_Channel0, darkness, desaturation),
                        m_Channel1 = Weather(basis.m_Channel1, darkness, desaturation),
                        m_Channel2 = Weather(basis.m_Channel2, darkness, desaturation),
                    },
                };
            }

            // The buffer is enableable, and MeshColorSystem checks IsBufferEnabled before it
            // applies anything.
            EntityManager.SetComponentEnabled<Game.Rendering.CustomMeshColor>(building, true);

            Touch(building);

            Mod.Log.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Seen Better Days: COLOUR step {0}/3 on {1} (category={2}, level={3}) - "
              + "darkness x{4:0.00}, desaturation {5:0.00}, MeshColor buffer length {6}\n"
              + "             channel0 ({7:0.00},{8:0.00},{9:0.00}) -> ({10:0.00},{11:0.00},{12:0.00})\n"
              + "             channel1 ({13:0.00},{14:0.00},{15:0.00}) -> ({16:0.00},{17:0.00},{18:0.00})\n"
              + "             channel2 ({19:0.00},{20:0.00},{21:0.00}) -> ({22:0.00},{23:0.00},{24:0.00})",
                m_ColourStep, Describe(building), category, level, darkness, desaturation, meshColors.Length,
                source.m_Channel0.r, source.m_Channel0.g, source.m_Channel0.b,
                weathered.m_Channel0.r, weathered.m_Channel0.g, weathered.m_Channel0.b,
                source.m_Channel1.r, source.m_Channel1.g, source.m_Channel1.b,
                weathered.m_Channel1.r, weathered.m_Channel1.g, weathered.m_Channel1.b,
                source.m_Channel2.r, source.m_Channel2.g, source.m_Channel2.b,
                weathered.m_Channel2.r, weathered.m_Channel2.g, weathered.m_Channel2.b));

            JumpCameraTo(building);
        }

        /// <summary>
        /// Makes MeshColorSystem look at this building again this frame - and at its sub-objects,
        /// which Recolor does too: a building's awnings, signs and fittings are separate entities
        /// and would otherwise keep their old colours while the shell changed.
        /// </summary>
        private void Touch(Entity building)
        {
            if (!EntityManager.HasComponent<BatchesUpdated>(building))
            {
                EntityManager.AddComponent<BatchesUpdated>(building);
            }

            if (!EntityManager.HasBuffer<Game.Objects.SubObject>(building))
            {
                return;
            }

            DynamicBuffer<Game.Objects.SubObject> subObjects =
                EntityManager.GetBuffer<Game.Objects.SubObject>(building, true);

            for (int i = 0; i < subObjects.Length; i++)
            {
                Entity subObject = subObjects[i].m_SubObject;
                if (EntityManager.Exists(subObject) && !EntityManager.HasComponent<BatchesUpdated>(subObject))
                {
                    EntityManager.AddComponent<BatchesUpdated>(subObject);
                }
            }
        }

        /// <summary>Ages one colour: duller first, then darker.</summary>
        private static UnityEngine.Color Weather(UnityEngine.Color colour, float darkness, float desaturation)
        {
            float luminance = colour.r * 0.299f + colour.g * 0.587f + colour.b * 0.114f;

            return new UnityEngine.Color(
                UnityEngine.Mathf.Lerp(colour.r, luminance, desaturation) * darkness,
                UnityEngine.Mathf.Lerp(colour.g, luminance, desaturation) * darkness,
                UnityEngine.Mathf.Lerp(colour.b, luminance, desaturation) * darkness,
                colour.a);
        }

        /// <summary>
        /// Finds decals the player has placed by hand near the camera and prints their exact
        /// transform.
        ///
        /// Every attempt to derive the wall rotation from first principles has produced entities
        /// that render (real batch, passes culling) and paint nothing, at any depth and in either
        /// projection direction. Extra Detailing Tools puts decals on walls perfectly well using
        /// the game's own Snap.ObjectSurface, so a working example exists and can simply be
        /// measured: place one by hand, press this, and compare its rotation with what
        /// BuildingFacade.DecalRotation computes for the same wall.
        ///
        /// Measuring beats reasoning here. Two more rounds of argument about LookRotationSafe
        /// would cost more than one hand-placed decal.
        /// </summary>
        private void MeasureNearbyDecals()
        {
            if (m_CameraUpdateSystem == null)
            {
                m_CameraUpdateSystem = World.GetExistingSystemManaged<Game.Rendering.CameraUpdateSystem>();
            }

            if (m_CameraUpdateSystem == null || m_CameraUpdateSystem.activeCameraController == null)
            {
                Mod.Log.Warn("Seen Better Days: no active camera, cannot measure.");
                return;
            }

            UnityEngine.Vector3 pivotVector = m_CameraUpdateSystem.activeCameraController.pivot;
            float3 pivot = new float3(pivotVector.x, pivotVector.y, pivotVector.z);

            NativeArray<Entity> candidates = m_PlacedObjectQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                int found = 0;
                bool dumpedWallDecal = false;

                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity candidate = candidates[i];
                    Entity decalPrefab = EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab;

                    DecalPrefabInfo info;
                    if (!m_Catalog.TryGetByPrefab(decalPrefab, out info))
                    {
                        continue;
                    }

                    Transform transform = EntityManager.GetComponentData<Transform>(candidate);
                    float distance = math.distance(transform.m_Position, pivot);
                    if (distance > 60f)
                    {
                        continue;
                    }

                    // Euler degrees are far easier to compare by eye than a quaternion, and the
                    // axes tell us at a glance whether the projector is standing up or lying flat.
                    float3 euler = math.degrees(ToEuler(transform.m_Rotation));
                    float3 localUp = math.rotate(transform.m_Rotation, new float3(0f, 1f, 0f));

                    sb.AppendLine();
                    sb.AppendFormat(
                        "  {0}  at ({1:0.0},{2:0.0},{3:0.0})  {4:0.0}m away\n"
                      + "      rotation euler=({5:0.0},{6:0.0},{7:0.0})  localUp=({8:0.00},{9:0.00},{10:0.00})  size=({11:0.0},{12:0.0},{13:0.0})",
                        info.Name, transform.m_Position.x, transform.m_Position.y, transform.m_Position.z, distance,
                        euler.x, euler.y, euler.z,
                        localUp.x, localUp.y, localUp.z,
                        info.Size.x, info.Size.y, info.Size.z);

                    // The same fields we print for our own elements, so the two can be read
                    // side by side. Position, rotation and components have already been shown to
                    // match; what has never been compared is what the renderer made of each.
                    if (EntityManager.HasComponent<Game.Rendering.CullingInfo>(candidate))
                    {
                        Game.Rendering.CullingInfo culling =
                            EntityManager.GetComponentData<Game.Rendering.CullingInfo>(candidate);
                        float3 cullSize = culling.m_Bounds.max - culling.m_Bounds.min;
                        sb.AppendLine();
                        sb.AppendFormat(
                            "      culling: size=({0:0.0},{1:0.0},{2:0.0}) radius={3:0.0} mask={4} minLod={5} passed={6}",
                            cullSize.x, cullSize.y, cullSize.z, culling.m_Radius,
                            culling.m_Mask, culling.m_MinLod, culling.m_PassedCulling);
                    }

                    if (EntityManager.HasBuffer<Game.Rendering.MeshBatch>(candidate))
                    {
                        DynamicBuffer<Game.Rendering.MeshBatch> batches =
                            EntityManager.GetBuffer<Game.Rendering.MeshBatch>(candidate, true);
                        sb.AppendFormat("  batches={0}", batches.Length);
                        for (int b = 0; b < batches.Length; b++)
                        {
                            sb.AppendFormat(" [group={0} instance={1} mesh={2}]",
                                batches[b].m_GroupIndex, batches[b].m_InstanceIndex, batches[b].m_MeshIndex);
                        }
                    }

                    // The whole component list of the first wall-facing one. Position and rotation
                    // already match ours to within half a metre, so whatever makes a hand-placed
                    // decal paint the wall and ours paint the ground is a component we are not
                    // setting - and a diff finds it in one pass instead of ten guesses.
                    if (!dumpedWallDecal && math.abs(localUp.y) < 0.5f)
                    {
                        dumpedWallDecal = true;
                        sb.AppendLine();
                        sb.Append("      COMPONENTS: ").Append(DescribeComponents(candidate));
                    }

                    if (++found >= 12)
                    {
                        break;
                    }
                }

                if (found == 0)
                {
                    Mod.Log.Info("Seen Better Days: no hand-placed decal found within 60 m of the camera. "
                               + "Place one on a wall with Extra Detailing Tools (Snap to surface), "
                               + "point the camera at it, and press Ctrl+Alt+P again.");
                    return;
                }

                Mod.Log.Info("Seen Better Days: MEASURED " + found + " hand-placed decal(s) near the camera."
                           + " localUp is the projector axis - (0,1,0) means it is lying flat like a"
                           + " ground decal, a horizontal vector means it is facing a wall:" + sb);
            }
            finally
            {
                candidates.Dispose();
            }
        }

        /// <summary>Every component type on an entity, sorted, for diffing one against another.</summary>
        private string DescribeComponents(Entity entity)
        {
            NativeArray<ComponentType> types = EntityManager.GetComponentTypes(entity, Allocator.Temp);
            try
            {
                List<string> names = new List<string>(types.Length);
                for (int i = 0; i < types.Length; i++)
                {
                    string name = types[i].GetManagedType() == null ? types[i].ToString() : types[i].GetManagedType().FullName;
                    names.Add(name);
                }

                names.Sort(System.StringComparer.Ordinal);
                return string.Join(", ", names.ToArray());
            }
            finally
            {
                types.Dispose();
            }
        }

        /// <summary>Quaternion to euler (radians, ZXY like Unity), for log-readable rotations.</summary>
        private static float3 ToEuler(quaternion q)
        {
            float4 v = q.value;
            float sinX = 2f * (v.w * v.x - v.y * v.z);
            sinX = math.clamp(sinX, -1f, 1f);

            return new float3(
                math.asin(sinX),
                math.atan2(2f * (v.w * v.y + v.z * v.x), 1f - 2f * (v.x * v.x + v.y * v.y)),
                math.atan2(2f * (v.w * v.z + v.x * v.y), 1f - 2f * (v.x * v.x + v.z * v.z)));
        }

        /// <summary>
        /// Drops one decal flat on the ground under the camera, with nothing else involved.
        /// The smallest question left worth asking: can this mod draw a decal at all?
        /// </summary>
        /// <summary>
        /// Weathers the selected building with the full decal plan, placed by raycast.
        ///
        /// The automatic version of what Ctrl+Alt+H proved by hand: the facade rectangle still
        /// decides where on the wall each mark belongs, but a ray finds the surface underneath it
        /// and the mark goes there, with its rotation taken from the surface.
        ///
        /// The log reports how many marks found real geometry and how many candidate positions
        /// were skipped because the ray missed. Nothing is placed at the bounding-box plane.
        /// </summary>
        /// <summary>
        /// Forces the selected building to one of the five states and holds it there.
        ///
        /// The reason this is worth a hotkey: a settled city does not produce the five states for
        /// inspection. The last census of this one found 1273 Maintained, 863 Aged, 5 Worn and
        /// nothing worse - so four of the five states had never been seen on a real building, and
        /// judging whether they read differently meant waiting for a city to decay on its own.
        /// Putting them side by side on one street answers in a minute what play would answer in
        /// an hour, if at all.
        ///
        /// Both layers follow: the colour is reapplied at once, and the decals are dropped so the
        /// detail layer rebuilds them against the new state on its next pass.
        /// </summary>
        private void SetWeatheringByHand(VisualState state, float weathering)
        {
            if (Mod.Settings == null || !Mod.Settings.EnableStateKeys)
            {
                Mod.Log.Info("Seen Better Days: setting a building's state by hand is switched off. "
                           + "Turn on \"Set a building's state by hand\" under Advanced in the "
                           + "mod's options.");
                return;
            }

            Entity building;
            BuildingCategory category;
            Entity prefabEntity;
            int level;

            if (!TryResolveTarget(out building, out category, out prefabEntity, out level))
            {
                return;
            }

            BuildingWeatheringSystem weathering2 = World.GetExistingSystemManaged<BuildingWeatheringSystem>();
            if (weathering2 == null)
            {
                Mod.Log.Warn("Seen Better Days: no weathering system.");
                return;
            }

            string description;
            if (!weathering2.Pin(building, weathering, out description))
            {
                Mod.Log.Warn("Seen Better Days: could not set " + Describe(building) + " - " + description);
                return;
            }

            // The marks belong to the old state, so they go now rather than when the round-robin
            // next reaches this building - during a side-by-side comparison, stale marks under a
            // new colour are not a delay, they are a wrong answer.
            string marks;
            DecalDetailSystem detail = World.GetExistingSystemManaged<DecalDetailSystem>();
            if (detail == null)
            {
                m_Renderer.Remove(building);
                marks = "no detail layer";
            }
            else if (!detail.RebuildNow(building, out marks))
            {
                marks = "no marks - " + marks;
            }

            Mod.Log.Info("Seen Better Days: " + Describe(building) + " held at " + description
                       + " by hand | " + marks + " | " + weathering2.PinnedCount
                       + " building(s) now held. Ctrl+Alt+F6 gives it back to the simulation.");
        }

        private void ReleaseWeatheringByHand()
        {
            Entity building;
            BuildingCategory category;
            Entity prefabEntity;
            int level;

            if (!TryResolveTarget(out building, out category, out prefabEntity, out level))
            {
                return;
            }

            BuildingWeatheringSystem weathering = World.GetExistingSystemManaged<BuildingWeatheringSystem>();
            if (weathering == null || !weathering.Unpin(building))
            {
                Mod.Log.Info("Seen Better Days: " + Describe(building) + " was not being held by hand.");
                return;
            }

            m_Renderer.Remove(building);
            Mod.Log.Info("Seen Better Days: " + Describe(building)
                       + " handed back to the simulation; it will snap to whatever its "
                       + "circumstances deserve on the next pass.");
        }

        /// <summary>The decal renderer, shared with <see cref="DecalDetailSystem"/> so that the
        /// automatic layer and the hand-driven tests keep one record of what is on the city, and
        /// one clear-out clears both.</summary>
        public DecalObjectOverlayRenderer DecalRenderer
        {
            get { return m_Renderer; }
        }

        private void ApplyDecalsToTarget()
        {
            Entity building;
            BuildingCategory category;
            Entity prefabEntity;
            int level;

            if (!TryResolveTarget(out building, out category, out prefabEntity, out level))
            {
                return;
            }

            uint seed = (uint)building.Index * 2654435761u + 1u;
            BuildingVisualProfile profile = BuildingVisualProfile.Debug(VisualState.Decayed, seed);

            int placed;
            string failure;
            if (!m_Renderer.Apply(building, profile, out placed, out failure))
            {
                Mod.Log.Warn("Seen Better Days: could not place decals on " + Describe(building)
                           + " - " + failure);
                return;
            }

            Mod.Log.Info("Seen Better Days: placed " + placed + " decal(s) on " + Describe(building)
                       + " | " + m_Renderer.LastPlacementReport
                       + " | profile: " + profile);

            JumpCameraTo(building);
            LogStatus();
        }

        /// <summary>
        /// Puts one decal exactly where the cursor's ray meets the world, oriented by the surface.
        ///
        /// The one experiment that separates the two remaining explanations for why our decals
        /// never appeared. See <c>DecalObjectOverlayRenderer.ApplyAtSurface</c>.
        /// </summary>
        private void PlaceDecalAtCursor()
        {
            if (m_ToolRaycastSystem == null)
            {
                m_ToolRaycastSystem = World.GetOrCreateSystemManaged<Game.Tools.ToolRaycastSystem>();
            }

            Game.Common.RaycastResult result;
            if (!m_ToolRaycastSystem.GetRaycastResult(out result) || result.m_Owner == Entity.Null)
            {
                Mod.Log.Warn("Seen Better Days: the cursor is not over anything the game raycasts. "
                           + "Point at a building wall and try again.");
                return;
            }

            DecalPrefabInfo decal = m_Renderer.ForcedDecal;
            if (decal == null)
            {
                List<DecalPrefabInfo> candidates = m_Catalog.PickTestSheet(1, 12f, requireBaseColor: true);
                if (candidates.Count == 0)
                {
                    candidates = m_Catalog.PickTestSheet(1, 12f, requireBaseColor: false);
                }

                if (candidates.Count > 0)
                {
                    decal = candidates[0];
                }
            }

            if (decal == null)
            {
                Mod.Log.Warn("Seen Better Days: no decal available to place.");
                return;
            }

            float3 hit = result.m_Hit.m_HitPosition;
            float3 normal = result.m_Hit.m_HitDirection;

            Mod.Log.Info("Seen Better Days: cursor ray hit entity " + result.m_Hit.m_HitEntity.Index
                       + " (owner " + result.m_Owner.Index + ") at "
                       + hit.x.ToString("0.00") + ", " + hit.y.ToString("0.00") + ", " + hit.z.ToString("0.00")
                       + " with direction "
                       + normal.x.ToString("0.00") + ", " + normal.y.ToString("0.00") + ", " + normal.z.ToString("0.00")
                       + " | placing " + decal.Name
                       + " (offset " + m_Renderer.NormalOffsetMetres.ToString("0.00")
                       + "m, inverted=" + m_Renderer.InvertProjection + ")");

            string failure;
            if (!m_Renderer.ApplyAtSurface(hit, normal, decal, out failure))
            {
                Mod.Log.Warn("Seen Better Days: could not place it - " + failure);
                return;
            }

            Mod.Log.Info("Seen Better Days: placed. If you can see it, the bounding-box plane was "
                       + "the fault all along. If not, press Ctrl+Alt+K to flip the projection and "
                       + "Ctrl+Alt+H again before concluding anything.");
        }

        private void DropGroundProbe()
        {
            if (m_CameraUpdateSystem == null)
            {
                m_CameraUpdateSystem = World.GetExistingSystemManaged<Game.Rendering.CameraUpdateSystem>();
            }

            if (m_CameraUpdateSystem == null || m_CameraUpdateSystem.activeCameraController == null)
            {
                Mod.Log.Warn("Seen Better Days: no active camera, cannot place a ground probe.");
                return;
            }

            UnityEngine.Vector3 pivot = m_CameraUpdateSystem.activeCameraController.pivot;

            DecalPrefabInfo decal = m_Renderer.ForcedDecal;
            if (decal == null)
            {
                List<DecalPrefabInfo> candidates = m_Catalog.PickTestSheet(1, 12f, requireBaseColor: true);
                if (candidates.Count == 0)
                {
                    candidates = m_Catalog.PickTestSheet(1, 12f, requireBaseColor: false);
                }

                if (candidates.Count > 0)
                {
                    decal = candidates[0];
                }
            }

            if (decal == null)
            {
                Mod.Log.Warn("Seen Better Days: no decal available for a ground probe.");
                return;
            }

            string failure;
            if (!m_Renderer.ApplyGroundProbe(new float3(pivot.x, pivot.y, pivot.z), decal, out failure))
            {
                Mod.Log.Warn("Seen Better Days: ground probe failed - " + failure);
                return;
            }

            Mod.Log.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Seen Better Days: GROUND PROBE {0} dropped flat at the camera pivot ({1:0.0},{2:0.0},{3:0.0}). "
              + "It is lying on the ground, unrotated, attached to nothing. If this is not visible, "
              + "nothing about facades matters yet.",
                decal, pivot.x, pivot.y, pivot.z));

            ScheduleDiagnosis(Entity.Null);
            LogStatus();
        }


        /// <summary>
        /// Chooses which parts of the colour weathering may take, and redraws whatever is already
        /// weathered so the three can be compared on the same buildings.
        ///
        /// One key per response rather than one key that cycles: cycling means the answer to
        /// "which one am I looking at" lives in the log, behind an alt-tab, which makes the
        /// comparison useless at exactly the moment it matters.
        /// </summary>
        private void SetWeatheringResponse(WeatheringResponse next)
        {
            MeshColorOverlayRenderer.Response = next;

            int redrawn = m_ColourRenderer.Reapply();

            BuildingWeatheringSystem weathering =
                World.GetExistingSystemManaged<BuildingWeatheringSystem>();
            if (weathering != null)
            {
                redrawn += weathering.Reapply();
            }

            string described;
            switch (next)
            {
                case WeatheringResponse.DarknessOnly:
                    described = "DarknessOnly - hue and saturation untouched, the building's own colour just darker";
                    break;
                case WeatheringResponse.Fading:
                    described = "Fading - darker and washed out, but no hue invented";
                    break;
                default:
                    described = "Full - darker, washed out, and tinted by family (moss green, rust ochre)";
                    break;
            }

            Mod.Log.Info("Seen Better Days: weathering response is now " + described
                       + "; " + redrawn + " building(s) redrawn.");
        }

        /// <summary>
        /// Hands the city over to the simulation, or takes it back.
        ///
        /// Off by default: with it running, every manual experiment is corrected within a second
        /// by whatever the building's circumstances actually justify, which is right for the mod
        /// and useless for a harness.
        /// </summary>
        private void ToggleAutomaticWeathering()
        {
            BuildingWeatheringSystem weathering =
                World.GetExistingSystemManaged<BuildingWeatheringSystem>();

            if (weathering == null)
            {
                Mod.Log.Warn("Seen Better Days: the weathering system is not in this world.");
                return;
            }

            weathering.Enabled = !weathering.Enabled;

            if (weathering.Enabled)
            {
                Mod.Log.Info("Seen Better Days: automatic weathering is ON. Every growable will be "
                           + "weathered to match its own circumstances - condition, services, "
                           + "abandonment - a few hundred buildings per pass. Manual overrides will "
                           + "be corrected.");
            }
            else
            {
                int reset = weathering.ResetAll();
                Mod.Log.Info("Seen Better Days: automatic weathering is OFF; " + reset
                           + " building(s) put back to their original colours.");
            }
        }

        private void ScheduleDiagnosis(Entity building)
        {
            m_PendingDiagnosis = building;

            // Long enough for the object, search, culling and batching systems to have run.
            m_DiagnosisCountdown = 10;
        }

        private void RemoveFromTarget()
        {
            Entity building = SelectedEntity;

            if (building == Entity.Null || !m_Renderer.Has(building))
            {
                // Nothing selected, or the selection carries nothing: fall back to whichever
                // building we most recently weathered, so the key still does something useful.
                m_ColourRenderer.CollectTrackedBuildings(m_Scratch);
            if (m_Scratch.Count == 0)
            {
                m_Renderer.CollectTrackedBuildings(m_Scratch);
            }

                if (m_Scratch.Count == 0)
                {
                    Mod.Log.Info("Seen Better Days: nothing to remove.");
                    return;
                }

                building = m_Scratch[m_Scratch.Count - 1];
            }

            bool removedColour = m_ColourRenderer.Remove(building);
            if (m_Renderer.Remove(building) || removedColour)
            {
                Mod.Log.Info("Seen Better Days: removed the overlay from " + Describe(building) + ".");
                LogStatus();
            }
            else
            {
                Mod.Log.Info("Seen Better Days: " + Describe(building) + " had no overlay.");
            }
        }

        private void RemoveEverything()
        {
            int cleared = m_Renderer.RemoveAll();
            int colourCleared = m_ColourRenderer.RemoveAll();
            int strays = m_Renderer.SweepStrayOverlays();

            Mod.Log.Info("Seen Better Days: cleared " + cleared + " decal building(s), "
                       + colourCleared + " recoloured building(s); swept "
                       + strays + " overlay entit(ies) in the world.");
        }

        /// <summary>
        /// Removes every decal entity created by this mod before the serializer snapshots the
        /// world. Unlike the colour layer, these developer-harness decals are not reconstructed
        /// after saving; the player can place a fresh deterministic sample with Ctrl+Alt+V.
        /// </summary>
        public int SuspendDecalsForSave()
        {
            int trackedEntities = m_Renderer.OverlayCount;
            m_Renderer.RemoveAll();
            int strayEntities = m_Renderer.SweepStrayOverlays();
            return trackedEntities + strayEntities;
        }

        private void CycleFacade()
        {
            m_Renderer.Side = (FacadeSide)(((int)m_Renderer.Side + 1) & 3);
            Mod.Log.Info("Seen Better Days: new overlays will go on the " + m_Renderer.Side
                       + " facade. Re-apply (Ctrl+Alt+A) to see it move.");
        }

        private void CycleForcedDecal()
        {
            List<DecalPrefabInfo> usable = m_Renderer.AllowNonBuildingDecals
                ? m_Catalog.All
                : m_Catalog.BuildingCapable;
            if (usable.Count == 0)
            {
                Mod.Log.Warn("Seen Better Days: no building-capable decal to cycle through.");
                return;
            }

            m_ForcedDecalIndex++;
            if (m_ForcedDecalIndex >= usable.Count)
            {
                m_ForcedDecalIndex = -1;
                m_Renderer.ForcedDecal = null;
                Mod.Log.Info("Seen Better Days: decal choice back to automatic.");
                return;
            }

            m_Renderer.ForcedDecal = usable[m_ForcedDecalIndex];
            Mod.Log.Info("Seen Better Days: forcing decal " + (m_ForcedDecalIndex + 1) + "/" + usable.Count
                       + " -> " + m_Renderer.ForcedDecal + ". Re-apply (Ctrl+Alt+A) to see it.");
        }

        private void DescribeTarget()
        {
            Entity building = SelectedEntity;
            if (building == Entity.Null)
            {
                Mod.Log.Info("Seen Better Days: nothing selected.");
                LogStatus();
                return;
            }

            BuildingWeatheringSystem weathering =
                World.GetExistingSystemManaged<BuildingWeatheringSystem>();

            Mod.Log.Info("Seen Better Days: colours of " + building.Index + " | "
                       + (weathering != null ? weathering.Describe(building) : m_ColourRenderer.Describe(building)));

            string reason;
            Entity prefabEntity;
            int level;
            BuildingCategory category = BuildingClassifier.Classify(
                EntityManager, building, out prefabEntity, out level, out reason);

            string prefabName = "<none>";
            PrefabBase prefabBase;
            if (prefabEntity != Entity.Null && m_PrefabSystem.TryGetPrefab(prefabEntity, out prefabBase) && prefabBase != null)
            {
                prefabName = prefabBase.name;
            }

            string geometryText = "no ObjectGeometryData";
            if (prefabEntity != Entity.Null && EntityManager.HasComponent<ObjectGeometryData>(prefabEntity))
            {
                ObjectGeometryData geometry = EntityManager.GetComponentData<ObjectGeometryData>(prefabEntity);
                float3 size = geometry.m_Bounds.max - geometry.m_Bounds.min;
                geometryText = string.Format(
                    CultureInfo.InvariantCulture,
                    "bounds min=({0:0.0},{1:0.0},{2:0.0}) max=({3:0.0},{4:0.0},{5:0.0}) size=({6:0.0},{7:0.0},{8:0.0})",
                    geometry.m_Bounds.min.x, geometry.m_Bounds.min.y, geometry.m_Bounds.min.z,
                    geometry.m_Bounds.max.x, geometry.m_Bounds.max.y, geometry.m_Bounds.max.z,
                    size.x, size.y, size.z);
            }

            string transformText = "no Transform";
            if (EntityManager.HasComponent<Transform>(building))
            {
                Transform transform = EntityManager.GetComponentData<Transform>(building);
                transformText = string.Format(
                    CultureInfo.InvariantCulture,
                    "position=({0:0.0},{1:0.0},{2:0.0}) rotation=({3:0.000},{4:0.000},{5:0.000},{6:0.000})",
                    transform.m_Position.x, transform.m_Position.y, transform.m_Position.z,
                    transform.m_Rotation.value.x, transform.m_Rotation.value.y,
                    transform.m_Rotation.value.z, transform.m_Rotation.value.w);
            }

            Mod.Log.Info("Seen Better Days: selected " + Describe(building) + "\n"
                       + "             prefab=" + prefabName + " (entity " + prefabEntity.Index + ")\n"
                       + "             category=" + category + " level=" + level
                       + (reason == null ? string.Empty : " (" + reason + ")") + "\n"
                       + "             " + transformText + "\n"
                       + "             " + geometryText + "\n"
                       + "             has overlay=" + m_Renderer.Has(building) + "\n"
                       + "             " + DescribeDirtChannel(building) + "\n"
                       + "             " + DescribeReceivingLayers(prefabEntity));

            LogStatus();
        }

        /// <summary>
        /// The game's own dirt state for this building: the simulation input and the byte the
        /// renderer reads. Together they say whether DirtynessSystem has acted on our change yet.
        /// </summary>
        private string DescribeDirtChannel(Entity building)
        {
            string conditionText = EntityManager.HasComponent<BuildingCondition>(building)
                ? EntityManager.GetComponentData<BuildingCondition>(building).m_Condition.ToString(CultureInfo.InvariantCulture)
                : "none";

            string dirtText = "none";
            if (EntityManager.HasComponent<Game.Objects.Surface>(building))
            {
                Game.Objects.Surface surface = EntityManager.GetComponentData<Game.Objects.Surface>(building);
                dirtText = surface.m_Dirtyness + "/255 (wetness=" + surface.m_Wetness + " snow=" + surface.m_SnowAmount + ")";
            }

            return "dirt channel: BuildingCondition=" + conditionText + " Surface.m_Dirtyness=" + dirtText
                 + " abandoned=" + EntityManager.HasComponent<Game.Buildings.Abandoned>(building);
        }

        /// <summary>
        /// What decal layers this building's own meshes accept.
        ///
        /// This is the one assumption the whole approach rests on and the one never checked in
        /// the game itself: ObjectInitializeSystem is supposed to give every building mesh
        /// MeshData.m_DecalLayer |= DecalLayers.Buildings, and ManagedBatchSystem writes that into
        /// the material as colossal_DecalLayerMask. A decal only paints a surface when the two
        /// masks overlap, so if this prints a layer set without Buildings then no decal will ever
        /// mark this facade, no matter where or how it is placed.
        /// </summary>
        private string DescribeReceivingLayers(Entity prefabEntity)
        {
            if (prefabEntity == Entity.Null || !EntityManager.HasBuffer<SubMesh>(prefabEntity))
            {
                return "receiving layers: prefab has no SubMesh buffer";
            }

            DynamicBuffer<SubMesh> subMeshes = EntityManager.GetBuffer<SubMesh>(prefabEntity, true);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("receiving layers over ").Append(subMeshes.Length).Append(" submesh(es):");

            for (int i = 0; i < subMeshes.Length && i < 8; i++)
            {
                Entity meshEntity = subMeshes[i].m_SubMesh;
                if (!EntityManager.HasComponent<MeshData>(meshEntity))
                {
                    sb.Append(" [").Append(i).Append("] no MeshData");
                    continue;
                }

                MeshData meshData = EntityManager.GetComponentData<MeshData>(meshEntity);
                sb.Append(" [").Append(i).Append("] decalLayer=").Append(meshData.m_DecalLayer)
                  .Append(" state=").Append(meshData.m_State)
                  .Append(" layers=").Append(meshData.m_DefaultLayers);
            }

            return sb.ToString();
        }

        private void LogStatus()
        {
            Mod.Log.Info("Seen Better Days: backend=" + m_Renderer.Name
                       + " available=" + m_Renderer.IsAvailable
                       + " buildings=" + m_Renderer.TrackedBuildingCount
                       + " elements=" + m_Renderer.OverlayCount
                       + " facade=" + m_Renderer.Side
                       + " offset=" + m_Renderer.NormalOffsetMetres.ToString("0.00") + "m"
                       + " invert=" + m_Renderer.InvertProjection
                       + " allowNonBuildingDecals=" + m_Renderer.AllowNonBuildingDecals
                       + " forcedDecal=" + (m_Renderer.ForcedDecal == null ? "auto" : m_Renderer.ForcedDecal.Name));
        }

        /// <summary>
        /// Resolved lazily rather than in OnCreate. This system is created while the mod loads,
        /// which is early enough that forcing the selection UI system into existence there
        /// would be building it ahead of its own dependencies.
        /// </summary>
        private Entity SelectedEntity
        {
            get
            {
                if (m_SelectedInfoUISystem == null)
                {
                    m_SelectedInfoUISystem = World.GetExistingSystemManaged<SelectedInfoUISystem>();
                }

                return m_SelectedInfoUISystem == null ? Entity.Null : m_SelectedInfoUISystem.selectedEntity;
            }
        }

        private string Describe(Entity entity)
        {
            return "entity " + entity.Index + ":" + entity.Version;
        }

        [Preserve]
        public BuildingOverlayTestSystem()
        {
        }
    }
}
