namespace SeenBetterDays.Data
{
    /// <summary>
    /// The four growable categories Seen Better Days will eventually weather differently
    /// (see the development notes, "Future building-category variation"), plus the two
    /// verdicts that mean "not our business".
    /// </summary>
    public enum BuildingCategory
    {
        /// <summary>Not a building at all, or the entity no longer exists.</summary>
        NotABuilding,

        /// <summary>A building, but not a growable we may touch: service, signature, unique,
        /// infrastructure, or a growable whose zone we could not resolve.</summary>
        Excluded,

        Residential,
        Commercial,
        Industrial,
        Office,
    }

    public static class BuildingCategoryExtensions
    {
        public static bool IsEligible(this BuildingCategory category)
        {
            return category >= BuildingCategory.Residential;
        }
    }
}
