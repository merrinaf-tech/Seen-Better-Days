using System.Globalization;
using UnityEngine;

namespace SeenBetterDays.Data
{
    /// <summary>
    /// Everything the renderer needs to know about how one building instance should look.
    ///
    /// This is the whole contract between the future simulation and any rendering backend:
    /// <c>renderer.Apply(building, profile)</c>. It is deliberately a bag of independent
    /// intensities plus a seed, not a state index, because two buildings in the same state
    /// must be able to look different, and because one building must be able to carry
    /// several families at once (dirty AND cracked AND tagged).
    ///
    /// It is a value type with no managed fields so it can eventually be stored in an ECS
    /// component and written to the save without any of this code changing shape.
    /// </summary>
    public struct BuildingVisualProfile
    {
        /// <summary>Nominal state. Descriptive only in this phase - the intensities below
        /// are what the renderer actually reads.</summary>
        public VisualState State;

        public float Dirt;
        public float Stain;
        public float Crack;
        public float Moss;
        public float Rust;
        public float Graffiti;

        /// <summary>Deterministic per-building seed. Same seed plus same intensities must
        /// always produce the same placement, so a save can store the seed instead of the
        /// resulting geometry.</summary>
        public uint Seed;

        public float GetIntensity(OverlayFamily family)
        {
            switch (family)
            {
                case OverlayFamily.Dirt: return Dirt;
                case OverlayFamily.Stain: return Stain;
                case OverlayFamily.Crack: return Crack;
                case OverlayFamily.Moss: return Moss;
                case OverlayFamily.Rust: return Rust;
                case OverlayFamily.Graffiti: return Graffiti;
                default: return 0f;
            }
        }

        public float MaxIntensity
        {
            get
            {
                float m = Dirt;
                if (Stain > m) m = Stain;
                if (Crack > m) m = Crack;
                if (Moss > m) m = Moss;
                if (Rust > m) m = Rust;
                if (Graffiti > m) m = Graffiti;
                return m;
            }
        }

        /// <summary>
        /// A stand-in for the ageing simulation that this phase deliberately does not build.
        /// It spreads the intensities over the families in a way that depends on the seed, so
        /// that two instances of the same prefab given the same nominal state still differ -
        /// which is exactly what test G needs to show.
        /// </summary>
        public static BuildingVisualProfile Debug(VisualState state, uint seed)
        {
            float baseIntensity = Mathf.Clamp01((int)state / 4f);
            var rng = new Unity.Mathematics.Random(seed == 0u ? 1u : seed);

            // Deterministic in the seed: 60%..100% of the nominal intensity.
            float Vary(float share)
            {
                return baseIntensity * share * (0.6f + 0.4f * rng.NextFloat());
            }

            // Not every family at once. Grime and staining are what a neglected building mostly
            // shows; moss, rust and graffiti are occasional and circumstantial, and a profile that
            // maxes all six produces something no real building looks like - the first version did
            // exactly that and turned a row of flats green, because full moss and full rust were
            // being asked for on the same wall.
            //
            // Which families a category actually favours (§12: rust and soot for industry, glass
            // streaks for offices, plaster damage for housing) is a question for the simulation.
            // This is a stand-in that is at least plausible.
            return new BuildingVisualProfile
            {
                State = state,
                Seed = seed,
                Dirt = Vary(1.0f),
                Stain = Vary(0.8f),
                Crack = Vary(0.5f),
                Moss = Vary(0.25f),
                Rust = Vary(0.2f),
                Graffiti = Vary(0.3f),
            };
        }

        /// <summary>
        /// Turns one weathering figure into the mix of families a building of this category would
        /// actually show.
        ///
        /// The categories differ the way the brief describes them: housing stains and cracks,
        /// shops collect street-level grime and tags, offices streak and dull rather than crack,
        /// industry rusts. The weights are what a category *tends* toward, not a rule - the seed
        /// still moves every building off the average so two neighbours never match.
        /// </summary>
        public static BuildingVisualProfile FromWeathering(BuildingCategory category, float weathering, uint seed)
        {
            float w = Mathf.Clamp01(weathering);
            var rng = new Unity.Mathematics.Random(seed == 0u ? 1u : seed);

            float Family(float weight)
            {
                return w * weight * (0.7f + 0.6f * rng.NextFloat());
            }

            float dirt, stain, crack, moss, rust, graffiti;

            switch (category)
            {
                case BuildingCategory.Commercial:
                    dirt = Family(1.0f); stain = Family(0.7f); crack = Family(0.3f);
                    moss = Family(0.15f); rust = Family(0.15f); graffiti = Family(0.6f);
                    break;
                case BuildingCategory.Office:
                    dirt = Family(0.9f); stain = Family(0.8f); crack = Family(0.15f);
                    moss = Family(0.1f); rust = Family(0.1f); graffiti = Family(0.2f);
                    break;
                case BuildingCategory.Industrial:
                    dirt = Family(1.0f); stain = Family(0.8f); crack = Family(0.5f);
                    moss = Family(0.1f); rust = Family(0.9f); graffiti = Family(0.2f);
                    break;
                default:
                    dirt = Family(1.0f); stain = Family(0.8f); crack = Family(0.5f);
                    moss = Family(0.3f); rust = Family(0.15f); graffiti = Family(0.3f);
                    break;
            }

            return new BuildingVisualProfile
            {
                State = StateFor(w),
                Seed = seed,
                Dirt = dirt,
                Stain = stain,
                Crack = crack,
                Moss = moss,
                Rust = rust,
                Graffiti = graffiti,
            };
        }

        /// <summary>The nominal state a weathering figure falls in. Descriptive - the intensities
        /// are what the renderer reads - but it is what a future tooltip would say.</summary>
        public static VisualState StateFor(float weathering)
        {
            if (weathering < 0.12f) return VisualState.Maintained;
            if (weathering < 0.35f) return VisualState.Aged;
            if (weathering < 0.60f) return VisualState.Worn;
            if (weathering < 0.85f) return VisualState.Neglected;
            return VisualState.Decayed;
        }

        public override string ToString()
        {
            var c = CultureInfo.InvariantCulture;
            return string.Format(
                c,
                "{0} seed={1} dirt={2:0.00} stain={3:0.00} crack={4:0.00} moss={5:0.00} rust={6:0.00} graffiti={7:0.00}",
                State, Seed, Dirt, Stain, Crack, Moss, Rust, Graffiti);
        }
    }
}
