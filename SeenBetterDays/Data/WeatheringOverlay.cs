using Colossal.Serialization.Entities;
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
    /// Overlays are runtime-only and are meant to be removed before a save. The marker itself is
    /// serializable as a recovery signature: if a crash or interrupted serialization preserves
    /// an overlay object anyway, the next load can still distinguish it from a user decal and
    /// delete it. The building reference is intentionally not restored because every surviving
    /// overlay is swept before play resumes.
    /// </summary>
    public struct WeatheringOverlay : IComponentData, IQueryTypeParameter, ISerializable
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

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Family);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Family);
            m_Building = Entity.Null;
        }
    }
}
