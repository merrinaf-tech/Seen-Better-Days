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

        /// <summary>The continuous 0..1 weathering value. Decal families enter at state
        /// boundaries, while colour follows this directly so crossing a boundary cannot cause
        /// an abrupt shade change.</summary>
        public float Weathering;

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
                Weathering = baseIntensity,
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
        /// Each visual state introduces new readable kinds of damage: Aged has cracks, Worn adds
        /// dirt, moss, rust (industry has rust from Aged) and small tags, Neglected adds full street art, posters and heavy
        /// plaster damage (decal minimum states, see DecalPrefabInfo.MinState), and Decayed adds
        /// stains. Category weights still
        /// vary the amount, while the seed moves individual buildings off the average.
        /// </summary>
        public static BuildingVisualProfile FromWeathering(BuildingCategory category, float weathering, uint seed)
        {
            float w = Mathf.Clamp01(weathering);
            var rng = new Unity.Mathematics.Random(seed == 0u ? 1u : seed);

            float Family(float weight)
            {
                return w * weight * (0.7f + 0.6f * rng.NextFloat());
            }

            float dirt, stain, crack, graffiti, moss, rust;

            // Moss and rust are weathering by time and damp rather than by neglect alone, so they
            // come in with dirt at Worn. Rust belongs to industry, moss to housing and shops.
            switch (category)
            {
                case BuildingCategory.Commercial:
                    dirt = Family(1.0f); stain = Family(0.7f); crack = Family(0.8f); graffiti = Family(0.6f);
                    moss = Family(0.35f); rust = Family(0.2f);
                    break;
                case BuildingCategory.Office:
                    dirt = Family(0.9f); stain = Family(0.8f); crack = Family(0.8f); graffiti = Family(0.35f);
                    moss = Family(0.25f); rust = Family(0.15f);
                    break;
                case BuildingCategory.Industrial:
                    dirt = Family(1.0f); stain = Family(0.8f); crack = Family(0.8f); graffiti = Family(0.35f);
                    moss = Family(0.15f); rust = Family(0.6f);
                    break;
                default:
                    dirt = Family(1.0f); stain = Family(0.8f); crack = Family(0.8f); graffiti = Family(0.35f);
                    moss = Family(0.5f); rust = Family(0.15f);
                    break;
            }

            VisualState state = StateFor(w);

            // These gates are the visual vocabulary of the five states. A zero is important:
            // the decal planner treats it as a hard prohibition, so a family cannot reappear by
            // rounding, minimum-mark rescue or a large facade budget.
            if (state < VisualState.Worn) dirt = 0f;
            if (state < VisualState.Decayed) stain = 0f;
            if (state < VisualState.Aged) crack = 0f;
            if (state < VisualState.Worn) graffiti = 0f;
            if (state < VisualState.Worn) moss = 0f;
            // Industry rusts early: from Aged, growing with the weathering like every family. Other
            // buildings wait for Worn.
            if (state < (category == BuildingCategory.Industrial ? VisualState.Aged : VisualState.Worn)) rust = 0f;

            return new BuildingVisualProfile
            {
                State = state,
                Weathering = w,
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
                "{0} weathering={1:0.00} seed={2} dirt={3:0.00} stain={4:0.00} crack={5:0.00} moss={6:0.00} rust={7:0.00} graffiti={8:0.00}",
                State, Weathering, Seed, Dirt, Stain, Crack, Moss, Rust, Graffiti);
        }
    }
}
