namespace SeenBetterDays.Data
{
    /// <summary>
    /// The five visual states Seen Better Days will eventually drive.
    ///
    /// Nothing in this proof of concept computes a state: no ageing, no wear formula, no
    /// condition or occupancy input. The enum exists only so the rendering side is already
    /// written against the vocabulary the simulation will later speak, and so that the
    /// renderer never gets built around "one state = one texture" - see BuildingVisualProfile.
    /// </summary>
    public enum VisualState
    {
        Maintained = 0,
        Aged = 1,
        Worn = 2,
        Neglected = 3,
        Decayed = 4,
    }
}
