using System.Collections.Generic;
using Game;
using Game.Common;
using Unity.Entities;
using UnityEngine.Scripting;

namespace SeenBetterDays.Rendering
{
    /// <summary>
    /// Deletes objects early enough in the frame for the game to unregister them: queued here,
    /// tagged Deleted by <see cref="DeferredDeletionSystem"/> in Modification1 of the next frame.
    ///
    /// Tagging them directly from this mod's systems, which run at ModificationEnd, came too late.
    /// The object search tree (Game.Objects.SearchSystem, Modification5) and the attachment
    /// bookkeeping (AttachSystem, Modification3) never saw the tag before CleanUpSystem destroyed
    /// the entity, so the search tree and the building's SubObject buffer kept pointing at an
    /// object that no longer existed. The next tool that searched the area - demolishing the
    /// building, the bulldozer, Anarchy - read it and crashed natively. Reproduced on 2026-10-11:
    /// leave design mode removing the decals placed in it, demolish the building.
    ///
    /// Not through ModificationBarrier1: it only hands out command buffers before Modification1,
    /// and asking for one at ModificationEnd throws.
    /// </summary>
    public static class DeferredDeletion
    {
        private static readonly List<Entity> s_Queue = new List<Entity>();
        private static readonly HashSet<Entity> s_Pending = new HashSet<Entity>();

        /// <summary>Queues an entity for deletion. False when it is already gone, already
        /// deleted or already queued.</summary>
        public static bool Delete(EntityManager entityManager, Entity entity)
        {
            if (!entityManager.Exists(entity) || entityManager.HasComponent<Deleted>(entity) || s_Pending.Contains(entity))
            {
                return false;
            }

            s_Queue.Add(entity);
            s_Pending.Add(entity);
            return true;
        }

        /// <summary>Whether an entity has been queued for deletion and is still alive: code that
        /// lists live objects must treat it as gone already.</summary>
        public static bool IsPending(Entity entity)
        {
            return s_Pending.Contains(entity);
        }

        /// <summary>Tags the queued entities. Called in Modification1 only.</summary>
        internal static int Flush(EntityManager entityManager)
        {
            // Last frame's entities have been destroyed by the cleanup since.
            s_Pending.RemoveWhere(e => !entityManager.Exists(e));

            int tagged = 0;
            for (int i = 0; i < s_Queue.Count; i++)
            {
                Entity entity = s_Queue[i];
                if (entityManager.Exists(entity) && !entityManager.HasComponent<Deleted>(entity))
                {
                    entityManager.AddComponent<Deleted>(entity);
                    tagged++;
                }
            }

            s_Queue.Clear();
            return tagged;
        }

        /// <summary>Forgets everything queued, for a city being unloaded.</summary>
        internal static void Clear()
        {
            s_Queue.Clear();
            s_Pending.Clear();
        }
    }

    /// <summary>Applies <see cref="DeferredDeletion"/> at the start of the modification phase.</summary>
    public partial class DeferredDeletionSystem : GameSystemBase
    {
        [Preserve]
        protected override void OnUpdate()
        {
            DeferredDeletion.Flush(EntityManager);
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Entities of the city before belong to nothing in this one.
            DeferredDeletion.Clear();
        }

        [Preserve]
        public DeferredDeletionSystem()
        {
        }
    }
}
