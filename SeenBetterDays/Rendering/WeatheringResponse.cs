namespace SeenBetterDays.Rendering
{
    /// <summary>
    /// Which parts of a building's colour weathering is allowed to take.
    ///
    /// Three separate things happen to a colour when it weathers, and they are not the same
    /// thing at all:
    ///
    ///   - **Light.** Multiplying all three channels by one factor lowers the value and leaves
    ///     hue and saturation exactly where the asset author put them. A red brick block stays
    ///     as red as it was, and as saturated; it is simply dimmer. This is the only one of the
    ///     three that cannot change what colour a building *is*.
    ///   - **Colour strength.** Pulling each channel towards the luminance drains saturation.
    ///     Physically right - grime is grey and it sits on top of the paint - but at any real
    ///     strength every building converges on the same grey, and a street loses the variety
    ///     that made it worth weathering.
    ///   - **Hue.** Biasing the channels against each other says *what kind* of dirt this is:
    ///     green for damp, ochre for rust. Expressive, and the easiest to overdo - this is what
    ///     turned four residential blocks green.
    ///
    /// Kept as an explicit choice rather than a blend because the right answer depends on the
    /// city, and on taste.
    /// </summary>
    public enum WeatheringResponse
    {
        /// <summary>Light only. Hue and saturation untouched - the building's own colour,
        /// darker.</summary>
        DarknessOnly,

        /// <summary>Light and colour strength. Weathered buildings go dim and a little washed
        /// out, but no hue is invented.</summary>
        Fading,

        /// <summary>All three, including the per-family hue bias.</summary>
        Full,
    }
}
