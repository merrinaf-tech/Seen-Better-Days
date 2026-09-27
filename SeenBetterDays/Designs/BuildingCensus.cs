using Colossal.Logging;
using Game.Prefabs;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Collections;
using Unity.Entities;

namespace SeenBetterDays.Designs
{
    /// <summary>
    /// The growable buildings loaded in this game, and the models they are built from.
    ///
    /// Each level of a building is its own prefab, but levels often share a model - the game
    /// commonly uses one for levels 1 and 2, another for 3 and 4 and a third for 5. A design is
    /// made on a wall, so it belongs to the model: a design made on level 1 fits level 2 exactly
    /// when both use the same meshes. The model is identified by the names of the meshes a
    /// prefab uses, which stay the same from one session to the next.
    ///
    /// Also written to the log once per load, by source, so the scale of the design project can
    /// be stated from a real count.
    /// </summary>
    public sealed class BuildingCensus
    {
        private static readonly Regex s_Level = new Regex(@"_L\d(?=_|$)", RegexOptions.Compiled);

        private readonly Dictionary<string, string> m_ModelByName = new Dictionary<string, string>();
        private readonly Dictionary<Entity, string> m_ModelByPrefab = new Dictionary<Entity, string>();

        /// <summary>The model a building prefab is built from, by the prefab's name.</summary>
        public bool TryGetModel(string prefabName, out string model)
        {
            model = null;
            return prefabName != null && m_ModelByName.TryGetValue(prefabName, out model);
        }

        public bool TryGetModel(Entity prefab, out string model)
        {
            return m_ModelByPrefab.TryGetValue(prefab, out model);
        }

        public static BuildingCensus Take(EntityManager entityManager, PrefabSystem prefabSystem,
                                          EntityQuery growablePrefabs, ILog log)
        {
            var census = new BuildingCensus();
            var prefabsBySource = new SortedDictionary<string, int>();
            var familiesBySource = new SortedDictionary<string, HashSet<string>>();
            var modelsBySource = new SortedDictionary<string, HashSet<string>>();
            var allModels = new HashSet<string>();
            var meshNames = new List<string>();

            NativeArray<Entity> prefabs = growablePrefabs.ToEntityArray(Allocator.TempJob);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity entity = prefabs[i];
                    PrefabBase prefab;
                    if (!prefabSystem.TryGetPrefab(entity, out prefab) || prefab == null)
                    {
                        continue;
                    }

                    string name = prefab.name ?? string.Empty;
                    string model = ModelOf(entityManager, prefabSystem, entity, name, meshNames);
                    census.m_ModelByName[name] = model;
                    census.m_ModelByPrefab[entity] = model;
                    allModels.Add(model);

                    string source = SourceOf(prefab);
                    int count;
                    prefabsBySource.TryGetValue(source, out count);
                    prefabsBySource[source] = count + 1;
                    Add(familiesBySource, source, s_Level.Replace(name, string.Empty));
                    Add(modelsBySource, source, model);
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            int totalPrefabs = 0;
            foreach (KeyValuePair<string, int> pair in prefabsBySource)
            {
                totalPrefabs += pair.Value;
            }

            var sb = new StringBuilder("Seen Better Days: growable building census - ");
            sb.Append(totalPrefabs).Append(" prefabs (one per level), ")
              .Append(allModels.Count).Append(" distinct models, ")
              .Append(allModels.Count * DesignLibrary.DesignableStates.Length)
              .Append(" model-state pairs a design could cover. By source:");
            foreach (KeyValuePair<string, int> pair in prefabsBySource)
            {
                sb.Append("\n  ").Append(pair.Key).Append(": ").Append(pair.Value).Append(" prefabs, ")
                  .Append(familiesBySource[pair.Key].Count).Append(" families, ")
                  .Append(modelsBySource[pair.Key].Count).Append(" models");
            }

            log.Info(sb.ToString());
            return census;
        }

        /// <summary>
        /// The names of the meshes a prefab uses, sorted and joined. A prefab without meshes (or
        /// whose meshes cannot be named) is its own model, by its prefab name.
        /// </summary>
        private static string ModelOf(EntityManager entityManager, PrefabSystem prefabSystem, Entity prefab,
                                      string prefabName, List<string> scratch)
        {
            scratch.Clear();
            if (entityManager.HasBuffer<SubMesh>(prefab))
            {
                DynamicBuffer<SubMesh> subMeshes = entityManager.GetBuffer<SubMesh>(prefab, true);
                for (int i = 0; i < subMeshes.Length; i++)
                {
                    string meshName = null;
                    try
                    {
                        meshName = prefabSystem.GetPrefabName(subMeshes[i].m_SubMesh);
                    }
                    catch
                    {
                        // Unnamed mesh: falls back to the prefab name below.
                    }

                    if (!string.IsNullOrEmpty(meshName) && !scratch.Contains(meshName))
                    {
                        scratch.Add(meshName);
                    }
                }
            }

            if (scratch.Count == 0)
            {
                return "prefab:" + prefabName;
            }

            scratch.Sort(System.StringComparer.Ordinal);
            return string.Join("|", scratch.ToArray());
        }

        private static void Add(SortedDictionary<string, HashSet<string>> map, string key, string value)
        {
            HashSet<string> set;
            if (!map.TryGetValue(key, out set))
            {
                set = new HashSet<string>();
                map[key] = set;
            }

            set.Add(value);
        }

        private static string SourceOf(PrefabBase prefab)
        {
            if (prefab.isBuiltin)
            {
                return "base game and DLC";
            }

            string path = null;
            try
            {
                path = prefab.asset != null ? prefab.asset.path : null;
            }
            catch
            {
                // Only the grouping is lost.
            }

            if (!string.IsNullOrEmpty(path))
            {
                string normalised = path.Replace('\\', '/');
                int at = normalised.IndexOf("/pdx_mods/", System.StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    string rest = normalised.Substring(at + "/pdx_mods/".Length);
                    int slash = rest.IndexOf('/');
                    return "Paradox Mods " + (slash > 0 ? rest.Substring(0, slash) : rest);
                }
            }

            return prefab.isSubscribedMod ? "subscribed mod (unknown)" : "local or other";
        }
    }
}
