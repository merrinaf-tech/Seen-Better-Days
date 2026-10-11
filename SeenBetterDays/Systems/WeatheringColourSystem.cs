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

        private EntityQuery m_RentersUpdatedQuery;
        private EntityQuery m_RecomputedQuery;
        private readonly HashSet<Entity> m_RentersChanged = new HashSet<Entity>();

        /// <summary>Temporary copies of buildings - drawn by the game in place of a building while
        /// it is hovered or handled by a tool - and the colour last written on each.</summary>
        private EntityQuery m_TempCopiesQuery;
        private readonly Dictionary<Entity, UnityEngine.Color> m_TempWritten = new Dictionary<Entity, UnityEngine.Color>();
        private readonly List<Entity> m_TempGone = new List<Entity>();

        /// <summary>
        /// Darkens the temporary copies of weathered buildings with their original's weathering.
        ///
        /// While a building is hovered the game draws a Temp copy in its place, whose colour it
        /// builds from the building's own colour, never from the darkened one: with the weathering
        /// moved out of that colour, hovering showed the building clean. A copy is darkened once
        /// per fresh colour the game gives it, known by its differing from what was written last.
        /// Copies exist only while hovering or using a tool, so this is usually no work at all.
        /// </summary>
        private int DarkenTempCopies(IReadOnlyList<MeshColorOverlayRenderer> renderers)
        {
            if (m_TempCopiesQuery.IsEmptyIgnoreFilter)
            {
                if (m_TempWritten.Count > 0)
                {
                    m_TempWritten.Clear();
                }

                return 0;
            }

            int darkened = 0;
            NativeArray<Entity> copies = m_TempCopiesQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < copies.Length; i++)
                {
                    Entity copy = copies[i];
                    Entity original = EntityManager.GetComponentData<Game.Tools.Temp>(copy).m_Original;
                    if (original == Entity.Null)
                    {
                        continue;
                    }

                    // Read a copy's colour only when the game has just given it one: reading makes
                    // the frame wait for the colour jobs, and doing it for every copy on every
                    // frame took the pass from about 0.1 ms to 0.44 ms on average.
                    bool fresh = !m_TempWritten.ContainsKey(copy)
                              || EntityManager.HasComponent<BatchesUpdated>(copy)
                              || EntityManager.HasComponent<Updated>(copy);
                    if (!fresh)
                    {
                        continue;
                    }

                    BuildingVisualProfile profile = default;
                    bool weathered = false;
                    for (int r = 0; r < renderers.Count && !weathered; r++)
                    {
                        weathered = renderers[r].TryGetProfile(original, out profile);
                    }

                    if (!weathered)
                    {
                        continue;
                    }

                    DynamicBuffer<MeshColor> colours = EntityManager.GetBuffer<MeshColor>(copy);
                    if (colours.Length == 0)
                    {
                        continue;
                    }

                    UnityEngine.Color now = colours[0].m_ColorSet.m_Channel0;
                    UnityEngine.Color last;
                    if (m_TempWritten.TryGetValue(copy, out last)
                        && System.Math.Abs(now.r - last.r) < 0.001f
                        && System.Math.Abs(now.g - last.g) < 0.001f
                        && System.Math.Abs(now.b - last.b) < 0.001f)
                    {
                        continue;
                    }

                    MeshColorOverlayRenderer.Darken(colours, profile);
                    m_TempWritten[copy] = colours[0].m_ColorSet.m_Channel0;
                    darkened++;
                }

                // Forget copies the game has removed.
                if (m_TempWritten.Count > copies.Length)
                {
                    m_TempGone.Clear();
                    foreach (KeyValuePair<Entity, UnityEngine.Color> pair in m_TempWritten)
                    {
                        if (!EntityManager.Exists(pair.Key) || !EntityManager.HasComponent<Game.Tools.Temp>(pair.Key))
                        {
                            m_TempGone.Add(pair.Key);
                        }
                    }

                    for (int i = 0; i < m_TempGone.Count; i++)
                    {
                        m_TempWritten.Remove(m_TempGone[i]);
                    }
                }
            }
            finally
            {
                copies.Dispose();
            }

            return darkened;
        }

        // What the pass costs, reported every ReportFrames frames: the reason for moving the
        // weathering here was to make it cheaper and safer, and that has to be measured.
        private const int ReportFrames = 1800;
        private int m_Frames;
        private int m_FramesWithWork;
        private int m_Darkened;
        private double m_TotalMilliseconds;
        private double m_MaxMilliseconds;
        private double m_TempMilliseconds;
        private double m_RentersMilliseconds;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_RentersUpdatedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Event>(),
                ComponentType.ReadOnly<RentersUpdated>());

            m_RecomputedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>() },
                Any = new[]
                {
                    ComponentType.ReadOnly<BatchesUpdated>(),
                    ComponentType.ReadOnly<Updated>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                },
            });

            m_TempCopiesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadWrite<MeshColor>(),
                    // Buildings only: with a tool open, its previews (trees, props, roads) get
                    // fresh colours every frame, and listing them cost 0.25 ms a frame.
                    ComponentType.ReadOnly<Building>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>() },
            });

            // No changed-version filter. It kept the list short when every temporary entity was
            // listed, but a filtered query has to wait for the jobs writing the colours before it
            // can answer: 0.2-0.26 ms a frame for as long as a building was under the cursor.
            // Limited to buildings the list is short anyway, and which copies have a fresh colour
            // is told by the tags the game puts on them (DarkenTempCopies).
        }

        [Preserve]
        protected override void OnUpdate()
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            m_RentersChanged.Clear();

            // Buildings the game recomputes this frame. No MeshColor in the query, so listing them
            // does not make the frame wait for the jobs that write colours.
            if (!m_RecomputedQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> recomputed = m_RecomputedQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < recomputed.Length; i++)
                {
                    m_RentersChanged.Add(recomputed[i]);
                }

                recomputed.Dispose();
            }

            if (!m_RentersUpdatedQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<RentersUpdated> events = m_RentersUpdatedQuery.ToComponentDataArray<RentersUpdated>(Allocator.Temp);
                for (int i = 0; i < events.Length; i++)
                {
                    m_RentersChanged.Add(events[i].m_Property);
                }

                events.Dispose();
            }

            m_RentersMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d / System.Diagnostics.Stopwatch.Frequency;
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

            long tempStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            darkened += DarkenTempCopies(renderers);
            m_TempMilliseconds += (System.Diagnostics.Stopwatch.GetTimestamp() - tempStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency;

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
                           + m_MaxMilliseconds.ToString("0.00") + " ms slowest ("
                           + (m_TempMilliseconds / m_Frames).ToString("0.000") + " ms of it on temporary copies, "
                           + (m_RentersMilliseconds / m_Frames).ToString("0.000") + " ms listing recomputed buildings), "
                           + m_Darkened + " building colour(s) darkened in " + m_FramesWithWork + " frame(s).");
                m_TempMilliseconds = 0d;
                m_RentersMilliseconds = 0d;
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
