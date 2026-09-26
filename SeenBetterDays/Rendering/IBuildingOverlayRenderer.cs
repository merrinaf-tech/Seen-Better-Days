using System.Collections.Generic;
using Unity.Entities;
using SeenBetterDays.Data;

namespace SeenBetterDays.Rendering
{
    /// <summary>
    /// The one seam between this mod's (future) simulation and whatever actually draws the
    /// weathering. Callers say "this building should look like this" and never learn whether
    /// the answer was a native decal object, a projected mesh, a shader or something else.
    ///
    /// Two backends are foreseen and are why this interface exists at all:
    ///   - <see cref="DecalObjectOverlayRenderer"/> - native Cities: Skylines II decal objects
    ///     (implemented; see the development notes for why it was chosen first);
    ///   - a runtime <c>Graphics.DrawMesh</c> renderer, if we ever need geometry the decal
    ///     system cannot express. Verified feasible but not built in this phase.
    /// </summary>
    public interface IBuildingOverlayRenderer
    {
        /// <summary>Short name used in logs and in the debug read-out.</summary>
        string Name { get; }

        /// <summary>False when the backend found nothing it can draw with. Check
        /// <see cref="UnavailableReason"/> before reporting a failure as a bug.</summary>
        bool IsAvailable { get; }

        /// <summary>Why <see cref="IsAvailable"/> is false; null when it is true.</summary>
        string UnavailableReason { get; }

        /// <summary>Buildings currently carrying at least one overlay.</summary>
        int TrackedBuildingCount { get; }

        /// <summary>Individual visual elements currently alive across all buildings.</summary>
        int OverlayCount { get; }

        /// <summary>
        /// Makes <paramref name="building"/> look the way <paramref name="profile"/> describes,
        /// replacing anything previously applied to it. Must not touch the shared prefab, and
        /// must not change how any other instance of that prefab looks.
        /// </summary>
        /// <param name="placed">How many individual visual elements were created.</param>
        /// <returns>False if nothing could be applied; <paramref name="failureReason"/> says why.</returns>
        bool Apply(Entity building, in BuildingVisualProfile profile, out int placed, out string failureReason);

        /// <summary>Removes every overlay from one building. Returns false if it had none.</summary>
        bool Remove(Entity building);

        /// <summary>Removes every overlay this backend owns. Returns the number of buildings cleared.</summary>
        int RemoveAll();

        bool Has(Entity building);

        /// <summary>
        /// Drops overlays whose building no longer exists or is being demolished. The proof of
        /// concept calls this every frame over a handful of entries; the real mod must be
        /// driven by the building's own deletion event instead.
        /// </summary>
        int PruneOrphans();

        void CollectTrackedBuildings(List<Entity> into);
    }
}
