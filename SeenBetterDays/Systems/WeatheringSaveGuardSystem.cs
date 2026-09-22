using Game;
using Game.Serialization;
using Colossal.Serialization.Entities;
using System.Diagnostics;
using UnityEngine.Scripting;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Keeps this mod's active visual state out of the player's save file.
    ///
    /// The base weathering is written into <c>Game.Rendering.CustomMeshColor</c>, which is a
    /// vanilla enableable component and is serialised with the city. The detail layer consists of
    /// ordinary object entities, which the serializer would also keep. Left active, a city saved
    /// while weathered would therefore retain both kinds of runtime changes.
    /// In one test city that left 1069 growables out of 1801 stuck on the previous session's
    /// colours, which the mod then correctly refused to touch - correctly, and uselessly.
    ///
    /// So this system runs at <see cref="SystemUpdatePhase.Serialize"/>, disables colour overrides
    /// without structurally changing building entities, and removes temporary decals. The live
    /// MeshColor stays weathered while saving; the inactive override is harmless in the save and
    /// is re-enabled after the writer finishes. Runtime WeatheringState is not serializable.
    ///
    /// Two properties fall out of that which are worth having on purpose, not by luck:
    ///
    ///   - A save made with Seen Better Days installed opens normally for someone who does not
    ///     have it, because any remaining vanilla colour override is inactive and pristine.
    ///   - Uninstalling the mod leaves no damage behind - there is nothing to clean up.
    ///
    /// Whether the phase really runs before the entity data is written is a claim about the
    /// engine, and it is tested rather than assumed: weather a city, save, reload, and ask for a
    /// census. "0 already recoloured" means this works.
    /// </summary>
    public partial class WeatheringSaveGuardSystem : GameSystemBase, IPreSerialize
    {
        private BuildingWeatheringSystem m_Weathering;
        private BuildingOverlayTestSystem m_OverlayTest;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Weathering = World.GetOrCreateSystemManaged<BuildingWeatheringSystem>();
            m_OverlayTest = World.GetOrCreateSystemManaged<BuildingOverlayTestSystem>();
        }

        /// <summary>
        /// Runs through the game's pre-serialization contract, before the serializer creates its
        /// entity table. Performing these structural changes as an ordinary system inside the
        /// Serialize phase corrupted that table on the 13,824-building test city and produced a
        /// NullReferenceException in EntitySerializer.CreateEntityTable.
        /// </summary>
        public void PreSerialize(Context context)
        {
            // Serialize can continue after this system's own update returns. Do not let the next
            // ModificationEnd pass or a developer hotkey recreate structural changes underneath
            // the snapshot still being written.
            SaveMutationGate.BlockAfterSerializationStarts();

            Stopwatch stopwatch = Stopwatch.StartNew();

            // PreSerialize is outside the normal system update chain. EntityManager completes the
            // dependencies of each component we touch, but native rendering jobs may already have
            // gathered the same buffers and decal batches. Changing them while such a job is still
            // running can surface later as an access violation in Burst rather than as a managed
            // exception here.
            //
            // Complete every tracked ECS job before making the save-time structural changes. This
            // is deliberately one barrier per save, not one per building.
            EntityManager.CompleteAllTrackedJobs();
            double synchronizationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            int suspended = m_Weathering.SuspendForSave();
            int strippedDecals = m_OverlayTest.SuspendDecalsForSave();

            // The mutations above are synchronous, but the render invalidation path may have
            // registered fresh tracked work while the affected batches were marked dirty. Finish
            // that work before the serializer creates its entity table from the changed world.
            EntityManager.CompleteAllTrackedJobs();
            stopwatch.Stop();

            if (suspended > 0 || strippedDecals > 0)
            {
                Mod.Log.Info("Seen Better Days: temporarily disabled " + suspended
                           + " weathering colour override(s) and removed " + strippedDecals
                           + " temporary decal entit(ies) in "
                           + stopwatch.Elapsed.TotalMilliseconds.ToString("0.0")
                           + " ms before the serializer created its entity table ("
                           + synchronizationMilliseconds.ToString("0.0")
                           + " ms waiting for existing ECS jobs). "
                           + "Colour overrides are re-enabled after serialization without an ECS "
                           + "component rebuild; developer decal samples stay cleared.");
            }
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // Work is invoked by PreSerialize<WeatheringSaveGuardSystem>, not by a regular phase
            // update. Keeping this empty system alive gives the wrapper a stable target.
        }

        [Preserve]
        public WeatheringSaveGuardSystem()
        {
        }
    }
}
