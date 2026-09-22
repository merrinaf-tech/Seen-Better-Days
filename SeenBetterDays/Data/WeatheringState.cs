using Unity.Entities;

namespace SeenBetterDays.Data
{
    /// <summary>
    /// How weathered one building currently looks, and the seed that makes it look like itself.
    ///
    /// This is the whole of what Seen Better Days needs to remember about a building while the
    /// city is running. It is deliberately not serializable: the value is a pure function of
    /// circumstances the game already saves, and is recomputed after loading. Keeping it runtime
    /// only also means a city does not acquire a dependency on this mod's component schema.
    ///
    /// <see cref="m_Weathering"/> chases a target computed from the building's *current*
    /// circumstances. It is deliberately not an accumulator over time: age is not degradation, and
    /// a building whose fortunes improve has to be able to get clean again. Time only decides how
    /// fast it moves, never where it is going.
    /// </summary>
    public struct WeatheringState : IComponentData, IQueryTypeParameter
    {
        /// <summary>0 = as built, 1 = as bad as this mod will make it.</summary>
        public float m_Weathering;

        /// <summary>Fixed per building. Two buildings in identical circumstances differ by this
        /// and only this, which is what stops a street of one prefab reading as a repeat.</summary>
        public uint m_Seed;

    }
}
