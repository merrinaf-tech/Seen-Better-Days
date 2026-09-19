using Game.Rendering;
using Unity.Entities;

namespace SeenBetterDays.Data
{
    /// <summary>
    /// A building's colours as they were before Seen Better Days touched them, one entry per
    /// submesh.
    ///
    /// Without this the weathering compounds: the obvious source for "the colour to darken" is the
    /// instance's own <see cref="MeshColor"/>, but that is precisely what our previous pass
    /// overwrote, so applying state 2 after state 1 darkens an already darkened colour. The first
    /// in-game run showed it plainly - three steps at x0.82, x0.64 and x0.46 landed on x0.24.
    ///
    /// Two things depend on having a pristine baseline, and both are in the brief:
    ///   - weathering must be a pure function of the visual state, so that re-applying the same
    ///     state is a no-op and two buildings in the same state look the same;
    ///   - a building whose circumstances improve must be able to recover, which means there has
    ///     to be something to recover *to*.
    ///
    /// Recolor keeps an equivalent record for its own undo, which is where the idea comes from.
    /// Captured once, when we first weather a building, and dropped when we reset it - so if the
    /// player has recoloured a building themselves, what we capture is their colour, and their
    /// choice is what the building returns to.
    /// </summary>
    public struct PristineMeshColor : IBufferElementData
    {
        public ColorSet m_ColorSet;

        public PristineMeshColor(ColorSet colorSet)
        {
            m_ColorSet = colorSet;
        }
    }
}
