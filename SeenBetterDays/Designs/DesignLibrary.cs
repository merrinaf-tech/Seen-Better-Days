using Colossal.Logging;
using Game.Prefabs;
using SeenBetterDays.Data;
using SeenBetterDays.Rendering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Entities;
using Unity.Mathematics;

namespace SeenBetterDays.Designs
{
    /// <summary>
    /// The hand-made decal designs: loaded from disk, checked against the decals installed, and
    /// picked for a building by its model and state.
    ///
    /// By model, not by prefab: levels that share a model share its walls, so a design made on
    /// level 1 is used on level 2 too when both are built from the same meshes. See
    /// <see cref="BuildingCensus"/>.
    ///
    /// Two folders are read. The mod's own Designs folder holds the designs shipped with it; the
    /// player's designs folder under ModsData holds what they exported, so a design is used in
    /// their own city the moment it is exported, before anyone has sent it anywhere.
    ///
    /// A design is used only when every one of its decals is installed. One with a decal from a
    /// pack the player does not have is set aside quietly and the building gets the random
    /// placement. One whose pack is installed but no longer has the decal was broken by a pack
    /// update, and is reported so it can be taken out of the mod.
    /// </summary>
    public sealed class DesignLibrary
    {
        public static readonly DesignLibrary Instance = new DesignLibrary();

        public const int FormatVersion = 1;

        /// <summary>Most decals a design may hold. Random placement stops at 24; a design is a
        /// deliberate composition and gets more room, but every decal is an entity drawn for as
        /// long as the camera is near.</summary>
        public const int MaxDecals = 40;

        public const string FileName = "design.json";

        public static readonly VisualState[] DesignableStates =
        {
            VisualState.Aged, VisualState.Worn, VisualState.Neglected, VisualState.Decayed,
        };

        /// <summary>A design ready to place: prefab entities and building-relative transforms.</summary>
        public sealed class Resolved
        {
            public string Name;

            /// <summary>The designer's name as they gave it, or empty. Shown in the tooltip.</summary>
            public string Author;

            public VisualState State;

            /// <summary>Shipped with the mod, as against exported by this player.</summary>
            public bool Shipped;
            public Entity[] Prefabs;
            public float3[] Positions;
            public quaternion[] Rotations;
            public OverlayFamily[] Families;
        }

        private sealed class Entry
        {
            public string Path;
            public bool Local;
            public DecalDesignFile File;
            public VisualState State;
            public string Model;
            public Resolved Resolved;
            public string Problem;
            public bool Obsolete;
        }

        private readonly List<Entry> m_Entries = new List<Entry>();
        private readonly Dictionary<string, List<Resolved>> m_Usable = new Dictionary<string, List<Resolved>>();
        private DecalPrefabCatalog m_Catalog;
        private BuildingCensus m_Census;

        public string ShippedFolder { get; private set; }
        public string LocalFolder { get; private set; }

        public int UsableCount { get; private set; }

        public void SetFolders(string shipped, string local)
        {
            ShippedFolder = shipped;
            LocalFolder = local;
        }

        /// <summary>Reads every design from both folders and resolves them against the
        /// catalogue. Called once the catalogue has been built.</summary>
        public void Reload(DecalPrefabCatalog catalog, BuildingCensus census, ILog log)
        {
            m_Catalog = catalog;
            m_Census = census;
            m_Entries.Clear();

            ReadFolder(ShippedFolder, false, log);
            ReadFolder(LocalFolder, true, log);

            for (int i = 0; i < m_Entries.Count; i++)
            {
                Resolve(m_Entries[i]);
            }

            Reindex();
            log.Info("Seen Better Days: " + Describe());
            ReportProblems(m_Entries, log);
        }

        /// <summary>Adds one design just exported, without rereading the rest.</summary>
        public void AddLocal(string path, ILog log)
        {
            if (m_Catalog == null)
            {
                return;
            }

            m_Entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            Entry entry = Read(path, true, log);
            if (entry == null)
            {
                return;
            }

            Resolve(entry);
            m_Entries.Add(entry);
            Reindex();
            ReportProblems(new List<Entry> { entry }, log);
        }

        /// <summary>A design for this building's model and state, chosen by the building's seed so
        /// the same building always gets the same one. False when there is none usable.</summary>
        public bool TryPick(Entity buildingPrefab, VisualState state, uint seed, out Resolved design)
        {
            design = null;
            string model;
            List<Resolved> list;
            if (UsableCount == 0 || m_Census == null
                || !m_Census.TryGetModel(buildingPrefab, out model)
                || !m_Usable.TryGetValue(Key(model, state), out list) || list.Count == 0)
            {
                return false;
            }

            design = list[(int)(seed % (uint)list.Count)];
            return true;
        }

        /// <summary>Every usable design for this building's model, in state order - what design
        /// mode offers to start from.</summary>
        public List<Resolved> ListFor(Entity buildingPrefab)
        {
            var list = new List<Resolved>();
            string model;
            if (m_Census == null || !m_Census.TryGetModel(buildingPrefab, out model))
            {
                return list;
            }

            for (int i = 0; i < DesignableStates.Length; i++)
            {
                List<Resolved> designs;
                if (m_Usable.TryGetValue(Key(model, DesignableStates[i]), out designs))
                {
                    list.AddRange(designs);
                }
            }

            return list;
        }

        public string Describe()
        {
            int shipped = 0, local = 0, missingPack = 0, obsolete = 0, other = 0;
            for (int i = 0; i < m_Entries.Count; i++)
            {
                Entry e = m_Entries[i];
                if (e.Local) local++; else shipped++;
                if (e.Resolved != null) continue;
                if (e.Obsolete) obsolete++;
                else if (IsQuiet(e.Problem)) missingPack++;
                else other++;
            }

            return "hand-made designs - " + m_Entries.Count + " found (" + shipped + " shipped, " + local
                 + " yours), " + UsableCount + " usable, " + missingPack + " waiting for a pack or building you do not have, "
                 + obsolete + " obsolete, " + other + " not usable for another reason.";
        }

        // ------------------------------------------------------------------------------------

        private void ReadFolder(string folder, bool local, ILog log)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folder, FileName, SearchOption.AllDirectories);
            }
            catch (Exception e)
            {
                log.Warn("Seen Better Days: could not list designs in " + folder + ": " + e.Message);
                return;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                Entry entry = Read(files[i], local, log);
                if (entry != null)
                {
                    m_Entries.Add(entry);
                }
            }
        }

        private static Entry Read(string path, bool local, ILog log)
        {
            var entry = new Entry { Path = path, Local = local };
            try
            {
                entry.File = Newtonsoft.Json.JsonConvert.DeserializeObject<DecalDesignFile>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                entry.Problem = "unreadable: " + e.Message;
                return entry;
            }

            DecalDesignFile file = entry.File;
            VisualState state;
            if (file == null)
            {
                entry.Problem = "empty file";
            }
            else if (file.format != FormatVersion)
            {
                entry.Problem = "format " + file.format + ", this version reads " + FormatVersion;
            }
            else if (string.IsNullOrEmpty(file.building))
            {
                entry.Problem = "no building named";
            }
            else if (!TryParseState(file.state, out state))
            {
                entry.Problem = "state '" + file.state + "' is not Aged, Worn, Neglected or Decayed";
            }
            else if (file.decals == null || file.decals.Length == 0)
            {
                entry.Problem = "no decals";
            }
            else if (file.decals.Length > MaxDecals)
            {
                entry.Problem = file.decals.Length + " decals, more than the " + MaxDecals + " allowed";
            }
            else
            {
                entry.State = state;
            }

            return entry;
        }

        private void Resolve(Entry entry)
        {
            entry.Resolved = null;
            entry.Obsolete = false;
            if (entry.Problem != null)
            {
                return;
            }

            // The building must be installed: its model is what the design is filed under.
            if (!m_Census.TryGetModel(entry.File.building, out entry.Model))
            {
                entry.Problem = "building not installed: " + entry.File.building;
                return;
            }

            DesignedDecal[] decals = entry.File.decals;
            var resolved = new Resolved
            {
                Name = DisplayName(entry),
                Author = entry.File.author != null ? entry.File.author.Trim() : string.Empty,
                State = entry.State,
                Shipped = !entry.Local,
                Prefabs = new Entity[decals.Length],
                Positions = new float3[decals.Length],
                Rotations = new quaternion[decals.Length],
                Families = new OverlayFamily[decals.Length],
            };

            for (int i = 0; i < decals.Length; i++)
            {
                DesignedDecal d = decals[i];
                if (d == null || d.position == null || d.position.Length != 3
                    || d.rotation == null || d.rotation.Length != 4)
                {
                    entry.Problem = "decal " + (i + 1) + " has no valid position or rotation";
                    return;
                }

                int pack = DecalPrefabCatalog.PackOf(d.decal);
                if (pack < 0)
                {
                    entry.Problem = "'" + d.decal + "' is not on the whitelist";
                    return;
                }

                if (d.scale != null && d.scale.Length == 3
                    && (math.abs(d.scale[0] - 1f) > 0.001f || math.abs(d.scale[1] - 1f) > 0.001f
                        || math.abs(d.scale[2] - 1f) > 0.001f))
                {
                    // Extra Detailing Tools draws the scale through its own systems. Until it is
                    // known that a decal created here is drawn scaled, a design that relies on it
                    // would come out wrong, so it waits.
                    entry.Problem = "uses a scaled decal ('" + d.decal + "'), which is not supported yet";
                    return;
                }

                DecalPrefabInfo info;
                if (!m_Catalog.TryGetByName(d.decal, out info))
                {
                    if (m_Catalog.IsPackPresent(pack))
                    {
                        entry.Obsolete = true;
                        entry.Problem = "'" + d.decal + "' is no longer in " + DecalPrefabCatalog.PackName(pack)
                                      + ", which is installed - probably renamed by a pack update";
                    }
                    else
                    {
                        entry.Problem = "pack not installed: " + DecalPrefabCatalog.PackName(pack);
                    }

                    return;
                }

                resolved.Prefabs[i] = info.PrefabEntity;
                resolved.Positions[i] = new float3(d.position[0], d.position[1], d.position[2]);
                resolved.Rotations[i] = math.normalizesafe(
                    new quaternion(d.rotation[0], d.rotation[1], d.rotation[2], d.rotation[3]), quaternion.identity);
                resolved.Families[i] = LowestFamily(info.AutomaticFamilies != OverlayFamily.None
                    ? info.AutomaticFamilies : info.Families);
            }

            entry.Resolved = resolved;
        }

        private void Reindex()
        {
            m_Usable.Clear();
            UsableCount = 0;

            // A design exported by this player and later shipped with the mod exists twice: once
            // in their folder, once in the mod's. Same building, state, author and time: keep the
            // shipped one, so it is not picked twice as often as the others.
            var shipped = new HashSet<string>();
            for (int i = 0; i < m_Entries.Count; i++)
            {
                if (!m_Entries[i].Local && m_Entries[i].Resolved != null)
                {
                    shipped.Add(Identity(m_Entries[i].File));
                }
            }

            for (int i = 0; i < m_Entries.Count; i++)
            {
                Entry e = m_Entries[i];
                if (e.Resolved == null || (e.Local && shipped.Contains(Identity(e.File))))
                {
                    continue;
                }

                string key = Key(e.Model, e.State);
                List<Resolved> list;
                if (!m_Usable.TryGetValue(key, out list))
                {
                    list = new List<Resolved>();
                    m_Usable[key] = list;
                }

                list.Add(e.Resolved);
                UsableCount++;
            }
        }

        private void ReportProblems(List<Entry> entries, ILog log)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e.Problem == null)
                {
                    continue;
                }

                // A missing pack or building is the player's choice, not a fault: one summary count
                // is enough.
                if (!e.Obsolete && IsQuiet(e.Problem))
                {
                    continue;
                }

                string line = "Seen Better Days: design " + DisplayName(e) + " (" + e.Path + ") "
                            + (e.Obsolete ? "is OBSOLETE: " : "is not usable: ") + e.Problem + ".";
                if (e.Obsolete) log.Warn(line); else log.Info(line);
            }
        }

        private static string DisplayName(Entry e)
        {
            string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(e.Path));
            return (e.File != null && e.File.building != null ? e.File.building : "?") + "/" + folder;
        }

        private static bool IsQuiet(string problem)
        {
            return problem != null
                && (problem.StartsWith("pack not installed", StringComparison.Ordinal)
                    || problem.StartsWith("building not installed", StringComparison.Ordinal));
        }

        private static string Identity(DecalDesignFile file)
        {
            return file.building + "|" + file.state + "|" + file.author + "|" + file.created;
        }

        private static string Key(string model, VisualState state)
        {
            return model + "#" + (int)state;
        }

        public static bool TryParseState(string text, out VisualState state)
        {
            for (int i = 0; i < DesignableStates.Length; i++)
            {
                if (string.Equals(text, DesignableStates[i].ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    state = DesignableStates[i];
                    return true;
                }
            }

            state = VisualState.Maintained;
            return false;
        }

        private static OverlayFamily LowestFamily(OverlayFamily families)
        {
            int bits = (int)families;
            return bits == 0 ? OverlayFamily.None : (OverlayFamily)(bits & -bits);
        }
    }
}
