using Unity.Mathematics;

namespace SeenBetterDays.Simulation
{
    /// <summary>
    /// How weathered a building *ought* to look, given what is happening to it right now.
    ///
    /// Everything here is a pure function of present circumstance. There is no term for age, no
    /// accumulator, no clock — which is what makes the rules in the brief hold by construction
    /// rather than by special case:
    ///
    ///   - **Age is not degradation.** Nothing here knows how old a building is.
    ///   - **Decay is never inevitable.** A building whose tenants can pay its upkeep returns a
    ///     target of zero for ever, however long it stands.
    ///   - **Recovery is free.** When circumstances improve the target drops, and the building
    ///     walks back up to clean on its own.
    ///   - **Level 5 in good condition stays Maintained.** Not a rule in the code — a consequence.
    ///     A healthy building's <c>BuildingCondition</c> is positive, and only the negative side
    ///     produces distress.
    ///   - **Nothing depends on levelling up.** A building that can never level (Plop the
    ///     Growables, or a Historical flag) simply sits at whatever condition its finances earn it,
    ///     and is weathered accordingly.
    ///
    /// The mod stays a reader of the simulation. It invents no parallel model of decay; it
    /// interprets numbers Cities: Skylines II already maintains.
    /// </summary>
    public static class WeatheringTarget
    {
        /// <summary>
        /// How far a building drifts from pristine purely because it is a cheap building.
        ///
        /// The brief allows levels 1-4 to show ordinary wear while insisting a Level 5 in good
        /// condition never does. A small floor by level gives a city texture — a low-rent block
        /// reads as a low-rent block even when its books balance — without any of it accumulating.
        /// </summary>
        private static readonly float[] s_LevelFloor = { 0.22f, 0.22f, 0.16f, 0.10f, 0.05f, 0f };

        /// <summary>
        /// How much of the poor-street term applies at each level. A cheap street wears a cheap
        /// building; it should not make the city's best-off tenants look abandoned. Until
        /// 2026-09-28 the term ignored level, so a level 5 on one of the cheapest streets of its
        /// zone type reached Worn on its own, and Neglected with a service missing - reported by
        /// a player as "rich people with high level looking abandoned". It also broke the rule
        /// that a level 5 in good condition stays Maintained. Zero at level 5 restores it.
        /// Level 1 is eased a little too: at full weight the street alone pushed many ordinary
        /// low-rent blocks to Worn, which read as more neglect than the city actually had.
        /// </summary>
        private static readonly float[] s_StreetByLevel = { 0.8f, 0.8f, 0.6f, 0.4f, 0.2f, 0f };

        /// <summary>
        /// Computes the target, 0..1.
        /// </summary>
        /// <param name="condition">The building's <c>BuildingCondition.m_Condition</c>.</param>
        /// <param name="abandonCost">
        /// <c>BuildingUtils.GetAbandonCost(...)</c> for this building: the depth of debt at which
        /// the game gives up on it. Dividing by it turns an absolute currency figure into
        /// "how close to being abandoned", comparable across levels and categories.
        /// </param>
        /// <param name="level">Spawnable building level, 1..5.</param>
        /// <param name="efficiency">
        /// <c>BuildingUtils.GetEfficiency(...)</c>, 0..1. Rubbish uncollected, water off, power
        /// out - the visible neglect that is nobody's fault but the city's.
        /// </param>
        /// <param name="abandoned">Whether the building carries <c>Game.Buildings.Abandoned</c>.</param>
        /// <param name="poverty">
        /// Where this building's land value falls within the city's own range: 0 on the most
        /// valuable street in town, 1 on the least. Measured against the city rather than against
        /// a constant, so a wealthy city still has visibly poorer corners and a struggling one is
        /// not uniformly derelict - the comparison a player actually makes is with the rest of
        /// their own city.
        /// </param>
        /// <param name="wealth">
        /// How far above an ordinary street this building's land value stands: 0 on a median
        /// street or below, 1 at the top of the city. This is the term that does the visible work,
        /// because it is the half of the distribution that has any spread in it.
        /// </param>
        /// <param name="seed">
        /// The building's own fixed seed. Two buildings of the same level on the same street are
        /// not kept equally well, and without this they came out identical - the city read as one
        /// building repeated, which is the thing the mod exists to break up.
        /// </param>
        public static float Compute(int condition, int abandonCost, int level, float efficiency, bool abandoned, float poverty, float wealth, uint seed)
        {
            return Explain(condition, abandonCost, level, efficiency, abandoned, poverty, wealth, seed).Target;
        }

        /// <summary>
        /// The target together with the parts it was made from, so the tooltip can say which one
        /// decided it. There is only this one formula: Compute returns its Target, so an
        /// explanation can never disagree with the value it explains.
        /// </summary>
        public struct Parts
        {
            public float Target;
            public bool Abandoned;
            /// <summary>How close the building is to being abandoned for debt, 0..1.</summary>
            public float Distress;
            /// <summary>What an ordinary street does to a building of this level, after wealth
            /// and this building's own upkeep luck.</summary>
            public float Floor;
            /// <summary>This building's upkeep luck against others like it: below 1 better kept,
            /// above 1 worse.</summary>
            public float Individuality;
            /// <summary>How far down its own zone type's land values the street sits, after the
            /// building's level has been taken into account.</summary>
            public float PoorStreet;

            /// <summary>The poor-street term as it was before it depended on level. Kept only so
            /// the load census can compare the old rule with the new one.</summary>
            public float PoorStreetIgnoringLevel;
            /// <summary>What missing services add.</summary>
            public float Neglect;
        }

        public static Parts Explain(int condition, int abandonCost, int level, float efficiency, bool abandoned, float poverty, float wealth, uint seed)
        {
            // Abandonment is the end of the scale, but only as a destination. The approach rate
            // is what stops a building going derelict the instant its last tenant leaves - the
            // brief is explicit that abandonment must not force Decayed immediately.
            if (abandoned)
            {
                return new Parts { Target = 1f, Abandoned = true };
            }

            // Only the negative half of condition means anything for weathering. Positive means
            // the tenants are covering upkeep and putting something aside; how much they are
            // putting aside is the game's business, not ours.
            float distress = 0f;
            if (condition < 0 && abandonCost > 0)
            {
                distress = math.saturate(-condition / (float)abandonCost);
            }

            // Wealth maintains a building rather than poverty dirtying it.
            //
            // That is the shape of the measurement, not a preference. In a real city the land
            // values below the median are almost flat - 69 at the tenth percentile against 78 at
            // the median - while above it they run to 592. Reading "how poor is this street" asks
            // the half of the distribution that has nothing to say; reading "how well off" asks
            // the half that does.
            //
            // So the per-level floor is not a constant any more: it is what an *ordinary* street
            // does to a building of this level, and money takes it away. A cheap block on a
            // premium street is kept up; the same block on an ordinary one is not.
            //
            // The brief's rules survive this intact, and by construction rather than by exception.
            // A Level 5 has a floor of zero, so it stays Maintained on any street, for ever. And
            // nothing here accumulates: a building whose street improves gets cleaner.
            // How well this particular building is looked after, against others exactly like it.
            // Deterministic: the same building always lands on the same figure, so nothing
            // flickers and a save reproduces the city exactly.
            //
            // Applied to the baseline only, never to distress or abandonment. A derelict building
            // should read as derelict, not as lucky - individuality is about how well an ordinary
            // building is kept, not about whether a crisis is happening to it.
            var rng = new Unity.Mathematics.Random(seed == 0u ? 1u : seed);
            float individuality = 0.45f + 1.1f * rng.NextFloat();

            float floor = s_LevelFloor[math.clamp(level, 0, s_LevelFloor.Length - 1)]
                        * (1f - math.saturate(wealth))
                        * individuality;

            // Kept, but small in practice: it can only reach as far as the spread below the median
            // allows, which in a measured city was not far at all.
            float poorStreetIgnoringLevel = math.saturate(poverty) * 0.45f;
            float poorStreet = poorStreetIgnoringLevel * s_StreetByLevel[math.clamp(level, 0, s_StreetByLevel.Length - 1)];

            // Services failing is weathering the player can see the cause of and fix. Weighted
            // below distress: a building can be perfectly kept and still have a bin strike.
            float neglect = math.saturate(1f - efficiency) * 0.35f;

            // Distress dominates, and the level floor only lifts the bottom rather than adding on
            // top - a struggling cheap building should not read as worse than a derelict one.
            return new Parts
            {
                Target = math.saturate(math.max(math.max(floor, poorStreet), distress) + neglect * (1f - distress)),
                Distress = distress,
                Floor = floor,
                Individuality = individuality,
                PoorStreet = poorStreet,
                PoorStreetIgnoringLevel = poorStreetIgnoringLevel,
                Neglect = neglect,
            };
        }

        /// <summary>What the target would have been under the rule before poor streets depended
        /// on level. For the load census's before-and-after comparison only.</summary>
        public static float TargetIgnoringLevelOnStreet(in Parts parts)
        {
            if (parts.Abandoned)
            {
                return 1f;
            }

            return math.saturate(math.max(math.max(parts.Floor, parts.PoorStreetIgnoringLevel), parts.Distress)
                                 + parts.Neglect * (1f - parts.Distress));
        }

        /// <summary>
        /// How far the displayed weathering may move toward the target in one update.
        ///
        /// Decay is slow and recovery is slower still: repainting a building is a bigger event
        /// than letting it slide, and a city that visibly heals the moment a budget slider moves
        /// would cheapen the whole effect. Abandonment is the exception - it degrades markedly
        /// faster, which is the brief's wording, while still taking real time to arrive.
        /// </summary>
        public static float Step(float current, float target, bool abandoned)
        {
            if (target > current)
            {
                return abandoned ? 0.08f : 0.02f;
            }

            return 0.012f;
        }
    }
}
