using Game;
using Game.Serialization;
using Colossal.Serialization.Entities;
using System.Diagnostics;
using UnityEngine.Scripting;

namespace SeenBetterDays.Systems
{
    /// <summary>
    /// Keeps this mod out of the player's save file.
    ///
    /// The base weathering is written into <c>Game.Rendering.CustomMeshColor</c>, which is a
    /// vanilla component and is serialised with the city. The detail layer consists of ordinary
    /// object entities, which the serializer would also keep. Left alone, a city saved while
    /// weathered would therefore retain both kinds of runtime changes.
    /// In one test city that left 1069 growables out of 1801 stuck on the previous session's
    /// colours, which the mod then correctly refused to touch - correctly, and uselessly.
    ///
    /// So this system runs at <see cref="SystemUpdatePhase.Serialize"/> and takes everything off
    /// first. What goes to disk is the city as the game built it. Automatic colours are rebuilt
    /// from circumstances the game saves anyway; temporary developer decal samples stay cleared.
    ///
    /// Two properties fall out of that which are worth having on purpose, not by luck:
    ///
    ///   - A save made with Seen Better Days installed opens normally for someone who does not
    ///     have it, because it contains nothing of ours.
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
            // dependencies of each component we touch, but that is not enough for native rendering
            // jobs which have already gathered batches containing these entities. Removing more
            // than a thousand colour buffers and tagging decal entities while one of those jobs is
            // still running can leave it holding stale chunk data. The failure then appears later
            // as an access violation in Burst rather than as a managed exception here.
            //
            // Complete every tracked ECS job before making the save-time structural changes. This
            // is deliberately one barrier per save, not one per building.
            EntityManager.CompleteAllTrackedJobs();
            double synchronizationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            int stripped = m_Weathering.SuspendForSave();
            int strippedDecals = m_OverlayTest.SuspendDecalsForSave();

            // The mutations above are synchronous, but the render invalidation path may have
            // registered fresh tracked work while the affected batches were marked dirty. Finish
            // that work before the serializer creates its entity table from the changed world.
            EntityManager.CompleteAllTrackedJobs();
            stopwatch.Stop();

            if (stripped > 0 || strippedDecals > 0)
            {
                Mod.Log.Info("Seen Better Days: took the weathering off " + stripped
                           + " building(s) and removed " + strippedDecals
                           + " temporary decal entit(ies) in "
                           + stopwatch.Elapsed.TotalMilliseconds.ToString("0.0")
                           + " ms before the serializer created its entity table ("
                           + synchronizationMilliseconds.ToString("0.0")
                           + " ms waiting for existing ECS jobs). "
                           + "Automatic colour weathering goes back on after serialization; "
                           + "developer decal samples stay cleared.");
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
