using System;
using System.Collections.Generic;
using System.Text;
using Colossal.IO.AssetDatabase;
using Colossal.Mathematics;
using Game.Prefabs;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using SeenBetterDays.Data;

namespace SeenBetterDays.Rendering
{
    /// <summary>One decal object prefab we are allowed to instantiate.</summary>
    public sealed class DecalPrefabInfo
    {
        /// <summary>The prefab entity, i.e. what a spawned instance's PrefabRef points at.</summary>
        public Entity PrefabEntity;

        public string Name;

        /// <summary>Which surfaces the decal's material is allowed to draw on.</summary>
        public DecalLayers LayerMask;

        /// <summary>Local bounds of the projector box, from the prefab's ObjectGeometryData.</summary>
        public Bounds3 Bounds;

        /// <summary>Projector box size: x across the texture, y the projection depth, z along it.</summary>
        public float3 Size;

        /// <summary>Mesh layers the prefab declares. PreCullingSystem turns these into the
        /// bounds mask that decides whether the object is ever considered for drawing; a zero
        /// here would mean it can only appear in a debug view.</summary>
        public MeshLayer Layers;

        /// <summary>Geometry flags. Overridable is the one that matters: it is what lets the
        /// game hide the decal for intersecting the building we put it on.</summary>
        public Game.Objects.GeometryFlags Flags;

        /// <summary>LOD threshold from the prefab - higher means it stops drawing sooner.</summary>
        public int MinLod;

        /// <summary>The render prefab, kept so the decal's material can be inspected lazily -
        /// loading two thousand surface assets up front would not be free.</summary>
        public RenderPrefab Mesh;

        /// <summary>Set once the catalogue has looked at the material.</summary>
        public bool Inspected;

        /// <summary>The decal paints base colour, i.e. it darkens what is underneath. This is
        /// the property that decides whether it reads in any light or only in some.</summary>
        public bool HasBaseColorMap;

        /// <summary>How much of the decal's effect is a smoothness change. A wet-effect decal is
        /// nearly all smoothness and almost no base colour: it needs a specular highlight to show
        /// at all, so it vanishes at night and under an overcast sky.</summary>
        public float Smoothness;

        public float NormalOpacity;

        /// <summary>Families this decal has been explicitly recognised as - never a guess that
        /// something unrecognised is "probably dirt". See <see cref="DecalPrefabCatalog"/>.</summary>
        public OverlayFamily Families;

        /// <summary>
        /// Families this decal may supply to the live automatic layer. Unlike
        /// <see cref="Families"/>, this is granted only by the explicit source whitelist.
        /// </summary>
        public OverlayFamily AutomaticFamilies;

        /// <summary>
        /// Whether this decal may be put on a wall.
        ///
        /// The layer mask alone is not enough, and trusting it put a road arrow on the side of an
        /// office block. Road markings, playground surfaces and the game's own ground decals all
        /// declare `DecalLayers.Buildings` - they are painted on lots, and lots belong to
        /// buildings - so the mask says yes to things that were never meant to stand up.
        ///
        /// `GeometryFlags.ExclusiveGround` is the honest test, because it is the asset author
        /// saying outright that this thing lies on the ground. Compare the two cases seen in the
        /// catalogue dump: a road decal carries `ExclusiveGround, OccupyZone, HasLot`, while a
        /// graffiti mural carries `Overridable, WalkThrough, Brushable` and nothing about ground.
        ///
        /// A flag the asset declares about itself beats a keyword list about its name, which would
        /// have to be extended for every language and every asset pack.
        /// </summary>
        public bool AffectsBuildings
        {
            get
            {
                return (LayerMask & DecalLayers.Buildings) != 0
                    && (Flags & Game.Objects.GeometryFlags.ExclusiveGround) == 0;
            }
        }

        /// <summary>Area the decal covers on the surface it lands on. The projector's thin
        /// axis is Y - that is the projection depth, not part of the footprint.</summary>
        public float Footprint
        {
            get { return Size.x * Size.z; }
        }

        /// <summary>Shortest footprint edge, i.e. how small this thing can look.</summary>
        public float SmallestEdge
        {
            get { return Size.x < Size.z ? Size.x : Size.z; }
        }

        public override string ToString()
        {
            return string.Format(
                "{0} [decalLayers={1} families={2} autoFamilies={3} size=({4:0.0},{5:0.0},{6:0.0}) meshLayers={7} geometryFlags={8} minLod={9}{10}]",
                Name, LayerMask, Families, AutomaticFamilies, Size.x, Size.y, Size.z, Layers, Flags, MinLod,
                Inspected
                    ? string.Format(" baseColor={0} smoothness={1:0.00} normalOpacity={2:0.00}",
                        HasBaseColorMap, Smoothness, NormalOpacity)
                    : string.Empty);
        }
    }

    /// <summary>
    /// Finds the decal object prefabs that are loaded right now - vanilla ones, ones from
    /// official packs, and ones any installed decal-pack mod has registered - and records what
    /// each of them can be used for.
    ///
    /// Two rules matter here and both come straight from the brief:
    ///
    ///   - We never copy or redistribute anyone's texture. We reference prefabs the game has
    ///     already loaded, by entity, and nothing else.
    ///   - We never treat "this decal exists" as "this decal is appropriate". Automatic use
    ///     requires both an approved source pack and a family keyword in the individual prefab;
    ///     everything else is catalogued, logged and reachable only through a deliberate debug
    ///     hotkey.
    /// </summary>
    public sealed class DecalPrefabCatalog
    {
        private static readonly KeyValuePair<string, OverlayFamily>[] s_Keywords =
        {
            new KeyValuePair<string, OverlayFamily>("graffiti", OverlayFamily.Graffiti),
            new KeyValuePair<string, OverlayFamily>("tag", OverlayFamily.Graffiti),
            new KeyValuePair<string, OverlayFamily>("scribble", OverlayFamily.Graffiti),
            new KeyValuePair<string, OverlayFamily>("streetart", OverlayFamily.Graffiti),
            new KeyValuePair<string, OverlayFamily>("street art", OverlayFamily.Graffiti),
            new KeyValuePair<string, OverlayFamily>("rust", OverlayFamily.Rust),
            new KeyValuePair<string, OverlayFamily>("corros", OverlayFamily.Rust),
            new KeyValuePair<string, OverlayFamily>("moss", OverlayFamily.Moss),
            new KeyValuePair<string, OverlayFamily>("ivy", OverlayFamily.Moss),
            new KeyValuePair<string, OverlayFamily>("crack", OverlayFamily.Crack),
            new KeyValuePair<string, OverlayFamily>("damage", OverlayFamily.Crack),
            new KeyValuePair<string, OverlayFamily>("patch", OverlayFamily.Crack),
            new KeyValuePair<string, OverlayFamily>("stain", OverlayFamily.Stain),
            new KeyValuePair<string, OverlayFamily>("leak", OverlayFamily.Stain),
            new KeyValuePair<string, OverlayFamily>("streak", OverlayFamily.Stain),
            new KeyValuePair<string, OverlayFamily>("dirt", OverlayFamily.Dirt),
            new KeyValuePair<string, OverlayFamily>("dirty", OverlayFamily.Dirt),
            new KeyValuePair<string, OverlayFamily>("grime", OverlayFamily.Dirt),
            new KeyValuePair<string, OverlayFamily>("soot", OverlayFamily.Dirt),
            new KeyValuePair<string, OverlayFamily>("trash", OverlayFamily.Dirt),
            new KeyValuePair<string, OverlayFamily>("leaf", OverlayFamily.Dirt),
            new KeyValuePair<string, OverlayFamily>("leaves", OverlayFamily.Dirt),
        };

        /// <summary>
        /// Asset packs explicitly approved for automatic placement, paired with the only family
        /// each pack may supply. Prefab names created by Extra Assets Importer begin with their
        /// source pack name, which gives us a stable boundary between discovery and endorsement.
        ///
        /// Keep this list deliberately small. A keyword such as "stain" can occur in a puddle
        /// pack and "graffiti" can occur in a forty-metre mural pack; neither fact makes every
        /// asset in that mod suitable for a random building facade.
        /// </summary>
        private static readonly KeyValuePair<string, OverlayFamily>[] s_ApprovedAutomaticSources =
        {
            new KeyValuePair<string, OverlayFamily>("Stains and Leakage Decal Pack", OverlayFamily.Stain),
            new KeyValuePair<string, OverlayFamily>("Scribbles & Tags Decal Pack", OverlayFamily.Graffiti),
            new KeyValuePair<string, OverlayFamily>("Cracks and Damage Decal Pack", OverlayFamily.Crack),
            new KeyValuePair<string, OverlayFamily>("G87 Stains and Puddles Decals Wet Pack", OverlayFamily.Stain),
            new KeyValuePair<string, OverlayFamily>("G87 Road Repair Patch Pack", OverlayFamily.Crack),
            new KeyValuePair<string, OverlayFamily>("Fallen leaves decals", OverlayFamily.Dirt),
            new KeyValuePair<string, OverlayFamily>("G87 Trash Decals Dirty Pack", OverlayFamily.Dirt),
            new KeyValuePair<string, OverlayFamily>("Street Art Decal Pack", OverlayFamily.Graffiti),
        };

        /// <summary>
        /// Below this, in metres on its shortest edge, a decal is not weathering - it is a lane
        /// marking, an alphabet letter or a manhole cover. The first in-game run picked at
        /// random out of 1,696 building-capable decals and scattered half-metre letters across a
        /// 24-metre facade, which is indistinguishable from the mod doing nothing at all.
        /// </summary>
        private const float MinAutoEdge = 1.5f;

        private readonly List<DecalPrefabInfo> m_All = new List<DecalPrefabInfo>();
        private readonly List<DecalPrefabInfo> m_BuildingCapable = new List<DecalPrefabInfo>();
        private readonly List<DecalPrefabInfo> m_AutoPool = new List<DecalPrefabInfo>();
        private readonly Dictionary<Entity, DecalPrefabInfo> m_ByPrefab = new Dictionary<Entity, DecalPrefabInfo>();

        /// <summary>Every decal object prefab found, sorted by name.</summary>
        public List<DecalPrefabInfo> All
        {
            get { return m_All; }
        }

        /// <summary>The subset whose material is allowed to draw on building surfaces.
        /// This is the only list the renderer picks from by default.</summary>
        public List<DecalPrefabInfo> BuildingCapable
        {
            get { return m_BuildingCapable; }
        }

        /// <summary>Whitelisted building-capable decals big enough to place automatically.</summary>
        public List<DecalPrefabInfo> AutoPool
        {
            get { return m_AutoPool; }
        }

        /// <summary>The biggest building-capable decal there is - the one to reach for when the
        /// question is "does anything render at all", where subtlety is the enemy.</summary>
        public DecalPrefabInfo Largest { get; private set; }

        public int TotalObjectPrefabsScanned { get; private set; }

        /// <summary>
        /// The biggest building-capable decal that still makes sense against a wall. Plain
        /// "biggest" picked an 87x89 metre sports-field decal on the first run - technically the
        /// answer to the question asked, and useless for looking at a house.
        /// </summary>
        public DecalPrefabInfo PickTestDecal(float maxEdge)
        {
            DecalPrefabInfo best = null;

            for (int i = 0; i < m_BuildingCapable.Count; i++)
            {
                DecalPrefabInfo candidate = m_BuildingCapable[i];
                if (candidate.Size.x > maxEdge || candidate.Size.z > maxEdge)
                {
                    continue;
                }

                if (best == null || candidate.Footprint > best.Footprint)
                {
                    best = candidate;
                }
            }

            return best ?? Largest;
        }

        /// <summary>
        /// A spread of decals for the contact sheet: one per recognised family first, so the
        /// row shows a stain next to a crack next to some graffiti rather than five near-identical
        /// puddles, then padded from the automatic pool if the install has few recognised ones.
        /// </summary>
        public List<DecalPrefabInfo> PickTestSheet(int count, float maxEdge, bool requireBaseColor)
        {
            List<DecalPrefabInfo> chosen = new List<DecalPrefabInfo>(count);

            OverlayFamily[] families =
            {
                OverlayFamily.Stain,
                OverlayFamily.Dirt,
                OverlayFamily.Graffiti,
                OverlayFamily.Crack,
                OverlayFamily.Rust,
                OverlayFamily.Moss,
            };

            for (int f = 0; f < families.Length && chosen.Count < count; f++)
            {
                DecalPrefabInfo best = null;

                for (int i = 0; i < m_BuildingCapable.Count; i++)
                {
                    DecalPrefabInfo candidate = m_BuildingCapable[i];
                    if ((candidate.Families & families[f]) == 0
                        || candidate.SmallestEdge < 2f
                        || candidate.Size.x > maxEdge
                        || candidate.Size.z > maxEdge
                        || chosen.Contains(candidate))
                    {
                        continue;
                    }

                    Inspect(candidate);

                    // A decal that does not paint base colour cannot read in every light, and
                    // weathering that only shows at certain hours is not worth shipping.
                    if (requireBaseColor && !candidate.HasBaseColorMap)
                    {
                        continue;
                    }

                    if (best == null || Score(candidate) > Score(best))
                    {
                        best = candidate;
                    }
                }

                if (best != null)
                {
                    chosen.Add(best);
                }
            }

            for (int i = 0; i < m_AutoPool.Count && chosen.Count < count; i++)
            {
                DecalPrefabInfo candidate = m_AutoPool[i];
                if (candidate.Size.x > maxEdge || candidate.Size.z > maxEdge || chosen.Contains(candidate))
                {
                    continue;
                }

                Inspect(candidate);
                if (!requireBaseColor || candidate.HasBaseColorMap)
                {
                    chosen.Add(candidate);
                }
            }

            return chosen;
        }

        /// <summary>
        /// Bigger is better, and a mostly-smoothness decal is heavily penalised: it is the kind
        /// that renders correctly and still cannot be seen.
        /// </summary>
        private static float Score(DecalPrefabInfo info)
        {
            return info.Footprint * (1f - 0.75f * math.saturate(info.Smoothness));
        }

        /// <summary>
        /// Reads a decal's material to find out whether it actually darkens anything.
        ///
        /// A Cities: Skylines II decal writes into HDRP's decal buffer and can write base colour,
        /// normal and mask (smoothness / metallic) independently. Only the base colour channel
        /// changes the albedo that lighting is then applied to, so only a decal with a base colour
        /// map reads the same at noon and at midnight. A decal that is mostly a smoothness change
        /// needs a specular highlight to be visible at all - which is why one of them rendered
        /// perfectly and showed nothing on a dark wall at night.
        ///
        /// Lazy and defensive: surfaces load on demand, and a failure leaves the decal
        /// un-preferred rather than taking the catalogue down.
        /// </summary>
        public void Inspect(DecalPrefabInfo info)
        {
            if (info == null || info.Inspected || info.Mesh == null)
            {
                return;
            }

            info.Inspected = true;

            try
            {
                foreach (SurfaceAsset surface in info.Mesh.surfaceAssets)
                {
                    if (surface == null)
                    {
                        continue;
                    }

                    if (!surface.isDataLoaded)
                    {
                        surface.LoadProperties(useVT: false);
                    }

                    TextureAsset baseColor;
                    if (surface.TryGetTexture("_BaseColorMap", out baseColor) && baseColor != null)
                    {
                        info.HasBaseColorMap = true;
                    }

                    float value;
                    if (surface.TryGetFloatProperty("_Smoothness", out value))
                    {
                        info.Smoothness = math.max(info.Smoothness, value);
                    }

                    if (surface.TryGetFloatProperty("_NormalOpacity", out value))
                    {
                        info.NormalOpacity = math.max(info.NormalOpacity, value);
                    }
                }
            }
            catch
            {
                info.HasBaseColorMap = false;
            }
        }

        /// <summary>
        /// Decals proven in this install to render on a vertical wall, by name fragment.
        ///
        /// Not a guess: these are the ones measured on a facade with a horizontal projector axis
        /// while the automatic pick - "G87 Stain 01 A S", the biggest albedo-painting decal -
        /// only ever appears lying flat, even when a person places it. Whatever separates a decal
        /// that paints a wall from one that does not, it travels with the asset, and the only
        /// honest way to know is to have seen it work.
        ///
        /// This is exactly the curated list the brief asks for under "External decal packs":
        /// discovery is not endorsement, and a decal earns its place by being verified.
        /// </summary>
        private static readonly string[] s_KnownWallDecals =
        {
            // Weathering only. A door decal renders on a wall perfectly well and was briefly used
            // as a probe, but rows of brand-new doors are not what this mod is for, and a
            // diagnostic shortcut has no business sitting in a list the real mod might read.
            "Algernon",
            "Clover",
        };

        /// <summary>The first catalogued decal whose name contains <paramref name="fragment"/>.</summary>
        public DecalPrefabInfo FindByName(string fragment)
        {
            for (int i = 0; i < m_All.Count; i++)
            {
                if (m_All[i].Name != null
                    && m_All[i].Name.IndexOf(fragment, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return m_All[i];
                }
            }

            return null;
        }

        /// <summary>A decal known to work on a wall, or null if this install has none of them.</summary>
        public DecalPrefabInfo FindKnownWallDecal()
        {
            for (int i = 0; i < s_KnownWallDecals.Length; i++)
            {
                DecalPrefabInfo found = FindByName(s_KnownWallDecals[i]);
                if (found != null)
                {
                    Inspect(found);
                    return found;
                }
            }

            return null;
        }

        public bool TryGetByPrefab(Entity prefabEntity, out DecalPrefabInfo info)
        {
            return m_ByPrefab.TryGetValue(prefabEntity, out info);
        }

        public void Rebuild(EntityManager entityManager, PrefabSystem prefabSystem, EntityQuery objectPrefabQuery)
        {
            m_All.Clear();
            m_BuildingCapable.Clear();
            m_AutoPool.Clear();
            m_ByPrefab.Clear();
            Largest = null;
            TotalObjectPrefabsScanned = 0;

            NativeArray<Entity> prefabEntities = objectPrefabQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                TotalObjectPrefabsScanned = prefabEntities.Length;

                for (int i = 0; i < prefabEntities.Length; i++)
                {
                    DecalPrefabInfo info = TryDescribe(entityManager, prefabSystem, prefabEntities[i]);
                    if (info == null)
                    {
                        continue;
                    }

                    m_All.Add(info);
                    m_ByPrefab[info.PrefabEntity] = info;

                    if (!info.AffectsBuildings)
                    {
                        continue;
                    }

                    m_BuildingCapable.Add(info);

                    info.AutomaticFamilies = ClassifyForAutomaticUse(info.Name, info.Families);
                    if (info.SmallestEdge >= MinAutoEdge
                        && info.AutomaticFamilies != OverlayFamily.None)
                    {
                        m_AutoPool.Add(info);
                    }

                    if (Largest == null || info.Footprint > Largest.Footprint)
                    {
                        Largest = info;
                    }
                }
            }
            finally
            {
                prefabEntities.Dispose();
            }

            // Stable order so the same install always picks the same decal for the same seed.
            m_All.Sort(CompareByName);
            m_BuildingCapable.Sort(CompareByName);
            m_AutoPool.Sort(CompareByName);
        }

        private static int CompareByName(DecalPrefabInfo a, DecalPrefabInfo b)
        {
            return string.CompareOrdinal(a.Name, b.Name);
        }

        private static int CompareByFootprintDescending(DecalPrefabInfo a, DecalPrefabInfo b)
        {
            return b.Footprint.CompareTo(a.Footprint);
        }

        private static DecalPrefabInfo TryDescribe(EntityManager entityManager, PrefabSystem prefabSystem, Entity prefabEntity)
        {
            if (!entityManager.HasComponent<ObjectData>(prefabEntity)
                || !entityManager.HasComponent<ObjectGeometryData>(prefabEntity)
                || !entityManager.HasBuffer<SubMesh>(prefabEntity))
            {
                return null;
            }

            // Placeholder prefabs are editor stand-ins, not things to place. Every decal imported
            // by Extra Assets Importer has a "<name>_Placeholder" twin carrying PlaceholderObject,
            // whose archetype includes Game.Objects.Placeholder - so an instance of one is
            // something PlaceholderSystem wants to replace, not something that draws. They were
            // being catalogued as ordinary decals and handed out for placement.
            if (entityManager.HasComponent<PlaceholderObjectData>(prefabEntity))
            {
                return null;
            }

            DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(prefabEntity, true);

            // A decal object is one whose single mesh is flagged as a decal. Multi-mesh objects
            // that happen to include a decal are skipped: we would not know how to place the
            // rest of them, and this phase only needs one clean case.
            if (subMeshes.Length != 1)
            {
                return null;
            }

            Entity meshEntity = subMeshes[0].m_SubMesh;
            if (!entityManager.HasComponent<MeshData>(meshEntity))
            {
                return null;
            }

            MeshData meshData = entityManager.GetComponentData<MeshData>(meshEntity);
            if ((meshData.m_State & MeshFlags.Decal) == 0)
            {
                return null;
            }

            // The decal's own layer mask lives on the managed RenderPrefab, not on the entity:
            // ManagedBatchSystem reads DecalProperties.m_LayerMask straight off it when it
            // builds the material. MeshData.m_DecalLayer means the opposite thing - which layer
            // a *receiving* surface belongs to - and must not be used here.
            RenderPrefab renderPrefab;
            if (!prefabSystem.TryGetPrefab(meshEntity, out renderPrefab) || renderPrefab == null)
            {
                return null;
            }

            DecalProperties decalProperties;
            if (!renderPrefab.TryGet(out decalProperties) || decalProperties == null)
            {
                return null;
            }

            PrefabBase prefabBase;
            if (!prefabSystem.TryGetPrefab(prefabEntity, out prefabBase) || prefabBase == null)
            {
                return null;
            }

            ObjectGeometryData geometry = entityManager.GetComponentData<ObjectGeometryData>(prefabEntity);
            float3 size = geometry.m_Bounds.max - geometry.m_Bounds.min;
            if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
            {
                return null;
            }

            return new DecalPrefabInfo
            {
                PrefabEntity = prefabEntity,
                Name = prefabBase.name,
                LayerMask = decalProperties.m_LayerMask,
                Bounds = geometry.m_Bounds,
                Size = size,
                Families = ClassifyByName(prefabBase.name),
                Layers = geometry.m_Layers,
                Flags = geometry.m_Flags,
                MinLod = geometry.m_MinLod,
                Mesh = renderPrefab,
            };
        }

        /// <summary>
        /// Recognises a decal only when its name says plainly what it is. Anything else gets
        /// <see cref="OverlayFamily.None"/> and is never picked as that family - a decal pack
        /// full of shop signs must not become "weathering" just because it is installed.
        /// </summary>
        private static OverlayFamily ClassifyByName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return OverlayFamily.None;
            }

            string lower = name.ToLowerInvariant();
            OverlayFamily families = OverlayFamily.None;

            for (int i = 0; i < s_Keywords.Length; i++)
            {
                if (lower.IndexOf(s_Keywords[i].Key, StringComparison.Ordinal) >= 0)
                {
                    families |= s_Keywords[i].Value;
                }
            }

            return families;
        }

        /// <summary>
        /// Applies the source whitelist and narrows a prefab to the family approved for that
        /// source. Requiring both an approved source and a matching asset name prevents a future
        /// mixed pack from contributing unrelated objects merely because its package is trusted.
        /// </summary>
        private static OverlayFamily ClassifyForAutomaticUse(string name, OverlayFamily namedFamilies)
        {
            if (string.IsNullOrEmpty(name) || namedFamilies == OverlayFamily.None)
            {
                return OverlayFamily.None;
            }

            for (int i = 0; i < s_ApprovedAutomaticSources.Length; i++)
            {
                KeyValuePair<string, OverlayFamily> source = s_ApprovedAutomaticSources[i];
                if (name.StartsWith(source.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return namedFamilies & source.Value;
                }
            }

            return OverlayFamily.None;
        }

        public bool HasAutomaticFamily(OverlayFamily family)
        {
            for (int i = 0; i < m_AutoPool.Count; i++)
            {
                if ((m_AutoPool[i].AutomaticFamilies & family) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Picks a decal for a family, deterministically in the seed. The live path is strict: if
        /// the whitelist has no matching prefab it returns null instead of substituting unrelated
        /// artwork. The broad fallback survives only behind the explicit non-building diagnostic.
        /// </summary>
        public DecalPrefabInfo Pick(OverlayFamily family, bool allowNonBuildingDecals, ref Unity.Mathematics.Random rng, out bool wasFamilyMatch)
        {
            wasFamilyMatch = false;

            List<DecalPrefabInfo> pool = allowNonBuildingDecals ? m_All : m_AutoPool;
            if (pool.Count == 0)
            {
                pool = allowNonBuildingDecals ? m_All : null;
                if (pool == null)
                {
                    return null;
                }

                if (pool.Count == 0)
                {
                    return null;
                }
            }

            int matches = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                OverlayFamily available = allowNonBuildingDecals
                    ? pool[i].Families
                    : pool[i].AutomaticFamilies;
                if ((available & family) != 0)
                {
                    matches++;
                }
            }

            if (matches > 0)
            {
                wasFamilyMatch = true;
                int wanted = rng.NextInt(matches);
                for (int i = 0; i < pool.Count; i++)
                {
                    OverlayFamily available = allowNonBuildingDecals
                        ? pool[i].Families
                        : pool[i].AutomaticFamilies;
                    if ((available & family) != 0 && wanted-- == 0)
                    {
                        return pool[i];
                    }
                }
            }

            return allowNonBuildingDecals ? pool[rng.NextInt(pool.Count)] : null;
        }

        public string Describe(int maxLines)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(
                "decal catalogue: {0} object prefabs scanned, {1} decals found, {2} able to draw on buildings, "
              + "{3} whitelisted and big enough to place automatically (shortest edge >= {4}m)",
                TotalObjectPrefabsScanned, m_All.Count, m_BuildingCapable.Count, m_AutoPool.Count, MinAutoEdge);

            sb.AppendLine();
            sb.Append("  automatic source whitelist: ");
            for (int i = 0; i < s_ApprovedAutomaticSources.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(s_ApprovedAutomaticSources[i].Key)
                  .Append(" -> ")
                  .Append(s_ApprovedAutomaticSources[i].Value);
            }

            if (Largest != null)
            {
                sb.AppendLine();
                sb.Append("  largest building-capable (diagnostic only): ").Append(Largest.ToString());
            }

            // Biggest first, not alphabetically first: an alphabetical listing of 1,696 decals
            // shows nothing but the alphabet pack, which is exactly what happened the first time.
            List<DecalPrefabInfo> byArea = new List<DecalPrefabInfo>(m_AutoPool);
            byArea.Sort(CompareByFootprintDescending);

            int shown = 0;
            for (int i = 0; i < byArea.Count && shown < maxLines; i++, shown++)
            {
                sb.AppendLine();
                sb.Append("  + ").Append(byArea[i].ToString());
            }

            if (byArea.Count > shown)
            {
                sb.AppendLine();
                sb.AppendFormat("  ... and {0} more in the automatic pool", byArea.Count - shown);
            }

            if (m_BuildingCapable.Count == 0 && m_All.Count > 0)
            {
                sb.AppendLine();
                sb.Append("  no decal on this install declares DecalLayers.Buildings; ")
                  .Append("the first few decals found were:");
                for (int i = 0; i < m_All.Count && i < 10; i++)
                {
                    sb.AppendLine();
                    sb.Append("  - ").Append(m_All[i].ToString());
                }
            }

            return sb.ToString();
        }
    }
}
