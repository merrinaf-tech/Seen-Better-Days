using System;

namespace SeenBetterDays.Designs
{
    /// <summary>
    /// One hand-made design as it is written to disk: the decals placed on one building for one
    /// state, in the building's own coordinates.
    ///
    /// Plain fields and float arrays, read and written with Newtonsoft.Json (shipped with the
    /// game), because the files are meant to be readable and editable by hand. Not Unity's
    /// JsonUtility: it wrote the file without the decal list and said nothing - it does not
    /// serialise arrays of classes from a mod's assembly. Decals are named, not referred to by
    /// entity: entities change from one session to the next, prefab names do not.
    /// </summary>
    [Serializable]
    public class DecalDesignFile
    {
        /// <summary>Raised when the layout of this file changes, so an old file is recognised
        /// rather than misread.</summary>
        public int format;

        /// <summary>The building's prefab name, e.g. "EE_ResidentialHigh01_L1_4x4". Each level
        /// of a building is its own prefab, so this also fixes the level.</summary>
        public string building;

        /// <summary>Aged, Worn, Neglected or Decayed - chosen by the designer, not read from
        /// the building used as the canvas.</summary>
        public string state;

        public string author;

        /// <summary>UTC, ISO 8601.</summary>
        public string created;

        public DesignedDecal[] decals;
    }

    [Serializable]
    public class DesignedDecal
    {
        /// <summary>The decal's prefab name.</summary>
        public string decal;

        /// <summary>The whitelisted pack it comes from, for the reader and for telling a missing
        /// pack from a renamed decal.</summary>
        public string pack;

        /// <summary>Metres, relative to the building's origin, in the building's frame.</summary>
        public float[] position;

        /// <summary>Quaternion x, y, z, w, relative to the building's rotation.</summary>
        public float[] rotation;

        /// <summary>Extra Detailing Tools' per-object scale; 1, 1, 1 when the decal was not
        /// scaled.</summary>
        public float[] scale;
    }
}
