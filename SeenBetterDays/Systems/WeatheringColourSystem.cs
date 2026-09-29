using System.Collections.Generic;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Rendering;
using SeenBetterDays.Data;
using SeenBetterDays.Rendering;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Runs straight after the game's MeshColorSystem, in PreCulling, and darkens the colours it
    /// has just recomputed for weathered buildings, before the frame is drawn. See
    /// <see cref="MeshColorOverlayRenderer"/> for why the weathering lives here and not in the
    /// building's saved colour.
    /// </summary>
    public partial class WeatheringColourSystem : GameSystemBase
    {
        /// <summary>
        /// Buildings checked for a rewrite no tag announced, per renderer, once every
        /// <see cref="VerifyEveryFrames"/> frames. Reading a colour makes the frame wait for the
        /// jobs that compute colours; done every frame it cost 0.13-0.25 ms a frame on average,
        /// with spikes past 10 ms. The tags catch the rewrites that matter as they happen; this is
        /// only a safety net, and a slow one is enough.
        /// </summary>
        private const int VerifyPerCheck = 32;
        private const int VerifyEveryFrames = 30;
        private int m_VerifyCountdown;

        // Diagnosis of buildings turning clean on hover (2026-09-28): whether this pass really
        // runs after MeshColorSystem, and what a hovered building carries when it does.
        private Game.Rendering.MeshColorSystem m_MeshColorSystem;
        private Game.Tools.ToolRaycastSystem m_RaycastSystem;

        /// <summary>The building under the cursor, walking up from whatever part was hit.</summary>
        private Entity HoveredBuilding()
        {
            if (m_RaycastSystem == null)
            {
                m_RaycastSystem = World.GetExistingSystemManaged<Game.Tools.ToolRaycastSystem>();
            }

            Game.Common.RaycastResult result;
            if (m_RaycastSystem == null || !m_RaycastSystem.GetRaycastResult(out result))
            {
                return Entity.Null;
            }

            Entity hit = result.m_Owner;
            for (int guard = 0; guard < 8 && hit != Entity.Null && EntityManager.Exists(hit); guard++)
            {
                if (EntityManager.HasComponent<Building>(hit))
                {
                    return hit;
                }

                if (!EntityManager.HasComponent<Owner>(hit))
                {
                    break;
                }

                hit = EntityManager.GetComponentData<Owner>(hit).m_Owner;
            }

            return Entity.Null;
        }
        private int m_OrderReports;

        private EntityQuery m_RentersUpdatedQuery;
        private readonly HashSet<Entity> m_RentersChanged = new HashSet<Entity>();

        // What the pass costs, reported every ReportFrames frames: the reason for moving the
        // weathering here was to make it cheaper and safer, and that has to be measured.
        private const int ReportFrames = 1800;
        private int m_Frames;
        private int m_FramesWithWork;
        private int m_Darkened;
        private double m_TotalMilliseconds;
        private double m_MaxMilliseconds;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_RentersUpdatedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Event>(),
                ComponentType.ReadOnly<RentersUpdated>());
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_OrderReports < 3)
            {
                if (m_MeshColorSystem == null)
                {
                    m_MeshColorSystem = World.GetExistingSystemManaged<Game.Rendering.MeshColorSystem>();
                }

                if (m_MeshColorSystem != null && m_OrderReports == 2)
                {
                    // Name every system that ran between MeshColorSystem and this pass: the one
                    // that copies colours for the screen must be among them, and the pass has to
                    // run before it.
                    uint after = m_MeshColorSystem.LastSystemVersion;
                    var between = new System.Text.StringBuilder();
                    foreach (ComponentSystemBase system in World.Systems)
                    {
                        if (system != this && system.LastSystemVersion > after)
                        {
                            between.Append(system.GetType().FullName).Append(" (").Append(system.LastSystemVersion).Append(") ");
                        }
                    }

                    Mod.Log.Info("Seen Better Days: systems that ran between MeshColorSystem ("
                               + after + ") and the colour pass: " + (between.Length > 0 ? between.ToString() : "none"));
                }

                if (m_MeshColorSystem != null)
                {
                    m_OrderReports++;
                    Mod.Log.Info("Seen Better Days: colour pass order - MeshColorSystem last ran at version "
                               + m_MeshColorSystem.LastSystemVersion + ", this pass at "
                               + LastSystemVersion + ", world now " + EntityManager.GlobalSystemVersion
                               + " (MeshColorSystem ran this frame before us if its version is the newer of the two).");
                }
            }

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            MeshColorOverlayRenderer.HoveredBuilding = HoveredBuilding();
            m_RentersChanged.Clear();
            if (!m_RentersUpdatedQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<RentersUpdated> events = m_RentersUpdatedQuery.ToComponentDataArray<RentersUpdated>(Allocator.Temp);
                for (int i = 0; i < events.Length; i++)
                {
                    m_RentersChanged.Add(events[i].m_Property);
                }

                events.Dispose();
            }

            IReadOnlyList<MeshColorOverlayRenderer> renderers = MeshColorOverlayRenderer.All;
            int verifyBudget = 0;
            if (--m_VerifyCountdown <= 0)
            {
                m_VerifyCountdown = VerifyEveryFrames;
                verifyBudget = VerifyPerCheck;
            }

            int darkened = 0;
            for (int i = 0; i < renderers.Count; i++)
            {
                renderers[i].DarkenRecoloured(m_RentersChanged, verifyBudget);
                darkened += renderers[i].LastPassDarkened;
            }

            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
            m_Frames++;
            m_TotalMilliseconds += ms;
            m_MaxMilliseconds = System.Math.Max(m_MaxMilliseconds, ms);
            if (darkened > 0)
            {
                m_FramesWithWork++;
                m_Darkened += darkened;
            }

            if (m_Frames >= ReportFrames)
            {
                Mod.Log.Info("Seen Better Days: colour pass over " + m_Frames + " frames - "
                           + (m_TotalMilliseconds / m_Frames).ToString("0.000") + " ms average, "
                           + m_MaxMilliseconds.ToString("0.00") + " ms slowest, "
                           + m_Darkened + " building colour(s) darkened in " + m_FramesWithWork + " frame(s).");
                m_Frames = 0;
                m_FramesWithWork = 0;
                m_Darkened = 0;
                m_TotalMilliseconds = 0d;
                m_MaxMilliseconds = 0d;
            }
        }
    }

    /// <summary>
    /// Resets custom colours on growable buildings when the player asks. Versions up to 0.1.4
    /// could save weathering there. The old PristineMeshColor marker was runtime-only, so a loaded
    /// city has no reliable way to distinguish those colours from player-selected ones.
    /// </summary>
    public partial class OldColourRepairSystem : GameSystemBase
    {
        /// <summary>Buildings repaired per frame: spread out, because the repair that ran on the
        /// whole city in one frame was followed by a crash.</summary>
        private const int RepairPerFrame = 16;

        private Game.Serialization.SaveGameSystem m_SaveGameSystem;
        private EntityQuery m_GrowablesQuery;
        private NativeArray<Entity> m_RepairQueue;
        private int m_RepairCursor = -1;
        private int m_Repaired;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SaveGameSystem = World.GetOrCreateSystemManaged<Game.Serialization.SaveGameSystem>();

            // BuildingCondition narrows this to growables; the classifier below makes it exact.
            // PristineMeshColor cannot be used here: the game did not save that mod component.
            m_GrowablesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(),
                    ComponentType.ReadOnly<BuildingCondition>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                },
            });
        }

        [Preserve]
        protected override void OnDestroy()
        {
            if (m_RepairQueue.IsCreated)
            {
                m_RepairQueue.Dispose();
            }

            base.OnDestroy();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (SaveMutationGate.IsBlocked(m_SaveGameSystem))
            {
                return;
            }

            if (!m_GrowablesQuery.IsEmptyIgnoreFilter && Mod.ConsumeOldColourRepairRequest())
            {
                StartRepair();
            }

            AdvanceRepair();
        }

        // ---- Explicit reset of colours saved by earlier versions -------------------------------

        /// <summary>
        /// Versions up to 0.1.4 kept the weathered colour in the building's custom colour, which
        /// is saved with the city; in some cities it was saved darkened and then darkened again on
        /// every load. Since the old snapshot is absent from loaded cities, this must reset every
        /// custom-coloured growable, including any colour the player chose. The option warns of
        /// that cost and requires confirmation.
        /// </summary>
        private void StartRepair()
        {
            if (m_RepairQueue.IsCreated)
            {
                m_RepairQueue.Dispose();
            }

            m_RepairQueue = m_GrowablesQuery.ToEntityArray(Allocator.Persistent);
            m_RepairCursor = 0;
            m_Repaired = 0;
            Mod.Log.Info("Seen Better Days: checking " + m_RepairQueue.Length
                       + " growable building(s) for saved custom colours, " + RepairPerFrame
                       + " a frame.");
        }

        private void AdvanceRepair()
        {
            if (m_RepairCursor < 0 || !m_RepairQueue.IsCreated)
            {
                return;
            }

            int end = System.Math.Min(m_RepairCursor + RepairPerFrame, m_RepairQueue.Length);
            for (int i = m_RepairCursor; i < end; i++)
            {
                Entity building = m_RepairQueue[i];
                string reason;
                Entity prefab;
                int level;
                if (!EntityManager.Exists(building)
                    || !BuildingClassifier.Classify(EntityManager, building, out prefab, out level, out reason).IsEligible())
                {
                    continue;
                }

                if (!EntityManager.HasBuffer<CustomMeshColor>(building))
                {
                    continue;
                }

                bool hasColour = EntityManager.GetBuffer<CustomMeshColor>(building, true).Length > 0;
                bool enabled = EntityManager.IsComponentEnabled<CustomMeshColor>(building);
                if (!hasColour && !enabled)
                {
                    continue;
                }

                // Same representation as the game's reset to default: empty and switched off.
                EntityManager.GetBuffer<CustomMeshColor>(building).Clear();
                EntityManager.SetComponentEnabled<CustomMeshColor>(building, false);
                // The old snapshot can still exist if this city has not been reloaded yet.
                if (EntityManager.HasBuffer<PristineMeshColor>(building))
                {
                    EntityManager.RemoveComponent<PristineMeshColor>(building);
                }
                if (!EntityManager.HasComponent<BatchesUpdated>(building))
                {
                    EntityManager.AddComponent<BatchesUpdated>(building);
                }

                m_Repaired++;
            }

            m_RepairCursor = end;
            if (m_RepairCursor >= m_RepairQueue.Length)
            {
                Mod.Log.Info("Seen Better Days: repair finished - " + m_Repaired
                           + " building(s) show the game's own colour again.");
                m_RepairQueue.Dispose();
                m_RepairCursor = -1;
            }
        }
    }
}
