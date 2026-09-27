using Colossal.Mathematics;
using Colossal.UI.Binding;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Game.UI;
using Game.UI.InGame;
using SeenBetterDays.Data;
using SeenBetterDays.Designs;
using SeenBetterDays.Rendering;
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.InputSystem;
using UnityEngine.Scripting;

namespace SeenBetterDays.Systems
{
    /// <summary>The building being decorated in design mode, or Entity.Null. The weathering
    /// and decal systems leave it alone: it is a blank canvas until design mode ends.</summary>
    public static class DesignCanvas
    {
        public static Entity Building = Entity.Null;
    }

    /// <summary>
    /// The design panel's bindings. Commands from the panel are queued here and carried out by
    /// <see cref="DesignStudioSystem"/>, which runs in the simulation's phase with the rest of
    /// the mod's entity work.
    /// </summary>
    public partial class DesignStudioUISystem : UISystemBase
    {
        private const string Group = "seenBetterDays";

        private ValueBinding<string> m_Building;
        private ValueBinding<string> m_Message;
        private ValueBinding<bool> m_Capturing;
        private ValueBinding<string> m_Designs;
        private ValueBinding<bool> m_AskName;
        private ValueBinding<string> m_SuggestedName;

        public enum Command
        {
            None,
            Export,
            ClearPlaced,
            OpenFolder,
            Load,
            NameChosen,
            Exit,
        }

        public Command PendingCommand { get; private set; }
        public VisualState PendingState { get; private set; }
        public int PendingIndex { get; private set; }
        public string PendingName { get; private set; }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            AddBinding(m_Building = new ValueBinding<string>(Group, "designBuilding", string.Empty));
            AddBinding(m_Message = new ValueBinding<string>(Group, "designMessage", string.Empty));
            AddBinding(m_Capturing = new ValueBinding<bool>(Group, "designCapturing", false));
            AddBinding(m_Designs = new ValueBinding<string>(Group, "designList", "[]"));
            AddBinding(m_AskName = new ValueBinding<bool>(Group, "designAskName", false));
            AddBinding(m_SuggestedName = new ValueBinding<string>(Group, "designSuggestedName", string.Empty));
            AddBinding(new TriggerBinding<int>(Group, "designLoad", index =>
            {
                PendingCommand = Command.Load;
                PendingIndex = index;
            }));
            AddBinding(new TriggerBinding<string>(Group, "designNameChosen", name =>
            {
                PendingCommand = Command.NameChosen;
                PendingName = name;
            }));
            AddBinding(new TriggerBinding<int>(Group, "designExport", state =>
            {
                PendingCommand = Command.Export;
                PendingState = (VisualState)state;
            }));
            AddBinding(new TriggerBinding(Group, "designClearPlaced", () => PendingCommand = Command.ClearPlaced));
            AddBinding(new TriggerBinding(Group, "designOpenFolder", () => PendingCommand = Command.OpenFolder));
            AddBinding(new TriggerBinding(Group, "designExit", () => PendingCommand = Command.Exit));
        }

        public void ClearCommand()
        {
            PendingCommand = Command.None;
        }

        public void Show(string building, string message)
        {
            m_Building.Update(building ?? string.Empty);
            m_Message.Update(message ?? string.Empty);
        }

        public void SetMessage(string message)
        {
            m_Message.Update(message ?? string.Empty);
        }

        public void SetCapturing(bool capturing)
        {
            m_Capturing.Update(capturing);
        }

        /// <summary>The designs offered to start from, as a JSON array for the panel.</summary>
        public void SetDesigns(string json)
        {
            m_Designs.Update(json ?? "[]");
        }

        public void AskName(string suggested)
        {
            m_SuggestedName.Update(suggested ?? string.Empty);
            m_AskName.Update(true);
        }

        public void CloseNamePrompt()
        {
            m_AskName.Update(false);
        }
    }

    /// <summary>
    /// Design mode: a building becomes a blank canvas, the player decorates it with Anarchy and
    /// Extra Detailing Tools, and exports what they placed as a hand-made design for the state
    /// they choose. The export is written to the player's designs folder, with a screenshot, and
    /// is used in their own city straight away.
    ///
    /// Ctrl+Alt+M on a selected building enters and leaves design mode, when the Design tools
    /// option is on. The panel carries the rest.
    /// </summary>
    public partial class DesignStudioSystem : GameSystemBase
    {
        /// <summary>How far outside the building's bounds a decal still counts as part of the
        /// design, in metres: decals sit on the wall surface, which can be at the very edge.</summary>
        private const float BoundsMargin = 2f;

        /// <summary>Frames between hiding the panel and taking the screenshot, and between the
        /// screenshot and showing the panel again.</summary>
        private const int CaptureDelayFrames = 3;

        private DesignStudioUISystem m_UI;
        private SelectedInfoUISystem m_SelectedInfo;
        private BuildingWeatheringSystem m_Weathering;
        private BuildingOverlayTestSystem m_Harness;
        private PrefabSystem m_PrefabSystem;
        private Game.Serialization.SaveGameSystem m_SaveGameSystem;
        private EntityQuery m_ObjectQuery;

        private Game.Rendering.RenderingSystem m_RenderingSystem;
        private bool m_RestoreOverlay;
        private bool m_RestoreView;

        private string m_BuildingName;
        private string m_PendingScreenshot;
        private string m_MessageAfterCapture;
        private int m_CaptureCountdown;
        private int m_ShowCountdown;
        private bool m_ConfirmClear;

        /// <summary>The designs offered in the panel for the canvas's model, in panel order.</summary>
        private List<DesignLibrary.Resolved> m_Offered = new List<DesignLibrary.Resolved>();

        /// <summary>The state an export was asked for while the name prompt is open.</summary>
        private VisualState m_ExportAfterName;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_UI = World.GetOrCreateSystemManaged<DesignStudioUISystem>();
            m_SelectedInfo = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_Weathering = World.GetOrCreateSystemManaged<BuildingWeatheringSystem>();
            m_Harness = World.GetOrCreateSystemManaged<BuildingOverlayTestSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_SaveGameSystem = World.GetOrCreateSystemManaged<Game.Serialization.SaveGameSystem>();
            m_RenderingSystem = World.GetOrCreateSystemManaged<Game.Rendering.RenderingSystem>();

            // Objects the player placed themselves: no owner (a building's own decals have one),
            // not a tool preview, not deleted, and not one of ours.
            m_ObjectQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Object>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Owner>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<WeatheringOverlay>(),
                },
            });
        }

        [Preserve]
        protected override void OnUpdate()
        {
            AdvanceCapture();

            if (SaveMutationGate.IsBlocked(m_SaveGameSystem))
            {
                return;
            }

            bool toolsOn = Mod.Settings != null && Mod.Settings.EnableDesignTools;
            Entity canvas = DesignCanvas.Building;

            if (canvas != Entity.Null && (!toolsOn || !EntityManager.Exists(canvas)
                                          || EntityManager.HasComponent<Deleted>(canvas)))
            {
                Leave();
                return;
            }

            if (toolsOn && ToggleKeyPressed())
            {
                if (canvas != Entity.Null) Leave(); else Enter(m_SelectedInfo.selectedEntity);
            }

            DesignStudioUISystem.Command command = m_UI.PendingCommand;
            if (command == DesignStudioUISystem.Command.None)
            {
                return;
            }

            VisualState state = m_UI.PendingState;
            int index = m_UI.PendingIndex;
            string chosenName = m_UI.PendingName;
            m_UI.ClearCommand();
            if (DesignCanvas.Building == Entity.Null)
            {
                return;
            }

            switch (command)
            {
                case DesignStudioUISystem.Command.Export:
                    m_ConfirmClear = false;
                    if (Mod.Settings != null && !Mod.Settings.DesignerNameAsked)
                    {
                        // First export: ask once for the name, pre-filled with the account name.
                        m_ExportAfterName = state;
                        m_UI.AskName(string.IsNullOrEmpty(Mod.Settings.DesignerName)
                            ? AccountName() : Mod.Settings.DesignerName);
                    }
                    else
                    {
                        Export(state);
                    }

                    break;
                case DesignStudioUISystem.Command.NameChosen:
                    if (Mod.Settings != null)
                    {
                        Mod.Settings.DesignerName = (chosenName ?? string.Empty).Trim();
                        Mod.Settings.DesignerNameAsked = true;
                        Mod.Settings.ApplyAndSave();
                    }

                    m_UI.CloseNamePrompt();
                    Export(m_ExportAfterName);
                    break;
                case DesignStudioUISystem.Command.Load:
                    Load(index);
                    break;
                case DesignStudioUISystem.Command.ClearPlaced:
                    ClearPlaced();
                    break;
                case DesignStudioUISystem.Command.OpenFolder:
                    OpenFolder();
                    break;
                case DesignStudioUISystem.Command.Exit:
                    Leave();
                    break;
            }
        }

        private static bool ToggleKeyPressed()
        {
            Keyboard keyboard = Keyboard.current;
            return keyboard != null
                && keyboard.ctrlKey.isPressed
                && keyboard.altKey.isPressed
                && keyboard.mKey.wasPressedThisFrame;
        }

        private void Enter(Entity building)
        {
            string reason;
            Entity prefab;
            int level;
            if (building == Entity.Null
                || !BuildingClassifier.Classify(EntityManager, building, out prefab, out level, out reason).IsEligible())
            {
                Mod.Log.Info("Seen Better Days: design mode needs a selected growable building.");
                return;
            }

            try
            {
                m_BuildingName = m_PrefabSystem.GetPrefabName(prefab);
            }
            catch
            {
                m_BuildingName = "?";
            }

            DesignCanvas.Building = building;
            m_ConfirmClear = false;
            m_Weathering.ClearForDesign(building);
            DecalObjectOverlayRenderer decals = m_Harness.DecalRenderer;
            if (decals != null)
            {
                decals.RemoveIncludingUntracked(building);
            }

            m_UI.Show(m_BuildingName, "Place decals with Anarchy and Extra Detailing Tools, frame the building, then "
                                    + "export: the screenshot is taken without the interface.");
            OfferDesigns();
            Mod.Log.Info("Seen Better Days: design mode on building " + building.Index + " (" + m_BuildingName + ").");
        }

        private void Leave()
        {
            Entity building = DesignCanvas.Building;
            DesignCanvas.Building = Entity.Null;
            m_UI.Show(string.Empty, string.Empty);
            m_UI.CloseNamePrompt();
            m_UI.SetDesigns("[]");
            m_Offered.Clear();
            m_ConfirmClear = false;

            if (building != Entity.Null && EntityManager.Exists(building))
            {
                // The decal layer picks it up again on its next pass; the colour is put back now.
                m_Weathering.RefreshNow(building);
                Mod.Log.Info("Seen Better Days: design mode ended on building " + building.Index + ".");
            }
        }

        // ---- Export ---------------------------------------------------------------------------

        private struct Found
        {
            public Entity Entity;
            public DecalPrefabInfo Info;
            public int Pack;
            public float3 LocalPosition;
            public quaternion LocalRotation;
            public float DistanceSq;
        }

        /// <summary>
        /// The decals the player placed on the canvas: every standalone decal object whose
        /// position falls inside the building's bounds, plus a margin. Those not on the whitelist
        /// are counted and left out.
        /// </summary>
        private List<Found> CollectPlaced(out int notWhitelisted, out string failure)
        {
            notWhitelisted = 0;
            failure = null;
            var found = new List<Found>();

            DecalPrefabCatalog catalog = m_Harness.Catalog;
            if (catalog == null)
            {
                failure = "The decal catalogue is not ready yet.";
                return found;
            }

            Entity building = DesignCanvas.Building;
            Transform transform = EntityManager.GetComponentData<Transform>(building);
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(building).m_Prefab;
            Bounds3 bounds = EntityManager.HasComponent<ObjectGeometryData>(prefab)
                ? EntityManager.GetComponentData<ObjectGeometryData>(prefab).m_Bounds
                : new Bounds3(new float3(-30f, 0f, -30f), new float3(30f, 60f, 30f));
            bounds.min -= BoundsMargin;
            bounds.max += BoundsMargin;
            quaternion inverse = math.inverse(transform.m_Rotation);

            NativeArray<Entity> objects = m_ObjectQuery.ToEntityArray(Allocator.TempJob);
            try
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    Entity e = objects[i];
                    DecalPrefabInfo info;
                    if (!catalog.TryGetByPrefab(EntityManager.GetComponentData<PrefabRef>(e).m_Prefab, out info))
                    {
                        continue;
                    }

                    Transform t = EntityManager.GetComponentData<Transform>(e);
                    float3 local = math.mul(inverse, t.m_Position - transform.m_Position);
                    if (math.any(local < bounds.min) || math.any(local > bounds.max))
                    {
                        continue;
                    }

                    int pack = DecalPrefabCatalog.PackOf(info.Name);
                    if (pack < 0)
                    {
                        notWhitelisted++;
                        continue;
                    }

                    found.Add(new Found
                    {
                        Entity = e,
                        Info = info,
                        Pack = pack,
                        LocalPosition = local,
                        LocalRotation = math.mul(inverse, t.m_Rotation),
                        DistanceSq = math.lengthsq(local.xz - MathUtils.Center(bounds).xz),
                    });
                }
            }
            finally
            {
                objects.Dispose();
            }

            // Stable, and when over the limit, the decals nearest the middle of the building win.
            found.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));
            return found;
        }

        private void Export(VisualState state)
        {
            if (Array.IndexOf(DesignLibrary.DesignableStates, state) < 0)
            {
                return;
            }

            int notWhitelisted;
            string failure;
            List<Found> found = CollectPlaced(out notWhitelisted, out failure);
            if (failure != null)
            {
                m_UI.SetMessage(failure);
                return;
            }

            if (found.Count == 0)
            {
                m_UI.SetMessage("No whitelisted decals found on this building"
                              + (notWhitelisted > 0 ? " (" + notWhitelisted + " from packs not on the whitelist)." : "."));
                return;
            }

            int overLimit = math.max(0, found.Count - DesignLibrary.MaxDecals);
            if (overLimit > 0)
            {
                found.RemoveRange(DesignLibrary.MaxDecals, overLimit);
            }

            string author = Mod.Settings != null && Mod.Settings.DesignerName != null
                ? Mod.Settings.DesignerName.Trim() : string.Empty;

            var file = new DecalDesignFile
            {
                format = DesignLibrary.FormatVersion,
                building = m_BuildingName,
                state = state.ToString(),
                author = author,
                created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                decals = new DesignedDecal[found.Count],
            };

            int scaled = 0;
            for (int i = 0; i < found.Count; i++)
            {
                Found f = found[i];
                float3 scale = ExtraDetailingScale.Read(EntityManager, f.Entity);
                if (math.any(math.abs(scale - 1f) > 0.001f))
                {
                    scaled++;
                }

                file.decals[i] = new DesignedDecal
                {
                    decal = f.Info.Name,
                    pack = DecalPrefabCatalog.PackName(f.Pack),
                    position = new[] { Round(f.LocalPosition.x), Round(f.LocalPosition.y), Round(f.LocalPosition.z) },
                    rotation = new[] { f.LocalRotation.value.x, f.LocalRotation.value.y, f.LocalRotation.value.z, f.LocalRotation.value.w },
                    scale = new[] { scale.x, scale.y, scale.z },
                };
            }

            // <building>/<state>-<author>-<date>: the author in the name, so a designer can tell
            // their own designs apart once they sit among others, and the name stays unique.
            string folder = Path.Combine(DesignLibrary.Instance.LocalFolder, SafeName(m_BuildingName),
                                         state + (author.Length > 0 ? "-" + SafeName(author) : string.Empty)
                                         + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            string jsonPath = Path.Combine(folder, DesignLibrary.FileName);
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(jsonPath, Newtonsoft.Json.JsonConvert.SerializeObject(file, Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception e)
            {
                m_UI.SetMessage("Could not write the design: " + e.Message);
                Mod.Log.Warn("Seen Better Days: could not write design to " + jsonPath + ": " + e);
                return;
            }

            DesignLibrary.Instance.AddLocal(jsonPath, Mod.Log);
            OfferDesigns();

            string summary = "Exported " + found.Count + " decal(s) as " + state + ".";
            if (notWhitelisted > 0) summary += " " + notWhitelisted + " not on the whitelist, left out.";
            if (overLimit > 0) summary += " " + overLimit + " over the limit of " + DesignLibrary.MaxDecals + ", left out.";
            if (scaled > 0) summary += " " + scaled + " scaled with Extra Detailing Tools: scale is not supported yet, so this design will not be used until it is.";

            Mod.Log.Info("Seen Better Days: " + summary + " Written to " + jsonPath + ".");

            // Hide the panel, take the screenshot a few frames later, then show the panel again.
            m_PendingScreenshot = Path.Combine(folder, "screenshot.png");
            m_MessageAfterCapture = summary + " Saved in " + folder;
            m_CaptureCountdown = CaptureDelayFrames;
            m_UI.SetCapturing(true);
            HideInterface();
        }

        /// <summary>
        /// Hides the whole game interface and the in-world overlays (selection outline, markers)
        /// for the screenshot, the way the game's own benchmark hides its UI. Put back by
        /// <see cref="ShowInterface"/> a few frames later.
        /// </summary>
        private void HideInterface()
        {
            try
            {
                m_RestoreOverlay = !m_RenderingSystem.hideOverlay;
                m_RenderingSystem.hideOverlay = true;

                Colossal.UI.UIView view = Game.SceneFlow.GameManager.instance?.userInterface?.view;
                m_RestoreView = view != null && view.enabled;
                if (m_RestoreView)
                {
                    view.enabled = false;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn("Seen Better Days: could not hide the interface for the screenshot: " + e.Message);
            }
        }

        private void ShowInterface()
        {
            try
            {
                if (m_RestoreOverlay)
                {
                    m_RenderingSystem.hideOverlay = false;
                }

                Colossal.UI.UIView view = Game.SceneFlow.GameManager.instance?.userInterface?.view;
                if (m_RestoreView && view != null)
                {
                    view.enabled = true;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Warn("Seen Better Days: could not show the interface again after the screenshot: " + e.Message);
            }

            m_RestoreOverlay = false;
            m_RestoreView = false;
        }

        private void AdvanceCapture()
        {
            if (m_CaptureCountdown > 0 && --m_CaptureCountdown == 0)
            {
                try
                {
                    UnityEngine.ScreenCapture.CaptureScreenshot(m_PendingScreenshot);
                }
                catch (Exception e)
                {
                    Mod.Log.Warn("Seen Better Days: could not take the design screenshot: " + e.Message);
                }

                m_ShowCountdown = CaptureDelayFrames;
                return;
            }

            if (m_ShowCountdown > 0 && --m_ShowCountdown == 0)
            {
                ShowInterface();
                m_UI.SetCapturing(false);
                m_UI.SetMessage(m_MessageAfterCapture);
            }
        }

        /// <summary>
        /// Deletes the whitelisted decals placed on the canvas, so a design can be checked by
        /// letting the mod put it back. Asks twice: these are the player's own objects.
        /// </summary>
        private void ClearPlaced()
        {
            if (!m_ConfirmClear)
            {
                m_ConfirmClear = true;
                m_UI.SetMessage("This deletes the whitelisted decals you placed on this building. Press again to confirm.");
                return;
            }

            m_ConfirmClear = false;
            int notWhitelisted;
            string failure;
            List<Found> found = CollectPlaced(out notWhitelisted, out failure);
            if (failure != null)
            {
                m_UI.SetMessage(failure);
                return;
            }

            for (int i = 0; i < found.Count; i++)
            {
                if (EntityManager.Exists(found[i].Entity) && !EntityManager.HasComponent<Deleted>(found[i].Entity))
                {
                    EntityManager.AddComponent<Deleted>(found[i].Entity);
                }
            }

            m_UI.SetMessage("Deleted " + found.Count + " decal(s)."
                          + (notWhitelisted > 0 ? " " + notWhitelisted + " not on the whitelist were left." : string.Empty));
        }

        /// <summary>
        /// Lists the designs there already are for the canvas's model - shipped and the player's
        /// own, every state - so a new design can start from one of them.
        /// </summary>
        private void OfferDesigns()
        {
            Entity building = DesignCanvas.Building;
            m_Offered = building != Entity.Null && EntityManager.HasComponent<PrefabRef>(building)
                ? DesignLibrary.Instance.ListFor(EntityManager.GetComponentData<PrefabRef>(building).m_Prefab)
                : new List<DesignLibrary.Resolved>();

            var rows = new List<object>(m_Offered.Count);
            for (int i = 0; i < m_Offered.Count; i++)
            {
                DesignLibrary.Resolved d = m_Offered[i];
                rows.Add(new
                {
                    index = i,
                    state = d.State.ToString(),
                    author = d.Author ?? string.Empty,
                    decals = d.Prefabs.Length,
                    shipped = d.Shipped,
                });
            }

            m_UI.SetDesigns(Newtonsoft.Json.JsonConvert.SerializeObject(rows));
        }

        /// <summary>
        /// Puts an existing design's decals on the canvas as the player's own objects, to edit and
        /// export as a new design. Added to whatever is already placed.
        /// </summary>
        private void Load(int index)
        {
            if (index < 0 || index >= m_Offered.Count || m_Harness.DecalRenderer == null)
            {
                return;
            }

            DesignLibrary.Resolved design = m_Offered[index];
            int placed = m_Harness.DecalRenderer.SpawnAsPlacedObjects(DesignCanvas.Building, design);
            m_UI.SetMessage("Placed the " + design.State + " design"
                          + (string.IsNullOrEmpty(design.Author) ? string.Empty : " by " + design.Author)
                          + ": " + placed + " decal(s), now yours to edit. Export it as a new design when done.");
            Mod.Log.Info("Seen Better Days: design " + design.Name + " loaded onto the canvas as " + placed + " placed decal(s).");
        }

        /// <summary>The signed-in account's name (Steam or Paradox), offered as the designer
        /// name. Empty when the platform does not give one.</summary>
        private static string AccountName()
        {
            try
            {
                Colossal.PSI.Common.PlatformManager platforms = Colossal.PSI.Common.PlatformManager.instance;
                return platforms != null && platforms.userName != null ? platforms.userName : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Opens the player's designs folder in the system's file browser.</summary>
        private void OpenFolder()
        {
            string folder = DesignLibrary.Instance.LocalFolder;
            try
            {
                Directory.CreateDirectory(folder);
                UnityEngine.Application.OpenURL("file:///" + folder.Replace('\\', '/'));
                m_UI.SetMessage("Your designs are in " + folder);
            }
            catch (Exception e)
            {
                m_UI.SetMessage("Your designs are in " + folder + " (could not open it: " + e.Message + ")");
            }
        }

        private static float Round(float value)
        {
            return (float)Math.Round(value, 3);
        }

        private static string SafeName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = (name ?? "building").ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            }

            return new string(chars);
        }
    }

    /// <summary>
    /// Reads Extra Detailing Tools' per-object scale when that mod is installed, without a
    /// reference to it: the component is found by name and read through reflection, which is
    /// fine for an export that runs once per click.
    /// </summary>
    internal static class ExtraDetailingScale
    {
        private static bool s_LookedUp;
        private static ComponentType s_Type;
        private static System.Reflection.MethodInfo s_Get;
        private static System.Reflection.FieldInfo s_Scale;

        public static float3 Read(EntityManager entityManager, Entity entity)
        {
            LookUp();
            if (s_Get == null)
            {
                return new float3(1f);
            }

            try
            {
                if (!entityManager.HasComponent(entity, s_Type))
                {
                    return new float3(1f);
                }

                object data = s_Get.Invoke(entityManager, new object[] { entity });
                return (float3)s_Scale.GetValue(data);
            }
            catch
            {
                return new float3(1f);
            }
        }

        private static void LookUp()
        {
            if (s_LookedUp)
            {
                return;
            }

            s_LookedUp = true;
            try
            {
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type = assembly.GetType("ExtraDetailingTools.Prefabs.TransformObject", false);
                    if (type == null)
                    {
                        continue;
                    }

                    System.Reflection.FieldInfo scale = type.GetField("m_Scale");
                    System.Reflection.MethodInfo get = null;
                    foreach (System.Reflection.MethodInfo method in typeof(EntityManager).GetMethods())
                    {
                        System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                        if (method.Name == "GetComponentData" && method.IsGenericMethodDefinition
                            && parameters.Length == 1 && parameters[0].ParameterType == typeof(Entity))
                        {
                            get = method.MakeGenericMethod(type);
                            break;
                        }
                    }

                    if (scale != null && scale.FieldType == typeof(float3) && get != null)
                    {
                        s_Type = ComponentType.ReadOnly(type);
                        s_Scale = scale;
                        s_Get = get;
                    }

                    return;
                }
            }
            catch (Exception e)
            {
                Mod.Log.Info("Seen Better Days: could not look up Extra Detailing Tools' scale: " + e.Message);
            }
        }
    }
}
