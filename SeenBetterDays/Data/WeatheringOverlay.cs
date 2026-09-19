using Unity.Entities;

namespace SeenBetterDays.Data
{
    /// <summary>
    /// Marks an entity as one of this mod's overlay objects and records which building it
    /// belongs to.
    ///
    /// Deliberately NOT <c>Game.Common.Owner</c>. Owner would make the game treat the decal as
    /// a sub-object of the building, and <c>Game.Objects.SubObjectSystem</c> deletes any
    /// sub-object whose prefab is not listed in the owner prefab's own sub-object set - which
    /// ours never will be. Keeping the link in our own component means the game's sub-object
    /// bookkeeping never sees these entities, and their lifetime is entirely ours.
    ///
    /// It is also not <see cref="Colossal.Serialization.Entities.ISerializable"/>: overlays are
    /// runtime-only and are meant to be rebuilt from the stored profile, not saved as geometry.
    /// The tag still lets us sweep up entities whose owning record was lost - after a mod
    /// reload, say - without keeping a second index.
    /// </summary>
    public struct WeatheringOverlay : IComponentData, IQueryTypeParameter
    {
        /// <summary>The building this overlay is drawn on.</summary>
        public Entity m_Building;

        /// <summary>Which weathering family this element represents, as an
        /// <see cref="OverlayFamily"/> bit pattern.</summary>
        public int m_Family;

        public WeatheringOverlay(Entity building, OverlayFamily family)
        {
            m_Building = building;
            m_Family = (int)family;
        }
    }
}
