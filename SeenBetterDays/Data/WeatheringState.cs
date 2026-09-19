using Colossal.Serialization.Entities;
using Unity.Entities;

namespace SeenBetterDays.Data
{
    /// <summary>
    /// How weathered one building currently looks, and the seed that makes it look like itself.
    ///
    /// This is the whole of what Seen Better Days needs to remember about a building. The colours
    /// are not stored - they are recomputed from this and the building's own pristine palette - so
    /// a save carries two numbers per weathered building and nothing else.
    ///
    /// <see cref="m_Weathering"/> chases a target computed from the building's *current*
    /// circumstances. It is deliberately not an accumulator over time: age is not degradation, and
    /// a building whose fortunes improve has to be able to get clean again. Time only decides how
    /// fast it moves, never where it is going.
    /// </summary>
    public struct WeatheringState : IComponentData, IQueryTypeParameter, ISerializable
    {
        /// <summary>0 = as built, 1 = as bad as this mod will make it.</summary>
        public float m_Weathering;

        /// <summary>Fixed per building. Two buildings in identical circumstances differ by this
        /// and only this, which is what stops a street of one prefab reading as a repeat.</summary>
        public uint m_Seed;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Weathering);
            writer.Write(m_Seed);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Weathering);
            reader.Read(out m_Seed);
        }
    }
}
