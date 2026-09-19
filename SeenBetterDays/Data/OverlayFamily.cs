using System;

namespace SeenBetterDays.Data
{
    /// <summary>
    /// The weathering families an overlay can belong to. A building's appearance is a
    /// combination of several of these at different intensities, never a single lookup.
    ///
    /// Flags rather than a plain enum because a decal in the catalogue may legitimately
    /// serve more than one family, and because a category filter ("offices want streaks and
    /// dirty glass, not rust") is then a single mask test.
    /// </summary>
    [Flags]
    public enum OverlayFamily
    {
        None = 0,
        Dirt = 1 << 0,
        Stain = 1 << 1,
        Crack = 1 << 2,
        Moss = 1 << 3,
        Rust = 1 << 4,
        Graffiti = 1 << 5,

        All = Dirt | Stain | Crack | Moss | Rust | Graffiti,
    }
}
