namespace SeenBetterDays.Geometry
{
    /// <summary>
    /// A side of a building's local bounding box. Names are relative to the prefab's own
    /// axes: in Cities: Skylines II an object's local +Z is its nominal front, which for a
    /// growable is the side the lot presents to the road. The proof of concept does not
    /// rely on that being true - it can cycle through all four - but it defaults to Front.
    /// </summary>
    public enum FacadeSide
    {
        Front = 0,  // local +Z
        Right = 1,  // local +X
        Back = 2,   // local -Z
        Left = 3,   // local -X
    }
}
