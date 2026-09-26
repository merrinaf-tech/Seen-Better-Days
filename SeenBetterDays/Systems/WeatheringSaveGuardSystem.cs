using Game;
using Game.Serialization;
using Colossal.Serialization.Entities;
using SeenBetterDays.Data;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Unity.Entities;
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
    /// without structurally changing building entities, and excludes our decal entities from the
    /// serializer's query. Marking every decal Deleted here used to churn native render batches
    /// during a save; repeated crashes followed when the camera moved immediately afterwards.
    /// Filtering the snapshot leaves the live decals and their batches alone. The live MeshColor
    /// stays weathered while saving; the inactive override is harmless in the save and is
    /// re-enabled after the writer finishes. Runtime WeatheringState is not serializable.
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
        private SerializerSystem m_Serializer;

        // The game exposes no registration hook for excluding a mod-owned entity from its save
        // query. Keep the reflection confined to this one field and method, and fail visibly if a
        // game update changes either signature. The marker itself remains serializable so an
        // older save containing stray decals can still be cleaned on load.
        private static readonly FieldInfo SerializerQueryField = typeof(SerializerSystem).GetField(
            "m_Query", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo CreateSerializerQueryMethod =
            typeof(SerializerSystem).GetMethod(
                "CreateQuery", BindingFlags.Instance | BindingFlags.NonPublic);

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Weathering = World.GetOrCreateSystemManaged<BuildingWeatheringSystem>();
            m_OverlayTest = World.GetOrCreateSystemManaged<BuildingOverlayTestSystem>();
            m_Serializer = World.GetOrCreateSystemManaged<SerializerSystem>();
        }

        /// <summary>
        /// Runs through the game's pre-serialization contract, before the serializer creates its
        /// entity table. Performing structural changes as an ordinary system inside the Serialize
        /// phase corrupted that table on the 13,824-building test city and produced a
        /// NullReferenceException in EntitySerializer.CreateEntityTable.
        /// </summary>
        public void PreSerialize(Context context)
        {
            // Serialize can continue after this system's own update returns. Do not let the next
            // ModificationEnd pass or a developer hotkey recreate structural changes underneath
            // the snapshot still being written.
            SaveMutationGate.BlockAfterSerializationStarts();

            Stopwatch stopwatch = Stopwatch.StartNew();

            // PreSerialize is outside the normal system update chain. Complete tracked work before
            // touching the colour buffers or replacing the serializer's entity query.
            EntityManager.CompleteAllTrackedJobs();
            double synchronizationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            int suspended = m_Weathering.SuspendForSave();
            bool decalsExcluded = ExcludeDecalsFromSave();
            int liveDecals = m_OverlayTest.ActiveDecalEntityCount;

            // Colour invalidation may register fresh work while the affected batches are marked
            // dirty. Finish it before the serializer creates its entity table.
            EntityManager.CompleteAllTrackedJobs();
            stopwatch.Stop();

            if (!decalsExcluded && liveDecals > 0)
            {
                Mod.Log.Error("Seen Better Days: could not exclude " + liveDecals
                              + " live decal entit(ies) from this save. They will be swept when "
                              + "the city next loads with this mod; no render batches were "
                              + "destroyed during serialization.");
            }

            if (suspended > 0 || liveDecals > 0)
            {
                Mod.Log.Info("Seen Better Days: temporarily disabled " + suspended
                           + " weathering colour override(s) and "
                           + (decalsExcluded ? "excluded " : "could not exclude ") + liveDecals
                           + " live decal entit(ies) from the save query in "
                           + stopwatch.Elapsed.TotalMilliseconds.ToString("0.0")
                           + " ms before the serializer created its entity table ("
                           + synchronizationMilliseconds.ToString("0.0")
                           + " ms waiting for existing ECS jobs). "
                           + "Colour overrides are re-enabled after serialization without an ECS "
                           + "component rebuild; live decals are not removed for saving.");
            }
        }

        private bool ExcludeDecalsFromSave()
        {
            try
            {
                if (m_Serializer == null || SerializerQueryField == null
                    || CreateSerializerQueryMethod == null)
                {
                    return false;
                }

                // SerializerSystem.OnUpdate normally refreshes its query when a component
                // serializer is registered. Do that refresh here, before adding our exclusion,
                // so its later update cannot replace the filtered query in the same save.
                ComponentSerializerLibrary library = m_Serializer.componentLibrary;
                if (library == null)
                {
                    return false;
                }

                if (library.isDirty)
                {
                    List<ComponentType> serializableTypes;
                    library.Initialize(m_Serializer, out serializableTypes);
                    CreateSerializerQueryMethod.Invoke(m_Serializer,
                        new object[] { serializableTypes });
                }

                EntityQuery original = (EntityQuery)SerializerQueryField.GetValue(m_Serializer);
                EntityQueryDesc[] originalDescs = original.GetEntityQueryDescs();
                if (originalDescs == null || originalDescs.Length == 0)
                {
                    return false;
                }

                ComponentType marker = ComponentType.ReadOnly<WeatheringOverlay>();
                EntityQueryDesc[] filteredDescs = new EntityQueryDesc[originalDescs.Length];
                bool alreadyExcluded = true;

                for (int i = 0; i < originalDescs.Length; i++)
                {
                    EntityQueryDesc source = originalDescs[i];
                    ComponentType[] none = source.None ?? Array.Empty<ComponentType>();
                    bool containsMarker = false;
                    for (int j = 0; j < none.Length; j++)
                    {
                        if (none[j].TypeIndex == marker.TypeIndex)
                        {
                            containsMarker = true;
                            break;
                        }
                    }

                    if (containsMarker)
                    {
                        filteredDescs[i] = source;
                        continue;
                    }

                    alreadyExcluded = false;
                    ComponentType[] filteredNone = new ComponentType[none.Length + 1];
                    Array.Copy(none, filteredNone, none.Length);
                    filteredNone[none.Length] = marker;
                    ComponentType[] filteredAny = WithoutType(source.Any, marker);
                    if (source.Any != null && source.Any.Length > 0
                        && filteredAny.Length == 0)
                    {
                        // An empty Any would broaden the serializer query to every entity.
                        return false;
                    }

                    filteredDescs[i] = new EntityQueryDesc
                    {
                        All = WithoutType(source.All, marker),
                        Any = filteredAny,
                        None = filteredNone,
                        Options = source.Options,
                    };
                }

                EntityQuery saveQuery = original;
                if (!alreadyExcluded)
                {
                    saveQuery = GetEntityQuery(filteredDescs);
                    SerializerQueryField.SetValue(m_Serializer, saveQuery);
                }

                return m_OverlayTest.CountDecalsInQuery(saveQuery) == 0;
            }
            catch (Exception exception)
            {
                Mod.Log.Error("Seen Better Days: failed to filter decal entities from the save "
                              + "query: " + exception);
                return false;
            }
        }

        private static ComponentType[] WithoutType(ComponentType[] types, ComponentType excluded)
        {
            if (types == null || types.Length == 0)
            {
                return types;
            }

            int kept = 0;
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i].TypeIndex != excluded.TypeIndex)
                {
                    kept++;
                }
            }

            if (kept == types.Length)
            {
                return types;
            }

            ComponentType[] result = new ComponentType[kept];
            int next = 0;
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i].TypeIndex != excluded.TypeIndex)
                {
                    result[next++] = types[i];
                }
            }

            return result;
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
