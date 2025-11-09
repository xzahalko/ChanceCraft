// ChanceCraft.cs - updated to add ForceSimulateTabSwitchRefresh_Deep improvements and avoid System.Collections.Generic.Queue<T>
// - Replaced Queue<UnityEngine.Transform> usage with a List<UnityEngine.Transform> FIFO to avoid forwarded generic Queue<> dependency.
// - Keep other previous fixes (non-generic Queue in TryExtractRecipeFromWrapper, HashSet -> List replacements where needed).
//
// Note: You still need Valheim/Unity assemblies referenced (Assembly-CSharp.dll, UnityEngine.dll). This change avoids requiring System.dll for Queue<T>.

using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace ChanceCraft
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public class ChanceCraftPlugin : BaseUnityPlugin
    {
        public const string pluginID = "deep.ChanceCraft";
        public const string pluginName = "Chance Craft";
        public const string pluginVersion = "1.1.2";

        private Harmony _harmony;

        private static ConfigEntry<float> weaponSuccessChance;
        private static ConfigEntry<float> armorSuccessChance;
        private static ConfigEntry<float> arrowSuccessChance;
        private static ConfigEntry<bool> loggingEnabled;

        // Runtime shared state
        private static bool IsDoCraft;
        private static List<ItemDrop.ItemData> _preCraftSnapshot;
        private static Recipe _snapshotRecipe;
        private static Dictionary<ItemDrop.ItemData, (int quality, int variant)> _preCraftSnapshotData;
        private static Dictionary<int, int> _preCraftSnapshotHashQuality;

        // Replace HashSet<string> with List<string> to avoid ISet<> assembly requirement
        private static List<string> _suppressedRecipeKeys = new List<string>();
        private static readonly object _suppressedRecipeKeysLock = new object();

        private static readonly HashSet<string> _recentRemovalKeys = new HashSet<string>();
        private static readonly object _recentRemovalKeysLock = new object();

        private static ItemDrop.ItemData _upgradeTargetItem;
        private static Recipe _upgradeRecipe;
        private static Recipe _upgradeGuiRecipe;
        private static bool _isUpgradeDetected;
        private static int _upgradeTargetItemIndex = -1;
        private static List<(string name, int amount)> _upgradeGuiRequirements;

        private static bool VERBOSE_DEBUG = false; // set false when done

        private static readonly object _savedResourcesLock = new object();

        void Awake()
        {
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), "ChanceCraft");

            loggingEnabled = Config.Bind("Logging", "Logging Enabled", true, "Enable logging");
            weaponSuccessChance = Config.Bind("General", "WeaponSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft weapons (0.0 - 1.0)"));
            armorSuccessChance = Config.Bind("General", "ArmorSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft armors (0.0 - 1.0)"));
            arrowSuccessChance = Config.Bind("General", "ArrowSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft arrows (0.0 - 1.0)"));

            LogInfo($"ChanceCraft loaded: weapon={weaponSuccessChance.Value}, armor={armorSuccessChance.Value}, arrow={arrowSuccessChance.Value}");
            Game.isModded = true;
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        #region Logging & helpers

        private static void LogWarning(string msg)
        {
            if (loggingEnabled?.Value ?? false) UnityEngine.Debug.LogWarning($"[ChanceCraft] {msg}");
        }

        private static void LogInfo(string msg)
        {
            if (loggingEnabled?.Value ?? false) UnityEngine.Debug.Log($"[ChanceCraft] {msg}");
        }

        private static string ItemInfo(ItemDrop.ItemData it)
        {
            if (it == null) return "<null>";
            try
            {
                return $"[{it.GetHashCode():X8}] name='{it.m_shared?.m_name}' q={it.m_quality} v={it.m_variant} stack={it.m_stack}";
            }
            catch
            {
                return "<bad item>";
            }
        }

        private static string RecipeInfo(Recipe r)
        {
            if (r == null) return "<null>";
            try
            {
                var name = r.m_item?.m_itemData?.m_shared?.m_name ?? "<no-result>";
                int amount = r.m_amount;
                var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (resourcesField == null) return $"Recipe(result='{name}', amount={amount})";
                var res = resourcesField.GetValue(r) as IEnumerable;
                if (res == null) return $"Recipe(result='{name}', amount={amount})";
                var parts = new List<string>();
                foreach (var req in res)
                {
                    try
                    {
                        var t = req.GetType();
                        var amtObj = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                        int amt = amtObj != null ? Convert.ToInt32(amtObj) : 0;
                        string resName = null;
                        var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                        if (resItem != null)
                        {
                            var itemData = resItem.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                            var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                            resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                        }
                        parts.Add($"{resName ?? "<unknown>"}:{amt}");
                    }
                    catch { parts.Add("<res-err>"); }
                }
                return $"Recipe(result='{name}', amount={amount}, resources=[{string.Join(", ", parts)}])";
            }
            catch { return "<bad recipe>"; }
        }

        private static string RecipeFingerprint(Recipe r)
        {
            if (r == null || r.m_item == null) return "<null>";
            try
            {
                var name = r.m_item.m_itemData?.m_shared?.m_name ?? "<no-result>";
                var parts = new List<string>();
                var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var resObj = resourcesField?.GetValue(r) as IEnumerable;
                if (resObj != null)
                {
                    foreach (var req in resObj)
                    {
                        try
                        {
                            var t = req.GetType();
                            var amountObj = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                            int reqAmt = amountObj != null ? Convert.ToInt32(amountObj) : 0;
                            var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                            string rname = null;
                            if (resItem != null)
                            {
                                var itemData = resItem.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                                var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                rname = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                            }
                            parts.Add($"{rname ?? "<unknown>"}:{reqAmt}");
                        }
                        catch { parts.Add("<res-err>"); }
                    }
                }
                return $"{name}|{r.m_amount}|{string.Join(",", parts)}";
            }
            catch { return "<fingerprint-err>"; }
        }

        private static bool RecipeConsumesResult(Recipe recipe)
        {
            if (recipe == null || recipe.m_item == null) return false;
            try
            {
                var craftedName = recipe.m_item.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(craftedName)) return false;
                var resourcesField = recipe.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var resources = resourcesField?.GetValue(recipe) as IEnumerable;
                if (resources == null) return false;
                foreach (var req in resources)
                {
                    try
                    {
                        var t = req.GetType();
                        var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                        if (resItem == null) continue;
                        var itemData = resItem.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                        var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                        var resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                        if (!string.IsNullOrEmpty(resName) && string.Equals(resName, craftedName, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch { /* ignore malformed */ }
                }
            }
            catch { }
            return false;
        }

        private static ItemDrop.ItemData GetSelectedInventoryItem(InventoryGui gui)
        {
            if (gui == null) return null;
            object TryGet(string name)
            {
                var t = typeof(InventoryGui);
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f.GetValue(gui);
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (p != null) return p.GetValue(gui);
                return null;
            }

            var candidates = new[] { "m_selectedItem", "m_selected", "m_selectedItemData", "m_currentItem", "m_selectedInventoryItem", "m_selectedSlot" };
            foreach (var c in candidates)
            {
                try
                {
                    var val = TryGet(c);
                    if (val is ItemDrop.ItemData idata) return idata;
                    if (val != null)
                    {
                        var valType = val.GetType();
                        var innerField = valType.GetField("m_item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (innerField != null)
                        {
                            var inner = innerField.GetValue(val);
                            if (inner is ItemDrop.ItemData ii) return ii;
                        }
                    }
                }
                catch { /* ignore */ }
            }

            try
            {
                var idxObj = TryGet("m_selectedSlot");
                if (idxObj is int idx)
                {
                    var player = Player.m_localPlayer;
                    var inv = player?.GetInventory();
                    if (inv != null)
                    {
                        var all = inv.GetAllItems();
                        if (idx >= 0 && idx < all.Count) return all[idx];
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        // Helper node class for a non-generic queue BFS
        private class WrapperQueueNode
        {
            public object Obj;
            public string Path;
            public int Depth;
            public WrapperQueueNode(object obj, string path, int depth) { Obj = obj; Path = path; Depth = depth; }
        }

        // Try to find a Recipe object embedded in wrappers using a non-generic queue (System.Collections.Queue)
        private static bool TryExtractRecipeFromWrapper(object wrapper, Recipe excludeRecipe, out Recipe foundRecipe, out string foundPath, int maxDepth = 3)
        {
            foundRecipe = null;
            foundPath = null;
            if (wrapper == null) return false;

            try
            {
                var seen = new HashSet<int>();
                // Use non-generic Queue to avoid needing System assembly for generic types in some project setups
                var q = new System.Collections.Queue();
                q.Enqueue(new WrapperQueueNode(wrapper, "root", 0));

                while (q.Count > 0)
                {
                    var nodeObj = q.Dequeue();
                    if (!(nodeObj is WrapperQueueNode node)) continue;
                    var obj = node.Obj;
                    var path = node.Path;
                    var depth = node.Depth;

                    if (obj == null) continue;
                    int id = RuntimeHelpers.GetHashCode(obj);
                    if (seen.Contains(id)) continue;
                    seen.Add(id);

                    if (obj is Recipe r)
                    {
                        if (!ReferenceEquals(r, excludeRecipe))
                        {
                            foundRecipe = r;
                            foundPath = path;
                            LogWarning($"TryExtractRecipeFromWrapper: found Recipe at path '{path}': {RecipeInfo(r)}");
                            return true;
                        }
                    }

                    if (depth >= maxDepth) continue;

                    Type t = obj.GetType();

                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        try
                        {
                            var val = f.GetValue(obj);
                            if (val == null) continue;

                            if (typeof(Recipe).IsAssignableFrom(f.FieldType))
                            {
                                var maybe = val as Recipe;
                                if (maybe != null && !ReferenceEquals(maybe, excludeRecipe))
                                {
                                    foundRecipe = maybe;
                                    foundPath = $"{path}.{f.Name}";
                                    LogWarning($"TryExtractRecipeFromWrapper: found Recipe field '{f.Name}' at path '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                    return true;
                                }
                            }

                            if (val is IEnumerable ie && !(val is string))
                            {
                                int idx = 0;
                                foreach (var elem in ie)
                                {
                                    if (elem == null) { idx++; continue; }
                                    if (elem is Recipe rr && !ReferenceEquals(rr, excludeRecipe))
                                    {
                                        foundRecipe = rr;
                                        foundPath = $"{path}.{f.Name}[{idx}]";
                                        LogWarning($"TryExtractRecipeFromWrapper: found Recipe in enumerable '{f.Name}' at '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                        return true;
                                    }
                                    q.Enqueue(new WrapperQueueNode(elem, $"{path}.{f.Name}[{idx}]", depth + 1));
                                    idx++;
                                    if (idx > 50) break;
                                }
                                continue;
                            }

                            q.Enqueue(new WrapperQueueNode(val, $"{path}.{f.Name}", depth + 1));
                        }
                        catch { /* ignore per-field issues */ }
                    }

                    foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        try
                        {
                            if (p.GetIndexParameters().Length > 0) continue;
                            if (!p.CanRead) continue;
                            var v = p.GetValue(obj);
                            if (v == null) continue;

                            if (typeof(Recipe).IsAssignableFrom(p.PropertyType))
                            {
                                var maybe = v as Recipe;
                                if (maybe != null && !ReferenceEquals(maybe, excludeRecipe))
                                {
                                    foundRecipe = maybe;
                                    foundPath = $"{path}.{p.Name}";
                                    LogWarning($"TryExtractRecipeFromWrapper: found Recipe property '{p.Name}' at path '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                    return true;
                                }
                            }

                            if (v is IEnumerable ie2 && !(v is string))
                            {
                                int idx = 0;
                                foreach (var elem in ie2)
                                {
                                    if (elem == null) { idx++; continue; }
                                    if (elem is Recipe rr && !ReferenceEquals(rr, excludeRecipe))
                                    {
                                        foundRecipe = rr;
                                        foundPath = $"{path}.{p.Name}[{idx}]";
                                        LogWarning($"TryExtractRecipeFromWrapper: found Recipe in enumerable property '{p.Name}' at '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                        return true;
                                    }
                                    q.Enqueue(new WrapperQueueNode(elem, $"{path}.{p.Name}[{idx}]", depth + 1));
                                    idx++;
                                    if (idx > 50) break;
                                }
                                continue;
                            }

                            q.Enqueue(new WrapperQueueNode(v, $"{path}.{p.Name}", depth + 1));
                        }
                        catch { /* ignore per-property issues */ }
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"TryExtractRecipeFromWrapper exception: {ex}");
            }
            return false;
        }

        private static Recipe GetUpgradeRecipeFromGui(InventoryGui gui)
        {
            if (gui == null) return null;
            try
            {
                var t = typeof(InventoryGui);
                var names = new[] {
                    "m_upgradeRecipe", "m_selectedUpgradeRecipe", "m_selectedRecipe",
                    "m_currentRecipe", "m_selectedUpgrade", "m_selectedUpgradeRecipeData",
                    "m_selectedUpgradeRecipePair", "m_currentUpgradeRecipe", "m_selectedRecipeData", "m_selected",
                    "m_targetRecipe", "m_previewRecipe", "m_craftRecipe"
                };

                foreach (var name in names)
                {
                    try
                    {
                        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (f != null)
                        {
                            var val = f.GetValue(gui);
                            if (val is Recipe r) return r;
                            if (val != null)
                            {
                                if (TryExtractRecipeFromWrapper(val, null, out var inner, out var path)) return inner;
                                var prop = val.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (prop != null && prop.CanRead) return prop.GetValue(val) as Recipe;
                            }
                        }

                        var propInfo = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (propInfo != null)
                        {
                            var val2 = propInfo.GetValue(gui);
                            if (val2 is Recipe r3) return r3;
                            if (val2 != null)
                            {
                                if (TryExtractRecipeFromWrapper(val2, null, out var inner3, out var path3)) return inner3;
                                var p2 = val2.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (p2 != null && p2.CanRead) return p2.GetValue(val2) as Recipe;
                            }
                        }
                    }
                    catch { /* ignore candidate errors */ }
                }

                var selectedRecipeField = t.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (selectedRecipeField != null)
                {
                    var wrapper = selectedRecipeField.GetValue(gui);
                    if (wrapper != null)
                    {
                        if (TryExtractRecipeFromWrapper(wrapper, null, out var inner, out var path)) return inner;
                        var rp = wrapper.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (rp != null && rp.CanRead) return rp.GetValue(wrapper) as Recipe;
                    }
                }

                // fallback scan
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        var v = f.GetValue(gui);
                        if (v is Recipe rr) return rr;
                        if (v != null && TryExtractRecipeFromWrapper(v, null, out var inner2, out var p2)) return inner2;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"GetUpgradeRecipeFromGui exception: {ex}");
            }
            return null;
        }

        #endregion

        #region InventoryGui.DoCrafting patch

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        static class InventoryGuiDoCraftingPatch
        {
//            private static readonly Dictionary<Recipe, object> _savedResources = new Dictionary<Recipe, object>();
            private static readonly Dictionary<string, object> _savedResources = new Dictionary<string, object>();
            private static readonly object _savedResourcesLock = new object();

            // Helper: stable key for a recipe so key survives save/load (name + quality + variant)
            private static string GetRecipeKey(Recipe r)
            {
                if (r == null) return null;
                try
                {
                    // Attempt to get recipe item name / quality / variant in a robust way
                    string name = r.m_item?.m_itemData?.m_shared?.m_name ?? r.name ?? "unknown";
                    int quality = r.m_item?.m_itemData?.m_quality ?? 0;
                    int variant = r.m_item?.m_itemData?.m_variant ?? 0;
                    return $"{name}|q{quality}|v{variant}";
                }
                catch
                {
                    return r.GetHashCode().ToString();
                }
            }

            private static bool _suppressedThisCall = false;
            private static Recipe _savedRecipeForCall = null;

            static void Prefix(InventoryGui __instance)
            {
                _suppressedThisCall = false;
                _savedRecipeForCall = null;

                _isUpgradeDetected = false;
                _upgradeTargetItem = null;
                _upgradeRecipe = null;
                _upgradeGuiRecipe = null;
                _preCraftSnapshot = null;
                _preCraftSnapshotData = null;
                _snapshotRecipe = null;
                _upgradeGuiRequirements = null;
                _upgradeTargetItemIndex = -1;
                _preCraftSnapshotHashQuality = null;

                try
                {
                    var selectedRecipeField = typeof(InventoryGui).GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (selectedRecipeField == null) return;
                    object value = selectedRecipeField.GetValue(__instance);
                    Recipe selectedRecipe = null;
                    if (value != null && value.GetType().Name == "RecipeDataPair")
                    {
                        var recipeProp = value.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (recipeProp != null) selectedRecipe = recipeProp.GetValue(value) as Recipe;
                    }
                    else
                    {
                        selectedRecipe = value as Recipe;
                    }
                    if (selectedRecipe == null) return;

                    // early GUI-wrapped recipe extraction
                    try
                    {
                        if (value != null)
                        {
                            if (TryExtractRecipeFromWrapper(value, selectedRecipe, out var extracted, out var path))
                            {
                                _upgradeGuiRecipe = extracted;
                                try
                                {
                                    var keyg = RecipeFingerprint(extracted);
                                    lock (_suppressedRecipeKeysLock)
                                    {
                                        if (!_suppressedRecipeKeys.Contains(keyg)) _suppressedRecipeKeys.Add(keyg);
                                    }
                                }
                                catch { }
                            }
                            else
                            {
                                var earlyGuiRecipe = GetUpgradeRecipeFromGui(__instance);
                                if (earlyGuiRecipe != null)
                                {
                                    _upgradeGuiRecipe = earlyGuiRecipe;
                                    try
                                    {
                                        var keyg = RecipeFingerprint(earlyGuiRecipe);
                                        lock (_suppressedRecipeKeysLock)
                                        {
                                            if (!_suppressedRecipeKeys.Contains(keyg)) _suppressedRecipeKeys.Add(keyg);
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }

                        if (_upgradeGuiRecipe == null || ReferenceEquals(_upgradeGuiRecipe, selectedRecipe))
                        {
                            var candidateEarly = FindBestUpgradeRecipeCandidate(selectedRecipe);
                            if (candidateEarly != null)
                            {
                                _upgradeRecipe = candidateEarly;
                                if (RecipeConsumesResult(candidateEarly)) _isUpgradeDetected = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Prefix early discovery exception: {ex}");
                    }

                    _savedRecipeForCall = selectedRecipe;

                    // Attempt to capture upgrade target item early
                    try
                    {
                        if (_upgradeTargetItem == null)
                        {
                            var igType = typeof(InventoryGui);
                            var fCraftUpgradeItem = igType.GetField("m_craftUpgradeItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fCraftUpgradeItem != null) _upgradeTargetItem = fCraftUpgradeItem.GetValue(__instance) as ItemDrop.ItemData;

                            if (_upgradeTargetItem == null)
                            {
                                var fUpgradeItems = igType.GetField("m_upgradeItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var fUpgradeIndex = igType.GetField("m_upgradeItemIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (fUpgradeItems != null)
                                {
                                    var list = fUpgradeItems.GetValue(__instance) as System.Collections.IList;
                                    if (list != null && list.Count > 0)
                                    {
                                        int idx = -1;
                                        if (fUpgradeIndex != null)
                                        {
                                            var idxObj = fUpgradeIndex.GetValue(__instance);
                                            if (idxObj is int ii) idx = ii;
                                        }
                                        _upgradeTargetItem = (idx >= 0 && idx < list.Count) ? (list[idx] as ItemDrop.ItemData) : (list[0] as ItemDrop.ItemData);
                                    }
                                }
                            }

                            if (_upgradeTargetItem == null)
                            {
                                var candidateNames = new[] { "m_selectedItem", "m_selected", "m_selectedItemData", "m_selectedInventoryItem" };
                                foreach (var nm in candidateNames)
                                {
                                    try
                                    {
                                        var f = igType.GetField(nm, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        if (f != null)
                                        {
                                            var v = f.GetValue(__instance) as ItemDrop.ItemData;
                                            if (v != null) { _upgradeTargetItem = v; break; }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }

                    // Try to read GUI-provided requirements and detect if they imply an upgrade
                    try
                    {
                        if (TryGetRequirementsFromGui(__instance, out var guiReqs) && guiReqs != null && guiReqs.Count > 0)
                        {
                            bool guiIndicatesUpgrade = false;

                            // explicit craft-upgrade multiplier
                            try
                            {
                                var craftUpgradeFieldLocal = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                                if (craftUpgradeFieldLocal != null)
                                {
                                    var cvObj = craftUpgradeFieldLocal.GetValue(__instance);
                                    if (cvObj is int v && v > 1) guiIndicatesUpgrade = true;
                                }
                            }
                            catch { }

                            // compare target quality vs final quality
                            try
                            {
                                var finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                if (_upgradeTargetItem != null && finalQuality > _upgradeTargetItem.m_quality) guiIndicatesUpgrade = true;
                            }
                            catch { }

                            try { if (RecipeConsumesResult(selectedRecipe)) guiIndicatesUpgrade = true; } catch { }

                            // Compare GUI list to base recipe resources
                            try
                            {
                                var baseResources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                var resourcesFieldBase = selectedRecipe.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var baseResourcesEnum = resourcesFieldBase?.GetValue(selectedRecipe) as IEnumerable;
                                if (baseResourcesEnum != null)
                                {
                                    foreach (var ritem in baseResourcesEnum)
                                    {
                                        try
                                        {
                                            var rt = ritem.GetType();
                                            var nameObj = rt.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ritem);
                                            string resName = null;
                                            if (nameObj != null)
                                            {
                                                var itemData = nameObj.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(nameObj);
                                                var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                                resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                            }
                                            var amountObj = rt.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ritem);
                                            int amt = amountObj != null ? Convert.ToInt32(amountObj) : 0;
                                            if (!string.IsNullOrEmpty(resName))
                                            {
                                                if (baseResources.ContainsKey(resName)) baseResources[resName] += amt;
                                                else baseResources[resName] = amt;
                                            }
                                        }
                                        catch { }
                                    }
                                }

                                var guiMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                foreach (var g in guiReqs) guiMap[g.name] = guiMap.ContainsKey(g.name) ? guiMap[g.name] + g.amount : g.amount;

                                try
                                {
                                    var baseResourcesLocal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                    var resourcesFieldBaseLocal = selectedRecipe.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    var baseResourcesEnumLocal = resourcesFieldBaseLocal?.GetValue(selectedRecipe) as IEnumerable;
                                    if (baseResourcesEnumLocal != null)
                                    {
                                        foreach (var ritem in baseResourcesEnumLocal)
                                        {
                                            try
                                            {
                                                var rt = ritem.GetType();
                                                var nameObj = rt.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ritem);
                                                string resName = null;
                                                if (nameObj != null)
                                                {
                                                    var itemData = nameObj.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(nameObj);
                                                    var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                                    resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                                }
                                                var amountObj = rt.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ritem);
                                                int amt = amountObj != null ? Convert.ToInt32(amountObj) : 0;
                                                if (!string.IsNullOrEmpty(resName))
                                                {
                                                    if (baseResourcesLocal.ContainsKey(resName)) baseResourcesLocal[resName] += amt;
                                                    else baseResourcesLocal[resName] = amt;
                                                }
                                            }
                                            catch { }
                                        }
                                    }

                                    var guiMapLocal = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var g in guiReqs) guiMapLocal[g.name] = guiMapLocal.ContainsKey(g.name) ? guiMapLocal[g.name] + g.amount : g.amount;

                                    bool hasGuiMismatch = false;
                                    if (guiMapLocal.Count != baseResourcesLocal.Count) hasGuiMismatch = true;
                                    else
                                    {
                                        foreach (var kv in guiMapLocal)
                                        {
                                            if (!baseResourcesLocal.TryGetValue(kv.Key, out var baseAmt) || baseAmt != kv.Value)
                                            {
                                                hasGuiMismatch = true;
                                                break;
                                            }
                                        }
                                    }

                                    // detect if mismatch should be considered upgrade: require at least one of these:
                                    // - explicit craft-upgrade control in GUI (m_craftUpgrade > 1)
                                    // - selected inventory item with lower quality (we treat that as candidate upgrade)
                                    // - recipe consumes result (special recipes that consume result item)
                                    bool explicitCraftUpgrade = false;
                                    try
                                    {
                                        var craftUpgradeFieldLocal = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                                        if (craftUpgradeFieldLocal != null)
                                        {
                                            var cvObj = craftUpgradeFieldLocal.GetValue(__instance);
                                            if (cvObj is int v && v > 1) explicitCraftUpgrade = true;
                                        }
                                    }
                                    catch { explicitCraftUpgrade = false; }

                                    bool selectedInvItemLowerQuality = false;
                                    try
                                    {
                                        var selInvItem = GetSelectedInventoryItem(__instance);
                                        var finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                        if (selInvItem != null && selInvItem.m_shared != null && !string.IsNullOrEmpty(selectedRecipe.m_item?.m_itemData?.m_shared?.m_name))
                                        {
                                            if (selInvItem.m_shared.m_name == selectedRecipe.m_item.m_itemData.m_shared.m_name && selInvItem.m_quality < finalQuality)
                                                selectedInvItemLowerQuality = true;
                                        }
                                    }
                                    catch { selectedInvItemLowerQuality = false; }

                                    bool consumesResult = false;
                                    try { if (RecipeConsumesResult(selectedRecipe)) consumesResult = true; } catch { consumesResult = false; }

                                    if (hasGuiMismatch && (explicitCraftUpgrade || selectedInvItemLowerQuality || consumesResult))
                                    {
                                        guiIndicatesUpgrade = true;
                                    }
                                }
                                catch { /* ignore */ }
                            }
                            catch { /* ignore */ }

                            if (guiIndicatesUpgrade)
                            {
                                int levelsToUpgrade = 1;
                                try
                                {
                                    Recipe recipeForQuality = _upgradeGuiRecipe ?? FindBestUpgradeRecipeCandidate(selectedRecipe) ?? selectedRecipe;
                                    if (_upgradeTargetItem != null)
                                    {
                                        var finalQ = recipeForQuality?.m_item?.m_itemData?.m_quality ?? 0;
                                        levelsToUpgrade = Math.Max(1, finalQ - _upgradeTargetItem.m_quality);
                                    }
                                    else
                                    {
                                        var craftUpgradeFieldLocal = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                                        if (craftUpgradeFieldLocal != null)
                                        {
                                            var cv = craftUpgradeFieldLocal.GetValue(__instance);
                                            if (cv is int v && v > 0) levelsToUpgrade = v;
                                        }
                                    }
                                }
                                catch { levelsToUpgrade = 1; }

                                var normalized = new List<(string name, int amount)>();
                                var dbCandidate = FindBestUpgradeRecipeCandidate(selectedRecipe);
                                foreach (var g in guiReqs)
                                {
                                    int amt = g.amount;
                                    int perLevel = amt;
                                    if (levelsToUpgrade > 1 && amt > 0 && (amt % levelsToUpgrade) == 0)
                                    {
                                        perLevel = amt / levelsToUpgrade;
                                    }
                                    else if (dbCandidate != null)
                                    {
                                        try
                                        {
                                            var resourcesField2 = dbCandidate.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            var candidateResources = resourcesField2?.GetValue(dbCandidate) as IEnumerable;
                                            if (candidateResources != null)
                                            {
                                                foreach (var req in candidateResources)
                                                {
                                                    try
                                                    {
                                                        var et = req.GetType();
                                                        var nameObj = et.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                                        string resName = null;
                                                        if (nameObj != null)
                                                        {
                                                            var itemData = nameObj.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(nameObj);
                                                            var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                                            resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                                        }
                                                        if (string.IsNullOrEmpty(resName)) continue;
                                                        if (!string.Equals(resName, g.name, StringComparison.OrdinalIgnoreCase)) continue;

                                                        var perLevelObj = et.GetField("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                                        if (perLevelObj != null)
                                                        {
                                                            int dbPerLevel = Convert.ToInt32(perLevelObj);
                                                            if (dbPerLevel > 0) perLevel = dbPerLevel;
                                                        }
                                                        else
                                                        {
                                                            var dbAmtObj = et.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                                            int dbAmt = dbAmtObj != null ? Convert.ToInt32(dbAmtObj) : 0;
                                                            if (dbAmt > 0)
                                                            {
                                                                if (levelsToUpgrade > 1 && (dbAmt % levelsToUpgrade) == 0) perLevel = dbAmt / levelsToUpgrade;
                                                                else if (dbAmt < amt) perLevel = dbAmt;
                                                            }
                                                        }
                                                        break;
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                    normalized.Add((g.name, perLevel));
                                }

                                _isUpgradeDetected = true;
                                _upgradeGuiRequirements = normalized;
                                var dbgJoined = string.Join(", ", _upgradeGuiRequirements.Select(x => x.name + ":" + x.amount));
                                LogInfo("Prefix-DBG: GUI requirement list indicates UPGRADE -> " + dbgJoined);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Prefix try-get-reqs exception: {ex}");
                    }

                    // If explicit craft multiplier present, treat as upgrade and skip suppression
                    try
                    {
                        var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (craftUpgradeField != null)
                        {
                            var cvObj = craftUpgradeField.GetValue(__instance);
                            if (cvObj is int v && v > 1)
                            {
                                _savedRecipeForCall = null;
                                _isUpgradeDetected = true;
                                _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance);
                                _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                                _upgradeTargetItem = GetSelectedInventoryItem(__instance);
                                return;
                            }
                        }
                    }
                    catch { }

                    // Build resourceList and snapshot existing inventory items of the result type
                    try
                    {
                        var resourcesField = selectedRecipe.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (resourcesField == null) return;
                        var resourcesEnumerable = resourcesField.GetValue(selectedRecipe) as IEnumerable;
                        if (resourcesEnumerable == null) return;
                        var resourceList = resourcesEnumerable.Cast<object>().ToList();

                        object GetMember(object obj, string name)
                        {
                            if (obj == null) return null;
                            var t = obj.GetType();
                            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (f != null) return f.GetValue(obj);
                            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (p != null) return p.GetValue(obj);
                            return null;
                        }

                        int ToInt(object v)
                        {
                            if (v == null) return 0;
                            try { return Convert.ToInt32(v); } catch { return 0; }
                        }

                        try
                        {
                            var craftedName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                            var localPlayer = Player.m_localPlayer;
                            if (!string.IsNullOrEmpty(craftedName) && localPlayer != null)
                            {
                                var inv = localPlayer.GetInventory();
                                if (inv != null)
                                {
                                    var existing = new List<ItemDrop.ItemData>();
                                    var existingData = new Dictionary<ItemDrop.ItemData, (int quality, int variant)>();
                                    foreach (var it in inv.GetAllItems())
                                    {
                                        if (it == null || it.m_shared == null) continue;
                                        if (it.m_shared.m_name == craftedName)
                                        {
                                            if (!existing.Contains(it)) existing.Add(it);
                                            existingData[it] = (it.m_quality, it.m_variant);
                                            LogInfo($"Prefix snapshot: found existing {ItemInfo(it)}");
                                        }
                                    }
                                    lock (typeof(ChanceCraftPlugin))
                                    {
                                        _preCraftSnapshot = existing;
                                        _preCraftSnapshotData = existingData;
                                        _snapshotRecipe = selectedRecipe;
                                        try
                                        {
                                            _preCraftSnapshotHashQuality = new Dictionary<int, int>();
                                            foreach (var it2 in existing)
                                            {
                                                if (it2 == null) continue;
                                                _preCraftSnapshotHashQuality[RuntimeHelpers.GetHashCode(it2)] = it2.m_quality;
                                            }
                                        }
                                        catch { _preCraftSnapshotHashQuality = null; }
                                    }
                                }
                            }
                        }
                        catch { }

                        // inventory-based upgrade detection: selected inventory item vs recipe final quality
                        try
                        {
                            var craftedName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                            int craftedQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                            var selectedInventoryItem = GetSelectedInventoryItem(__instance);

                            if (selectedInventoryItem != null &&
                                selectedInventoryItem.m_shared != null &&
                                !string.IsNullOrEmpty(craftedName) &&
                                selectedInventoryItem.m_shared.m_name == craftedName &&
                                selectedInventoryItem.m_quality < craftedQuality)
                            {
                                _isUpgradeDetected = true;
                                _upgradeTargetItem = selectedInventoryItem;
                                _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                                _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                                _savedRecipeForCall = null;
                                return;
                            }

                            if (!string.IsNullOrEmpty(craftedName) && craftedQuality > 0 && Player.m_localPlayer != null)
                            {
                                var inv = Player.m_localPlayer.GetInventory();
                                if (inv != null)
                                {
                                    ItemDrop.ItemData foundLower = null;
                                    foreach (var it in inv.GetAllItems())
                                    {
                                        if (it == null || it.m_shared == null) continue;
                                        if (it.m_shared.m_name == craftedName && it.m_quality < craftedQuality)
                                        {
                                            foundLower = it;
                                            break;
                                        }
                                    }
                                    if (foundLower != null)
                                    {
                                        _isUpgradeDetected = true;
                                        _upgradeTargetItem = foundLower;
                                        _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                                        _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                                        _savedRecipeForCall = null;
                                        return;
                                    }
                                }
                            }
                        }
                        catch { }

                        // Recipe-consumes-result check -> treat as upgrade and skip suppression
                        try
                        {
                            if (RecipeConsumesResult(selectedRecipe))
                            {
                                _isUpgradeDetected = true;
                                _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                                _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                                _savedRecipeForCall = null;
                                return;
                            }
                        }
                        catch { }

                        // Build validReqs and only suppress multi-resource recipes
                        var validReqs = new List<object>();
                        foreach (var req in resourceList)
                        {
                            var nameObj = GetMember(req, "m_resItem");
                            if (nameObj == null) continue;
                            string rname = null;
                            try
                            {
                                var itemData = nameObj.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(nameObj);
                                var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                rname = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                            }
                            catch { }
                            int ramount = ToInt(GetMember(req, "m_amount"));
                            if (string.IsNullOrEmpty(rname) || ramount <= 0) continue;
                            validReqs.Add(req);
                        }

                        if (validReqs.Count <= 1) return;

                        var itemType = selectedRecipe.m_item?.m_itemData?.m_shared?.m_itemType;
                        bool isEligible =
                            itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                            itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                            itemType == ItemDrop.ItemData.ItemType.Bow ||
                            itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                            itemType == ItemDrop.ItemData.ItemType.Shield ||
                            itemType == ItemDrop.ItemData.ItemType.Helmet ||
                            itemType == ItemDrop.ItemData.ItemType.Chest ||
                            itemType == ItemDrop.ItemData.ItemType.Legs ||
                            itemType == ItemDrop.ItemData.ItemType.Ammo;

                        if (!isEligible) return;

                        lock (_savedResourcesLock)
                        {
                            var saveKey = GetRecipeKey(selectedRecipe);
                            if (!string.IsNullOrEmpty(saveKey))
                            {
                                lock (_savedResourcesLock)
                                {
                                    if (!_savedResources.ContainsKey(saveKey))
                                    {
                                        _savedResources[saveKey] = resourcesEnumerable; // originalResourcesObject
                                    }
                                }
                            }
                        }

                        try
                        {
                            var key = RecipeFingerprint(selectedRecipe);
                            lock (_suppressedRecipeKeysLock)
                            {
                                if (!_suppressedRecipeKeys.Contains(key)) _suppressedRecipeKeys.Add(key);
                            }
                        }
                        catch { }

                        // Suppress live recipe resources by setting empty array/list
                        try
                        {
                            Type fieldType = resourcesField.FieldType;
                            object empty = null;
                            if (fieldType.IsArray) empty = Array.CreateInstance(fieldType.GetElementType(), 0);
                            else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
                                empty = Activator.CreateInstance(fieldType);

                            if (empty != null)
                            {
                                resourcesField.SetValue(selectedRecipe, empty);
                                _suppressedThisCall = true;
                                IsDoCraft = true;
                            }
                        }
                        catch { }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Prefix snapshot/build exception: {ex}");
                    }
                }
                catch (Exception ex)
                {
                    LogWarning($"Prefix outer exception: {ex}");
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;
                }
            }

            static void Postfix(InventoryGui __instance, Player player)
            {
                try
                {
                    if (_savedRecipeForCall != null && _suppressedThisCall)
                    {
                        lock (_savedResourcesLock)
                        {
                            var key = GetRecipeKey(_savedRecipeForCall);
                            if (!string.IsNullOrEmpty(key) && _savedResources.TryGetValue(key, out var saved))
                            {
                                try
                                {
                                    var resourcesField = _savedRecipeForCall.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (resourcesField != null) resourcesField.SetValue(_savedRecipeForCall, saved);
                                }
                                catch { }
                                _savedResources.Remove(key);
                            }
                        }
                    }

                    if (!_suppressedThisCall)
                    {
                        _suppressedThisCall = false;
                        _savedRecipeForCall = null;
                        IsDoCraft = false;
                        return;
                    }

                    var recipeForLogic = _savedRecipeForCall;
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;

                    try
                    {
                        if (_isUpgradeDetected || IsUpgradeOperation(__instance, recipeForLogic))
                        {
                            if (_upgradeGuiRecipe == null) _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance);
                            if (_upgradeTargetItem == null) _upgradeTargetItem = GetSelectedInventoryItem(__instance);

                            var resultFromTry = TrySpawnCraftEffect(__instance, recipeForLogic, true);
                            if (resultFromTry == null)
                            {
                                try
                                {
                                    if (recipeForLogic != null)
                                    {
                                        var key = RecipeFingerprint(recipeForLogic);
                                        lock (_suppressedRecipeKeysLock) { _suppressedRecipeKeys.Remove(key); }
                                    }
                                    if (_upgradeGuiRecipe != null)
                                    {
                                        var keyg = RecipeFingerprint(_upgradeGuiRecipe);
                                        lock (_suppressedRecipeKeysLock) { _suppressedRecipeKeys.Remove(keyg); }
                                    }
                                }
                                catch { }

                                lock (typeof(ChanceCraftPlugin))
                                {
                                    _preCraftSnapshot = null;
                                    _preCraftSnapshotData = null;
                                    _snapshotRecipe = null;
                                }
                                _upgradeTargetItem = null;
                                _upgradeRecipe = null;
                                _upgradeGuiRecipe = null;
                                _isUpgradeDetected = false;
                                return;
                            }
                        }

                        Recipe recept = TrySpawnCraftEffect(__instance, recipeForLogic, false);

                        if (player != null && recept != null)
                        {
                            try
                            {
                                List<ItemDrop.ItemData> beforeSet = null;
                                lock (typeof(ChanceCraftPlugin))
                                {
                                    if (_snapshotRecipe != null && _snapshotRecipe == recept) beforeSet = _preCraftSnapshot;
                                    _preCraftSnapshot = null;
                                    _preCraftSnapshotData = null;
                                    _snapshotRecipe = null;
                                }

                                int toRemoveCount = recept.m_amount > 0 ? recept.m_amount : 1;
                                var invItems = player.GetInventory()?.GetAllItems();
                                if (invItems != null)
                                {
                                    int removedTotal = 0;
                                    for (int i = invItems.Count - 1; i >= 0 && toRemoveCount > 0; i--)
                                    {
                                        var item = invItems[i];
                                        if (item == null || item.m_shared == null) continue;
                                        var craftedName = recept.m_item?.m_itemData?.m_shared?.m_name;
                                        var craftedQuality = recept.m_item?.m_itemData?.m_quality ?? 0;
                                        var craftedVariant = recept.m_item?.m_itemData?.m_variant ?? 0;

                                        if (item.m_shared.m_name == craftedName && item.m_quality == craftedQuality && item.m_variant == craftedVariant)
                                        {
                                            if (item == _upgradeTargetItem) continue;
                                            if (beforeSet == null || !beforeSet.Contains(item))
                                            {
                                                int remove = Math.Min(item.m_stack, toRemoveCount);
                                                item.m_stack -= remove;
                                                toRemoveCount -= remove;
                                                removedTotal += remove;
                                                if (item.m_stack <= 0) player.GetInventory().RemoveItem(item);
                                            }
                                        }
                                    }

                                    if (toRemoveCount > 0)
                                    {
                                        if (beforeSet != null && removedTotal == 0)
                                        {
                                            // nothing to remove (all were pre-existing) -> skip fallback
                                        }
                                        else
                                        {
                                            for (int i = invItems.Count - 1; i >= 0 && toRemoveCount > 0; i--)
                                            {
                                                var item = invItems[i];
                                                if (item == null || item.m_shared == null) continue;
                                                var craftedName = recept.m_item?.m_itemData?.m_shared?.m_name;
                                                var craftedQuality = recept.m_item?.m_itemData?.m_quality ?? 0;
                                                var craftedVariant = recept.m_item?.m_itemData?.m_variant ?? 0;

                                                if (item.m_shared.m_name == craftedName && item.m_quality == craftedQuality && item.m_variant == craftedVariant)
                                                {
                                                    if (item == _upgradeTargetItem) continue;
                                                    int remove = Math.Min(item.m_stack, toRemoveCount);
                                                    item.m_stack -= remove;
                                                    toRemoveCount -= remove;
                                                    if (item.m_stack <= 0) player.GetInventory().RemoveItem(item);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                LogWarning($"Postfix removal exception: {ex}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Postfix logic exception: {ex}");
                    }
                }
                catch (Exception ex)
                {
                    LogWarning($"Postfix outer exception: {ex}");
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;
                }
            }
        }

        #endregion

        // Add this helper near other static helpers/fields in ChanceCraft.cs
        private static void ClearCapturedUpgradeGui()
        {
            try
            {
                _upgradeGuiRecipe = null;
                _upgradeGuiRequirements = null;
                _upgradeRecipe = null;
                _upgradeTargetItem = null;
            }
            catch { }
        }

        #region TrySpawnCraftEffect and related logic

        // Full TrySpawnCraftEffect with calls to ClearCapturedUpgradeGui() at all non-upgrade / upgrade exit points.
        // Insert/replace this function in ChanceCraft.cs
        public static Recipe TrySpawnCraftEffect(InventoryGui gui, Recipe forcedRecipe = null, bool isUpgradeCall = false)
        {
            Recipe selectedRecipe = forcedRecipe;

            if (selectedRecipe == null)
            {
                var selectedRecipeField = typeof(InventoryGui).GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (selectedRecipeField != null)
                {
                    object value = selectedRecipeField.GetValue(gui);
                    if (value != null && value.GetType().Name == "RecipeDataPair")
                    {
                        var recipeProp = value.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (recipeProp != null)
                            selectedRecipe = recipeProp.GetValue(value) as Recipe;
                    }
                    else
                    {
                        selectedRecipe = value as Recipe;
                    }
                }
            }

            if (selectedRecipe == null || Player.m_localPlayer == null) return null;

            var itemType = selectedRecipe.m_item?.m_itemData?.m_shared?.m_itemType;
            if (itemType != ItemDrop.ItemData.ItemType.OneHandedWeapon &&
                itemType != ItemDrop.ItemData.ItemType.TwoHandedWeapon &&
                itemType != ItemDrop.ItemData.ItemType.Bow &&
                itemType != ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft &&
                itemType != ItemDrop.ItemData.ItemType.Shield &&
                itemType != ItemDrop.ItemData.ItemType.Helmet &&
                itemType != ItemDrop.ItemData.ItemType.Chest &&
                itemType != ItemDrop.ItemData.ItemType.Legs &&
                itemType != ItemDrop.ItemData.ItemType.Ammo)
            {
                return null;
            }

            var player = Player.m_localPlayer;

            float chance = 0.6f;
            if (itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.Bow ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft)
            {
                chance = weaponSuccessChance.Value;
            }
            else if (itemType == ItemDrop.ItemData.ItemType.Shield ||
                     itemType == ItemDrop.ItemData.ItemType.Helmet ||
                     itemType == ItemDrop.ItemData.ItemType.Chest ||
                     itemType == ItemDrop.ItemData.ItemType.Legs)
            {
                chance = armorSuccessChance.Value;
            }
            else if (itemType == ItemDrop.ItemData.ItemType.Ammo)
            {
                chance = arrowSuccessChance.Value;
            }

            int qualityLevel = selectedRecipe.m_item?.m_itemData?.m_quality ?? 1;
            float qualityScalePerLevel = 0.05f;
            float qualityFactor = 1f + qualityScalePerLevel * Math.Max(0, qualityLevel - 1);
            chance = Mathf.Clamp01(chance * qualityFactor);

            float randVal = UnityEngine.Random.value;

            // Use consistent, conservative helper for upgrade detection
            bool isUpgradeNow = ShouldTreatAsUpgrade(gui, selectedRecipe, isUpgradeCall);

            bool suppressedThisOperation = IsDoCraft;
            try
            {
                var key = RecipeFingerprint(selectedRecipe);
                lock (_suppressedRecipeKeysLock)
                {
                    if (_suppressedRecipeKeys.Contains(key)) suppressedThisOperation = true;
                }
            }
            catch { }

            try
            {
                lock (typeof(ChanceCraftPlugin))
                {
                    if (_snapshotRecipe != null && _snapshotRecipe == selectedRecipe && _preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                    {
                        suppressedThisOperation = true;
                    }
                }
            }
            catch { }

            // Diagnostic: show the roll, chance and flags
            try
            {
                var recipeKeyDbg = selectedRecipe != null ? RecipeFingerprint(selectedRecipe) : "null";
                LogInfo($"TrySpawnCraftEffect-DBG: recipe={recipeKeyDbg} itemType={itemType} quality={qualityLevel} chance={chance:F3} randVal={randVal:F3} isUpgradeNow={isUpgradeNow} suppressedThisOperation={suppressedThisOperation}");
            }
            catch { }

            // Detailed upgrade-detection diagnostics
            try
            {
                string ugGuiRecipeKey = _upgradeGuiRecipe != null ? RecipeFingerprint(_upgradeGuiRecipe) : "null";
                string ugRecipeKey = _upgradeRecipe != null ? RecipeFingerprint(_upgradeRecipe) : "null";
                string ugTargetHash = _upgradeTargetItem != null ? RuntimeHelpers.GetHashCode(_upgradeTargetItem).ToString("X") : "null";
                int ugGuiReqCount = _upgradeGuiRequirements != null ? _upgradeGuiRequirements.Count : 0;
                bool guiHasUpgradeRecipe = false;
                try { guiHasUpgradeRecipe = GetUpgradeRecipeFromGui(gui) != null; } catch { guiHasUpgradeRecipe = false; }
                LogInfo($"TSCE-DBG-DETAIL: isUpgradeCall={isUpgradeCall} _isUpgradeDetected={_isUpgradeDetected} IsUpgradeOperation={IsUpgradeOperation(gui, selectedRecipe)} guiHasUpgradeRecipe={guiHasUpgradeRecipe} _upgradeGuiRecipe={ugGuiRecipeKey} _upgradeRecipe={ugRecipeKey} _upgradeTargetItem={ugTargetHash} _upgradeGuiRequirementsCount={ugGuiReqCount}");
            }
            catch { }

            if (randVal <= chance)
            {
                // SUCCESS
                var craftingStationField = typeof(InventoryGui).GetField("currentCraftingStation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var craftingStation = craftingStationField?.GetValue(gui);
                if (craftingStation != null)
                {
                    var m_craftItemEffectsField = craftingStation.GetType().GetField("m_craftItemEffects", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var m_craftItemEffects = m_craftItemEffectsField?.GetValue(craftingStation);
                    if (m_craftItemEffects != null)
                    {
                        var createMethod = m_craftItemEffects.GetType().GetMethod("Create", new Type[] { typeof(Vector3), typeof(Quaternion) });
                        if (createMethod != null)
                        {
                            try { createMethod.Invoke(m_craftItemEffects, new object[] { player.transform.position, Quaternion.identity }); } catch { }
                        }
                    }
                }

                bool suppressedThisOperationLocal = suppressedThisOperation;
                try
                {
                    var key = RecipeFingerprint(selectedRecipe);
                    lock (_suppressedRecipeKeysLock)
                    {
                        if (_suppressedRecipeKeys.Contains(key)) suppressedThisOperationLocal = true;
                    }
                }
                catch { }

                try
                {
                    lock (typeof(ChanceCraftPlugin))
                    {
                        if (_snapshotRecipe != null && _snapshotRecipe == selectedRecipe && _preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                        {
                            suppressedThisOperationLocal = true;
                        }
                    }
                }
                catch { }

                if (isUpgradeNow)
                {
                    try
                    {
                        var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                        if (ReferenceEquals(recipeToUse, selectedRecipe))
                        {
                            var candidate = FindBestUpgradeRecipeCandidate(selectedRecipe);
                            if (candidate != null) recipeToUse = candidate;
                        }

                        if (_upgradeTargetItem == null) _upgradeTargetItem = GetSelectedInventoryItem(gui);

                        // Diagnostic: log which recipe and target will be used for upgrade-success removal
                        try
                        {
                            var recipeKey = recipeToUse != null ? RecipeFingerprint(recipeToUse) : "null";
                            var targetHash = _upgradeTargetItem != null ? RuntimeHelpers.GetHashCode(_upgradeTargetItem).ToString("X") : "null";
                            var snapshotCount = (_preCraftSnapshot != null) ? _preCraftSnapshot.Count : 0;
                            var snapshotDataCount = (_preCraftSnapshotData != null) ? _preCraftSnapshotData.Count : 0;
                            LogInfo($"TrySpawnCraftEffect-DBG SUCCESS UPGRADE: recipeToUse={recipeKey} targetHash={targetHash} preSnapshotCount={snapshotCount} preSnapshotData={snapshotDataCount}");
                        }
                        catch { }

                        RemoveRequiredResourcesUpgrade(gui, player, recipeToUse, _upgradeTargetItem, true);

                        lock (typeof(ChanceCraftPlugin))
                        {
                            // clear post-success snapshot state
                            _preCraftSnapshot = null;
                            _preCraftSnapshotData = null;
                            _snapshotRecipe = null;
                            _upgradeTargetItemIndex = -1;
                            _preCraftSnapshotHashQuality = null;
                        }

                        // Clear GUI-captured upgrade state to avoid it influencing subsequent crafts.
                        ClearCapturedUpgradeGui();
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"TrySpawnCraftEffect success/upgrade removal exception: {ex}");
                    }

                    return null;
                }
                else
                {
                    if (suppressedThisOperationLocal)
                    {
                        try
                        {
                            // Decide consistently using the helper whether this suppressed-success should be treated as an upgrade.
                            bool treatAsUpgrade = ShouldTreatAsUpgrade(gui, selectedRecipe, false);
                            if (treatAsUpgrade)
                            {
                                var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                                if (ReferenceEquals(recipeToUse, selectedRecipe))
                                {
                                    var candidate = FindBestUpgradeRecipeCandidate(selectedRecipe);
                                    if (candidate != null) recipeToUse = candidate;
                                }

                                var targetItem = _upgradeTargetItem ?? GetSelectedInventoryItem(gui);
                                var targetHashLog = targetItem != null ? RuntimeHelpers.GetHashCode(targetItem).ToString("X") : "null";
                                LogInfo($"TrySpawnCraftEffect-DBG suppressed-success treating as UPGRADE: recipe={RecipeFingerprint(recipeToUse)} target={targetHashLog}");

                                RemoveRequiredResourcesUpgrade(gui, player, recipeToUse, targetItem, true);
                            }
                            else
                            {
                                RemoveRequiredResources(gui, player, selectedRecipe, true, false);
                            }
                        }
                        catch (Exception ex) { LogWarning($"TrySpawnCraftEffect success removal exception: {ex}"); }

                        // Clear GUI-captured upgrade state after this branch so stale GUI data won't persist.
                        ClearCapturedUpgradeGui();
                    }
                    return null;
                }
            }
            else
            {
                // FAILURE
                if (isUpgradeCall || IsUpgradeOperation(gui, selectedRecipe) || _isUpgradeDetected || isUpgradeNow)
                {
                    try
                    {
                        // Diagnostic: show failure + pre-snapshot state
                        try
                        {
                            var recipeKey = selectedRecipe != null ? RecipeFingerprint(selectedRecipe) : "null";
                            int preSnapshotEntries = _preCraftSnapshotData != null ? _preCraftSnapshotData.Count : 0;
                            LogInfo($"TrySpawnCraftEffect-DBG FAILURE UPGRADE: recipe={recipeKey} preSnapshotData={preSnapshotEntries} upgradeTargetIndex={_upgradeTargetItemIndex}");
                        }
                        catch { }

                        // Attempt revert operations then remove upgrade resources while preserving target item
                        bool didRevertAny = false;
                        lock (typeof(ChanceCraftPlugin))
                        {
                            if (_preCraftSnapshotData != null)
                            {
                                var invItems = Player.m_localPlayer?.GetInventory()?.GetAllItems();
                                var preRefs = _preCraftSnapshot;
                                foreach (var kv in _preCraftSnapshotData)
                                {
                                    var originalRef = kv.Key;
                                    var pre = kv.Value;
                                    try
                                    {
                                        if (originalRef != null && invItems != null && invItems.Contains(originalRef))
                                        {
                                            if (TryUnpackQualityVariant(pre, out int pq, out int pv))
                                            {
                                                if (originalRef.m_quality != pq || originalRef.m_variant != pv)
                                                {
                                                    originalRef.m_quality = pq;
                                                    originalRef.m_variant = pv;
                                                    didRevertAny = true;
                                                }
                                            }
                                            continue;
                                        }

                                        string expectedName = originalRef?.m_shared?.m_name ?? selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                                        if (string.IsNullOrEmpty(expectedName) || invItems == null) continue;

                                        foreach (var cur in invItems)
                                        {
                                            if (cur == null || cur.m_shared == null) continue;
                                            if (!string.Equals(cur.m_shared.m_name, expectedName, StringComparison.OrdinalIgnoreCase)) continue;

                                            if (TryUnpackQualityVariant(pre, out int pq2, out int pv2))
                                            {
                                                if (cur.m_quality <= pq2) continue;
                                                if (preRefs != null && preRefs.Contains(cur)) continue;
                                                cur.m_quality = pq2;
                                                cur.m_variant = pv2;
                                                didRevertAny = true;
                                                break;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }

                            if (!didRevertAny)
                            {
                                try
                                {
                                    string resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                                    int expectedPreQuality = Math.Max(0, (selectedRecipe.m_item?.m_itemData?.m_quality ?? 0) - 1);

                                    if (_preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                                    {
                                        var preQs = _preCraftSnapshotData.Values.Select(v => { if (TryUnpackQualityVariant(v, out int a, out int b)) return a; return 0; }).ToList();
                                        if (preQs.Count > 0) expectedPreQuality = Math.Max(0, preQs.Max());
                                    }

                                    if (!string.IsNullOrEmpty(resultName) && Player.m_localPlayer != null)
                                    {
                                        var invItems2 = Player.m_localPlayer.GetInventory()?.GetAllItems();
                                        if (invItems2 != null)
                                        {
                                            foreach (var it in invItems2)
                                            {
                                                if (it == null || it.m_shared == null) continue;
                                                if (!string.Equals(it.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase)) continue;
                                                if (it.m_quality <= expectedPreQuality) continue;
                                                if (_preCraftSnapshot != null && _preCraftSnapshot.Contains(it)) continue;
                                                it.m_quality = expectedPreQuality;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (!didRevertAny && _upgradeTargetItemIndex >= 0)
                            {
                                try
                                {
                                    var inv = Player.m_localPlayer?.GetInventory();
                                    var all = inv?.GetAllItems();
                                    if (all != null && _upgradeTargetItemIndex >= 0 && _upgradeTargetItemIndex < all.Count)
                                    {
                                        var candidate = all[_upgradeTargetItemIndex];
                                        if (candidate != null && candidate.m_shared != null)
                                        {
                                            string resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                                            int finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                            int expectedPreQuality = Math.Max(0, finalQuality - 1);

                                            if (_preCraftSnapshotData != null)
                                            {
                                                var kv = _preCraftSnapshotData.FirstOrDefault(p => p.Key != null && p.Key.m_shared != null && string.Equals(p.Key.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase));
                                                if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && TryUnpackQualityVariant(kv.Value, out int pq3, out int pv3))
                                                    expectedPreQuality = pq3;
                                            }

                                            if (string.Equals(candidate.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase) && candidate.m_quality > expectedPreQuality)
                                            {
                                                candidate.m_quality = expectedPreQuality;
                                                didRevertAny = true;
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (!didRevertAny && _preCraftSnapshotHashQuality != null && _preCraftSnapshotHashQuality.Count > 0)
                            {
                                try
                                {
                                    string resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                                    int finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                    int expectedPreQuality = Math.Max(0, finalQuality - 1);

                                    var invItems3 = Player.m_localPlayer?.GetInventory()?.GetAllItems();
                                    if (invItems3 != null)
                                    {
                                        foreach (var it in invItems3)
                                        {
                                            if (it == null || it.m_shared == null) continue;
                                            int h = RuntimeHelpers.GetHashCode(it);

                                            if (_preCraftSnapshotHashQuality.TryGetValue(h, out int prevQ))
                                            {
                                                if (it.m_quality > prevQ)
                                                {
                                                    it.m_quality = prevQ;
                                                    var kv = _preCraftSnapshotData.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == h);
                                                    if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && TryUnpackQualityVariant(kv.Value, out int pq4, out int pv4))
                                                        it.m_variant = pv4;
                                                    didRevertAny = true;
                                                }
                                            }
                                            else
                                            {
                                                if (!string.IsNullOrEmpty(resultName) &&
                                                    string.Equals(it.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase) &&
                                                    it.m_quality > expectedPreQuality)
                                                {
                                                    it.m_quality = expectedPreQuality;
                                                    didRevertAny = true;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch { }

                            }

                            // --- FORCED/EXTRA REVERT PASS (failsafe) ---
                            try
                            {
                                string resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                                int finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                int expectedPreQuality = Math.Max(0, finalQuality - 1);

                                try
                                {
                                    if (_preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                                    {
                                        var preQs = _preCraftSnapshotData.Values.Select(v => { if (TryUnpackQualityVariant(v, out int a, out int b)) return a; return 0; }).ToList();
                                        if (preQs.Count > 0) expectedPreQuality = Math.Max(0, preQs.Max());
                                    }
                                }
                                catch { }

                                try { LogInfo($"TrySpawnCraftEffect-DBG RevertCheck: resultName={resultName} finalQuality={finalQuality} expectedPreQuality={expectedPreQuality} preSnapshotCount={(_preCraftSnapshot != null ? _preCraftSnapshot.Count : 0)} preSnapshotDataCount={(_preCraftSnapshotData != null ? _preCraftSnapshotData.Count : 0)} preHashCount={(_preCraftSnapshotHashQuality != null ? _preCraftSnapshotHashQuality.Count : 0)}"); } catch { }

                                var inv = Player.m_localPlayer?.GetInventory();
                                var all = inv?.GetAllItems();
                                if (!string.IsNullOrEmpty(resultName) && all != null)
                                {
                                    foreach (var it in all)
                                    {
                                        try
                                        {
                                            if (it == null || it.m_shared == null) continue;
                                            if (!string.Equals(it.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase)) continue;
                                            bool wasPre = _preCraftSnapshot != null && _preCraftSnapshot.Contains(it);
                                            int curQ = it.m_quality;
                                            if (curQ > expectedPreQuality && !wasPre)
                                            {
                                                try { LogInfo($"TrySpawnCraftEffect-DBG FORCED REVERT: itemHash={RuntimeHelpers.GetHashCode(it):X} name={it.m_shared.m_name} oldQ={it.m_quality} -> newQ={expectedPreQuality}"); } catch { }
                                                it.m_quality = expectedPreQuality;
                                                try
                                                {
                                                    var kv = _preCraftSnapshotData?.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == RuntimeHelpers.GetHashCode(it));
                                                    if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && TryUnpackQualityVariant(kv.Value, out int pq5, out int pv5))
                                                    {
                                                        it.m_variant = pv5;
                                                        LogInfo($"TrySpawnCraftEffect-DBG FORCED REVERT: restored variant={it.m_variant} for itemHash={RuntimeHelpers.GetHashCode(it):X}");
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }

                            // perform UI refresh after revert attempts (keep snapshots intact until after final defensive revert and resource removal)
                            try
                            {
                                ForceSimulateTabSwitchRefresh(gui);
                                try { RefreshInventoryGui(gui); } catch { }
                                try { RefreshCraftingPanel(gui); } catch { }
                                try { gui?.StartCoroutine(DelayedRefreshCraftingPanel(gui, 1)); } catch { }
                                UnityEngine.Debug.LogWarning("[ChanceCraft] TrySpawnCraftEffect: performed UI refresh after revert attempts.");
                            }
                            catch (Exception exRefresh)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: UI refresh after revert failed: {exRefresh}");
                            }

                            try { Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=red>Upgrade failed!</color>"); } catch { }
                        }
                        // end lock

                        // Diagnostic: inventory snapshot BEFORE removal
                        try
                        {
                            var target = _upgradeTargetItem ?? GetSelectedInventoryItem(gui);
                            var targetHash = target != null ? RuntimeHelpers.GetHashCode(target).ToString("X") : "null";
                            var inv = Player.m_localPlayer?.GetInventory();
                            int woodBefore = 0, scrapBefore = 0, hideBefore = 0;
                            if (inv != null)
                            {
                                var all = inv.GetAllItems();
                                woodBefore = all.Where(it => it != null && it.m_shared != null && string.Equals(it.m_shared.m_name, "$item_wood", StringComparison.OrdinalIgnoreCase)).Sum(it => it.m_stack);
                                scrapBefore = all.Where(it => it != null && it.m_shared != null && string.Equals(it.m_shared.m_name, "$item_leatherscraps", StringComparison.OrdinalIgnoreCase)).Sum(it => it.m_stack);
                                hideBefore = all.Where(it => it != null && it.m_shared != null && string.Equals(it.m_shared.m_name, "$item_deerhide", StringComparison.OrdinalIgnoreCase)).Sum(it => it.m_stack);
                            }
                            LogInfo($"TrySpawnCraftEffect-DBG BEFORE removal: targetHash={targetHash} wood={woodBefore} scraps={scrapBefore} hides={hideBefore} didRevertAny={didRevertAny}");
                        }
                        catch { }

                        var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                        if (ReferenceEquals(recipeToUse, selectedRecipe))
                        {
                            var candidate = FindBestUpgradeRecipeCandidate(selectedRecipe);
                            if (candidate != null) recipeToUse = candidate;
                        }

                        var upgradeTarget = _upgradeTargetItem ?? GetSelectedInventoryItem(gui);
                        RemoveRequiredResourcesUpgrade(gui, Player.m_localPlayer, recipeToUse, upgradeTarget, false);

                        // Defensive: Force revert after removal (revert only the selected/identified target when possible)
                        try { ForceRevertAfterRemoval(gui, recipeToUse, upgradeTarget); } catch { }

                        // Now clear snapshots and other state AFTER we used snapshot data for revert
                        lock (typeof(ChanceCraftPlugin))
                        {
                            _preCraftSnapshot = null;
                            _preCraftSnapshotData = null;
                            _snapshotRecipe = null;
                            _upgradeTargetItemIndex = -1;
                            _preCraftSnapshotHashQuality = null;
                        }

                        // Clear GUI-captured upgrade state after performing upgrade-failure removal
                        ClearCapturedUpgradeGui();
                    }
                    catch { }

                    return null;
                }
                else
                {
                    // Non-upgrade failure path
                    try
                    {
                        bool gameAlreadyHandledNormal = false;
                        lock (typeof(ChanceCraftPlugin))
                        {
                            if (_snapshotRecipe != null && _snapshotRecipe == selectedRecipe && _preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                            {
                                foreach (var kv in _preCraftSnapshotData)
                                {
                                    var item = kv.Key;
                                    var pre = kv.Value;
                                    if (item == null || item.m_shared == null) continue;
                                    if (TryUnpackQualityVariant(pre, out int pq6, out int pv6))
                                    {
                                        int currentQuality = item.m_quality;
                                        int currentVariant = item.m_variant;
                                        if (currentQuality > pq6 && currentVariant == pv6)
                                        {
                                            gameAlreadyHandledNormal = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        if (gameAlreadyHandledNormal)
                        {
                            lock (typeof(ChanceCraftPlugin))
                            {
                                _preCraftSnapshot = null;
                                _preCraftSnapshotData = null;
                                _snapshotRecipe = null;
                                _upgradeTargetItemIndex = -1;
                                _preCraftSnapshotHashQuality = null;
                            }

                            // clear GUI-captured upgrade state as we are not treating this as upgrade
                            ClearCapturedUpgradeGui();
                            return null;
                        }
                    }
                    catch { }

                    try { RemoveRequiredResources(gui, Player.m_localPlayer, selectedRecipe, false, false); } catch { }
                    try { Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=red>Crafting failed!</color>"); } catch { }

                    // clear GUI-captured upgrade state after non-upgrade failure
                    ClearCapturedUpgradeGui();
                    return selectedRecipe;
                }
            }
        }
        #endregion

        // Robust check whether this invocation should be treated as an UPGRADE operation
        private static bool ShouldTreatAsUpgrade(InventoryGui gui, Recipe selectedRecipe, bool isUpgradeCall)
        {
            try
            {
                if (isUpgradeCall) return true;
                if (_isUpgradeDetected) return true;
                if (IsUpgradeOperation(gui, selectedRecipe)) return true;

                // If GUI exposes a live upgrade recipe and the GUI indicates upgrade mode, that's a good sign
                bool guiHasUpgradeRecipe = false;
                try { guiHasUpgradeRecipe = GetUpgradeRecipeFromGui(gui) != null; } catch { guiHasUpgradeRecipe = false; }

                // Determine the candidate target item (captured or currently selected)
                ItemDrop.ItemData target = null;
                try { target = _upgradeTargetItem ?? GetSelectedInventoryItem(gui); } catch { target = _upgradeTargetItem; }

                // If we have no target, don't treat as upgrade (conservative)
                if (target == null) return false;

                // Get recipe result name and target name safely
                string recipeResultName = null;
                try { recipeResultName = selectedRecipe?.m_item?.m_itemData?.m_shared?.m_name; } catch { recipeResultName = null; }
                string targetName = null;
                try { targetName = target?.m_shared?.m_name; } catch { targetName = null; }

                // If names match and target quality is less than recipe final quality, it's an upgrade
                if (!string.IsNullOrEmpty(recipeResultName) && !string.IsNullOrEmpty(targetName) &&
                    string.Equals(recipeResultName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    int finalQ = selectedRecipe?.m_item?.m_itemData?.m_quality ?? 0;
                    if (target.m_quality < finalQ) return true;
                }

                // If we captured a GUI upgrade recipe previously and it matches the selectedRecipe, treat as upgrade only if a target exists
                if (_upgradeGuiRecipe != null && selectedRecipe != null)
                {
                    try
                    {
                        if (RecipeFingerprint(_upgradeGuiRecipe) == RecipeFingerprint(selectedRecipe) && target != null) return true;
                    }
                    catch { }
                }

                // Otherwise conservative: not an upgrade
                return false;
            }
            catch { return false; }
        }

        // Helper: unpack a tuple-like value of unknown shape into two ints (quality, variant).
        // Tries common shapes (ValueTuple Item1/Item2, named tuple properties quality/variant, fields),
        // and finally falls back to a lightweight manual integer parser from ToString() — avoids Regex/extra refs.
        private static bool TryUnpackQualityVariant(object tupleValue, out int quality, out int variant)
        {
            quality = 0;
            variant = 0;
            if (tupleValue == null) return false;

            var t = tupleValue.GetType();

            // 1) ValueTuple<T1,T2> via properties Item1/Item2
            var pItem1 = t.GetProperty("Item1");
            var pItem2 = t.GetProperty("Item2");
            if (pItem1 != null && pItem2 != null)
            {
                try
                {
                    quality = Convert.ToInt32(pItem1.GetValue(tupleValue));
                    variant = Convert.ToInt32(pItem2.GetValue(tupleValue));
                    return true;
                }
                catch { /* fallthrough */ }
            }

            // 2) Named-tuple compiled to properties (e.g. quality / variant)
            var pQuality = t.GetProperty("quality");
            var pVariant = t.GetProperty("variant");
            if (pQuality != null && pVariant != null)
            {
                try
                {
                    quality = Convert.ToInt32(pQuality.GetValue(tupleValue));
                    variant = Convert.ToInt32(pVariant.GetValue(tupleValue));
                    return true;
                }
                catch { /* fallthrough */ }
            }

            // 3) Fields fallback (Item1/Item2)
            var fItem1 = t.GetField("Item1");
            var fItem2 = t.GetField("Item2");
            if (fItem1 != null && fItem2 != null)
            {
                try
                {
                    quality = Convert.ToInt32(fItem1.GetValue(tupleValue));
                    variant = Convert.ToInt32(fItem2.GetValue(tupleValue));
                    return true;
                }
                catch { /* fallthrough */ }
            }

            // 4) Fields fallback (quality/variant)
            var fQuality = t.GetField("quality");
            var fVariant = t.GetField("variant");
            if (fQuality != null && fVariant != null)
            {
                try
                {
                    quality = Convert.ToInt32(fQuality.GetValue(tupleValue));
                    variant = Convert.ToInt32(fVariant.GetValue(tupleValue));
                    return true;
                }
                catch { /* fallthrough */ }
            }

            // 5) Last-resort: manual parse of integers from ToString() -> "(1, 2)" or similar
            try
            {
                string s = tupleValue.ToString();
                var numbers = new System.Collections.Generic.List<int>();
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < s.Length && numbers.Count < 2; i++)
                {
                    char c = s[i];
                    if (char.IsDigit(c) || (c == '-' && sb.Length == 0))
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        if (sb.Length > 0)
                        {
                            if (int.TryParse(sb.ToString(), out int val))
                            {
                                numbers.Add(val);
                            }
                            sb.Clear();
                        }
                    }
                }
                if (sb.Length > 0 && numbers.Count < 2)
                {
                    if (int.TryParse(sb.ToString(), out int val2))
                    {
                        numbers.Add(val2);
                    }
                    sb.Clear();
                }

                if (numbers.Count >= 2)
                {
                    quality = numbers[0];
                    variant = numbers[1];
                    return true;
                }
            }
            catch { }

            return false;
        }

        #region Resource removal helpers

        public static void RemoveRequiredResources(InventoryGui gui, Player player, Recipe selectedRecipe, bool crafted, bool skipRemovingResultResource = false)
        {
            if (player == null || selectedRecipe == null) return;
            var inventory = player.GetInventory();
            if (inventory == null) return;

            // Use the SAME removal key format as RemoveRequiredResourcesUpgrade:
            string removalKey = null;
            bool removalKeyAdded = false;
            try
            {
                try
                {
                    var recipeKeyNow = selectedRecipe != null ? RecipeFingerprint(selectedRecipe) : "null";
                    var target = _upgradeTargetItem ?? GetSelectedInventoryItem(gui);
                    var targetHash = target != null ? RuntimeHelpers.GetHashCode(target).ToString("X") : "null";
                    // Same format as the upgrade helper
                    removalKey = $"{recipeKeyNow}|t:{targetHash}|crafted:{crafted}";
                }
                catch { removalKey = null; }

                if (!string.IsNullOrEmpty(removalKey))
                {
                    lock (_recentRemovalKeysLock)
                    {
                        if (_recentRemovalKeys.Contains(removalKey))
                        {
                            LogInfo($"RemoveRequiredResources: skipping duplicate removal for {removalKey}");
                            return;
                        }
                        _recentRemovalKeys.Add(removalKey);
                        removalKeyAdded = true;
                    }

                    // Diagnostic: log the caller frame for the call that will perform removal
                    try
                    {
                        var st = new System.Diagnostics.StackTrace(1, false);
                        var frame = st.GetFrame(0);
                        var caller = frame != null ? $"{frame.GetMethod()?.DeclaringType?.Name}.{frame.GetMethod()?.Name}" : "unknown";
                        LogInfo($"RemoveRequiredResources CALLER: removalKey={removalKey} caller={caller}");
                    }
                    catch { }
                }

                // --- original instrumented body ---
                var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                int craftUpgrade = 1;
                if (craftUpgradeField != null)
                {
                    try
                    {
                        object value = craftUpgradeField.GetValue(gui);
                        if (value is int q && q > 1) craftUpgrade = q;
                    }
                    catch { craftUpgrade = 1; }
                }

                object GetMember(object obj, string name)
                {
                    if (obj == null) return null;
                    var t = obj.GetType();
                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) return f.GetValue(obj);
                    var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null) return p.GetValue(obj);
                    return null;
                }

                object resourcesObj = GetMember(selectedRecipe, "m_resources");
                var resources = resourcesObj as IEnumerable;
                if (resources == null) return;
                var resourceList = resources.Cast<object>().ToList();

                string resultName = null;
                try { resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name; } catch { resultName = null; }

                int RemoveAmountFromInventoryLocal(string resourceName, int amount)
                {
                    if (string.IsNullOrEmpty(resourceName) || amount <= 0) return 0;

                    int remaining = amount;
                    var items = inventory.GetAllItems();

                    if (VERBOSE_DEBUG)
                    {
                        try
                        {
                            int totalAvailable = items.Where(it => it != null && it.m_shared != null && string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase)).Sum(it => it.m_stack);
                            var details = items
                                .Where(it => it != null && it.m_shared != null)
                                .Select(it => $"{RuntimeHelpers.GetHashCode(it):X}:{it.m_shared.m_name}:q{it.m_quality}:s{it.m_stack}")
                                .Take(12);
                            LogInfo($"RemoveAmountFromInventoryLocal-ENTRY: resource={resourceName} requested={amount} totalAvailable={totalAvailable} stacks=[{string.Join(", ", details)}]");
                        }
                        catch { }
                    }

                    void TryRemove(Func<ItemDrop.ItemData, bool> predicate)
                    {
                        if (remaining <= 0) return;
                        for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                        {
                            var it = items[i];
                            if (it == null || it.m_shared == null) continue;
                            if (!predicate(it)) continue;
                            int toRemove = Math.Min(it.m_stack, remaining);
                            int before = it.m_stack;
                            it.m_stack -= toRemove;
                            remaining -= toRemove;
                            if (VERBOSE_DEBUG)
                            {
                                try
                                {
                                    LogInfo($"RemoveAmountFromInventoryLocal-REMOVE: removed={toRemove} from stackHash={RuntimeHelpers.GetHashCode(it):X} name={it.m_shared.m_name} q={it.m_quality} beforeStack={before} afterStack={it.m_stack}");
                                }
                                catch { }
                            }
                            if (it.m_stack <= 0)
                            {
                                try { inventory.RemoveItem(it); } catch { }
                            }
                        }
                    }

                    try
                    {
                        TryRemove(it => it.m_shared.m_name == resourceName);
                        TryRemove(it => string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase));
                        TryRemove(it => it.m_shared.m_name != null && resourceName != null && it.m_shared.m_name.IndexOf(resourceName, StringComparison.OrdinalIgnoreCase) >= 0);
                        TryRemove(it => it.m_shared.m_name != null && resourceName != null && resourceName.IndexOf(it.m_shared.m_name, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    catch { }

                    if (remaining > 0)
                    {
                        try
                        {
                            inventory.RemoveItem(resourceName, remaining);
                            remaining = 0;
                        }
                        catch { }
                    }

                    int removedTotal = amount - remaining;
                    if (VERBOSE_DEBUG)
                    {
                        try
                        {
                            var targetHash = "n/a";
                            LogInfo($"RemoveAmountFromInventoryLocal-EXIT: resource={resourceName} requested={amount} removed={removedTotal} targetHash={targetHash}");
                        }
                        catch { }
                    }

                    return removedTotal;
                }

                var validReqs = new List<(string name, int amount)>();
                foreach (var req in resourceList)
                {
                    try
                    {
                        var resItem = GetMember(req, "m_resItem");
                        if (resItem == null) continue;
                        string resName = null;
                        try
                        {
                            var itemData = resItem.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                            var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                            resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                        }
                        catch { }
                        if (string.IsNullOrEmpty(resName)) continue;

                        int baseAmount = 0;
                        try { var a = req.GetType().GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req); baseAmount = a != null ? Convert.ToInt32(a) : 0; } catch { baseAmount = 0; }
                        int perLevel = 0;
                        try { var pl = req.GetType().GetField("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req); perLevel = pl != null ? Convert.ToInt32(pl) : 0; } catch { perLevel = 0; }

                        bool isUpgradeNow = _isUpgradeDetected || IsUpgradeOperation(gui, selectedRecipe);
                        int finalAmount;
                        if (isUpgradeNow && perLevel > 0)
                        {
                            int craftUpgradeVal = craftUpgrade;
                            finalAmount = perLevel * Math.Max(1, craftUpgradeVal);
                        }
                        else
                        {
                            finalAmount = baseAmount;
                        }

                        if (finalAmount > 0) validReqs.Add((resName, finalAmount));
                    }
                    catch { }
                }

                if (VERBOSE_DEBUG)
                {
                    try
                    {
                        var rq = string.Join(", ", validReqs.Select(v => $"{v.name}:{v.amount}"));
                        LogInfo($"RemoveRequiredResources-DBG: isUpgradeDetected={_isUpgradeDetected} IsUpgradeOperation={IsUpgradeOperation(gui, selectedRecipe)} craftUpgrade={craftUpgrade} craftedFlag={crafted} computedReqs=[{rq}] skipRemovingResultResource={skipRemovingResultResource}");
                    }
                    catch { }
                }

                List<(string name, int amount)> validReqsFiltered = validReqs;
                if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName))
                {
                    validReqsFiltered = validReqs.Where(v => !string.Equals(v.name, resultName, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (validReqs.Count == 0) return;

                if (!crafted)
                {
                    if (validReqsFiltered.Count == 0) return;
                    int keepIndex = UnityEngine.Random.Range(0, validReqsFiltered.Count);
                    var keepTuple = validReqsFiltered[keepIndex];

                    if (VERBOSE_DEBUG)
                    {
                        try
                        {
                            LogInfo($"RemoveRequiredResources (failure, non-crafted): keeping one resource: {keepTuple.name}:{keepTuple.amount} (index {keepIndex})");
                        }
                        catch { }
                    }

                    bool skippedKeep = false;
                    foreach (var req in validReqs)
                    {
                        if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) &&
                            string.Equals(req.name, resultName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!skippedKeep && string.Equals(req.name, keepTuple.name, StringComparison.OrdinalIgnoreCase) && req.amount == keepTuple.amount)
                        {
                            skippedKeep = true;
                            if (VERBOSE_DEBUG) LogInfo($"RemoveRequiredResources: skipping keep resource {req.name}:{req.amount}");
                            continue;
                        }

                        try
                        {
                            if (VERBOSE_DEBUG) LogInfo($"RemoveRequiredResources: removing (failure) {req.name} amount={req.amount}");
                            int removed = RemoveAmountFromInventoryLocal(req.name, req.amount);
                            if (VERBOSE_DEBUG) LogInfo($"RemoveRequiredResources: removal result {req.name} removed={removed} requested={req.amount}");
                        }
                        catch (Exception ex)
                        {
                            LogWarning($"RemoveRequiredResources removal exception: {ex}");
                        }
                    }
                    return;
                }

                foreach (var req in validReqs)
                {
                    if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) &&
                        string.Equals(req.name, resultName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    try
                    {
                        if (VERBOSE_DEBUG) LogInfo($"RemoveRequiredResources: removing (crafted) {req.name} amount={req.amount}");
                        int removed = RemoveAmountFromInventoryLocal(req.name, req.amount);
                        if (VERBOSE_DEBUG) LogInfo($"RemoveRequiredResources: removal result {req.name} removed={removed} requested={req.amount}");
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"RemoveRequiredResources removal exception: {ex}");
                    }
                }

                // --- end original body ---
            }
            finally
            {
                try
                {
                    if (removalKeyAdded && !string.IsNullOrEmpty(removalKey))
                    {
                        lock (_recentRemovalKeysLock)
                        {
                            _recentRemovalKeys.Remove(removalKey);
                        }
                    }
                }
                catch { }
            }
        }

        // RemoveRequiredResourcesUpgrade: prefer GUI-captured normalized requirements; fallback to recipe/ObjectDB resources
        // Replace the existing RemoveRequiredResourcesUpgrade(...) function with this implementation.

        public static void RemoveRequiredResourcesUpgrade(InventoryGui gui, Player player, Recipe selectedRecipe, ItemDrop.ItemData upgradeTarget, bool crafted)
        {
            if (player == null || selectedRecipe == null) return;
            var inventory = player.GetInventory();
            if (inventory == null) return;

            try
            {
                // Read craftUpgrade (levels) from GUI if available
                var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                int craftUpgrade = 1;
                if (craftUpgradeField != null)
                {
                    try
                    {
                        object cv = craftUpgradeField.GetValue(gui);
                        if (cv is int iv && iv > 0) craftUpgrade = iv;
                    }
                    catch { craftUpgrade = 1; }
                }

                // helper to extract recipe resources (same reflection used elsewhere)
                object GetMember(object obj, string name)
                {
                    if (obj == null) return null;
                    var t = obj.GetType();
                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null) return f.GetValue(obj);
                    var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null) return p.GetValue(obj);
                    return null;
                }

                object resourcesObj = GetMember(selectedRecipe, "m_resources");
                var resources = resourcesObj as IEnumerable;
                if (resources == null) return;
                var resourceList = resources.Cast<object>().ToList();

                // Build list of (resourceName, perLevelAmount)
                var validReqs = new List<(string name, int perLevel)>();
                foreach (var req in resourceList)
                {
                    try
                    {
                        var resItem = GetMember(req, "m_resItem");
                        if (resItem == null) continue;
                        string resName = null;
                        try
                        {
                            var itemData = resItem.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                            var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                            resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                        }
                        catch { }
                        if (string.IsNullOrEmpty(resName)) continue;

                        int perLevel = 0;
                        try { var pl = req.GetType().GetField("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req); perLevel = pl != null ? Convert.ToInt32(pl) : 0; } catch { perLevel = 0; }
                        int baseAmount = 0;
                        try { var a = req.GetType().GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req); baseAmount = a != null ? Convert.ToInt32(a) : 0; } catch { baseAmount = 0; }

                        // For upgrade logic, we care about per-level amounts; if perLevel==0 fallback to baseAmount
                        int effectivePerLevel = perLevel > 0 ? perLevel : baseAmount;
                        if (effectivePerLevel > 0) validReqs.Add((resName, effectivePerLevel));
                    }
                    catch { }
                }

                if (validReqs.Count == 0) return;

                // resultName (to optionally skip removing the produced target resource)
                string resultName = null;
                try { resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name; } catch { resultName = null; }

                // Helper to remove amount while skipping the upgrade target item if needed (keeps same semantics as R3U)
                int RemoveAmountSkippingTarget(string resourceName, int amount)
                {
                    // Use existing helper if present in file; otherwise reimplement minimal removal that avoids the upgradeTarget instance
                    try
                    {
                        // Prefer calling a central helper if available
                        var method = typeof(ChanceCraftPlugin).GetMethod("RemoveAmountFromInventorySkippingTarget", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                        if (method != null)
                        {
                            object removed = method.Invoke(null, new object[] { inventory, resourceName, amount, upgradeTarget });
                            if (removed is int ri) return ri;
                        }
                    }
                    catch { }

                    // Fallback: remove normally but try to avoid target by removing from stacks that are not the target
                    int remaining = amount;
                    var items = inventory.GetAllItems();
                    if (items == null) return 0;

                    // First pass: remove from stacks that are not the upgradeTarget (by reference/hash)
                    for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                    {
                        var it = items[i];
                        if (it == null || it.m_shared == null) continue;
                        if (!string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (upgradeTarget != null && (ReferenceEquals(it, upgradeTarget) || RuntimeHelpers.GetHashCode(it) == RuntimeHelpers.GetHashCode(upgradeTarget))) continue;
                        int toRemove = Math.Min(it.m_stack, remaining);
                        int before = it.m_stack;
                        it.m_stack -= toRemove;
                        remaining -= toRemove;
                        try { LogInfo($"RemoveAmountFromInventorySkippingTarget-REMOVE: removed={toRemove} from stackHash={RuntimeHelpers.GetHashCode(it):X} name={it.m_shared.m_name} q={it.m_quality} beforeStack={before} afterStack={it.m_stack}"); } catch { }
                        if (it.m_stack <= 0)
                        {
                            try { inventory.RemoveItem(it); } catch { }
                        }
                    }

                    // Second pass: remove from any remaining stacks (may include target)
                    if (remaining > 0)
                    {
                        for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                        {
                            var it = items[i];
                            if (it == null || it.m_shared == null) continue;
                            if (!string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase)) continue;
                            int toRemove = Math.Min(it.m_stack, remaining);
                            int before = it.m_stack;
                            it.m_stack -= toRemove;
                            remaining -= toRemove;
                            try { LogInfo($"RemoveAmountFromInventorySkippingTarget-REMOVE-FALLBACK: removed={toRemove} from stackHash={RuntimeHelpers.GetHashCode(it):X} name={it.m_shared.m_name} q={it.m_quality} beforeStack={before} afterStack={it.m_stack}"); } catch { }
                            if (it.m_stack <= 0)
                            {
                                try { inventory.RemoveItem(it); } catch { }
                            }
                        }
                    }

                    return amount - remaining;
                }

                if (!crafted)
                {
                    // UPGRADE FAILURE: remove only one randomly chosen required resource (per-level * craftUpgrade)
                    var candidates = validReqs.Where(r => !(string.Equals(r.name, resultName, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (candidates.Count == 0)
                    {
                        // if all required resources equal the result (rare), allow removing from validReqs
                        candidates = validReqs;
                    }

                    int idx = UnityEngine.Random.Range(0, candidates.Count);
                    var chosen = candidates[idx];
                    int amountToRemove = chosen.perLevel * Math.Max(1, craftUpgrade);

                    try { LogInfo($"RemoveRequiredResourcesUpgrade-FAIL: chosenResource={chosen.name} amount={amountToRemove} targetHash={(upgradeTarget != null ? RuntimeHelpers.GetHashCode(upgradeTarget).ToString("X") : "null")}"); } catch { }

                    int removed = RemoveAmountSkippingTarget(chosen.name, amountToRemove);

                    try { LogInfo($"RemoveRequiredResourcesUpgrade-FAIL: removed={removed} requested={amountToRemove} resource={chosen.name}"); } catch { }
                    return;
                }
                else
                {
                    // UPGRADE SUCCESS: remove per-level amounts for all required resources (same behaviour as before)
                    foreach (var req in validReqs)
                    {
                        // skip removing the produced result resource
                        if (!string.IsNullOrEmpty(resultName) && string.Equals(req.name, resultName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        int amountToRemove = req.perLevel * Math.Max(1, craftUpgrade);
                        try { LogInfo($"RemoveRequiredResourcesUpgrade: removing (crafted) {req.name} amount={amountToRemove}"); } catch { }

                        int removed = 0;
                        try
                        {
                            removed = RemoveAmountSkippingTarget(req.name, amountToRemove);
                        }
                        catch (Exception ex) { LogWarning($"RemoveRequiredResourcesUpgrade removal exception: {ex}"); }

                        try { LogInfo($"RemoveRequiredResourcesUpgrade: removal result {req.name} removed={removed} requested={amountToRemove}"); } catch { }
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                LogWarning($"RemoveRequiredResourcesUpgrade exception: {ex}");
            }
        }

        // Replace the old ForceRevertAfterRemoval with this safer version.
        private static void ForceRevertAfterRemoval(InventoryGui gui, Recipe selectedRecipe, ItemDrop.ItemData upgradeTarget = null)
        {
            try
            {
                if (selectedRecipe == null) return;
                string resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(resultName)) return;
                int finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;

                // Prefer a per-item previous quality if available.
                int expectedPreQuality = Math.Max(0, finalQuality - 1);

                try
                {
                    if (_preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                    {
                        var preQs = _preCraftSnapshotData.Values
                            .Select(v => { if (TryUnpackQualityVariant(v, out int a, out int b)) return a; return 0; })
                            .ToList();
                        if (preQs.Count > 0) expectedPreQuality = Math.Max(0, preQs.Max());
                    }
                }
                catch { }

                var inv = Player.m_localPlayer?.GetInventory();
                var all = inv?.GetAllItems();
                if (all == null) return;

                // 1) If an explicit upgradeTarget was provided, try to revert only that one.
                if (upgradeTarget != null)
                {
                    try
                    {
                        // find the runtime instance in inventory (prefer reference equality, then hash match)
                        var found = all.FirstOrDefault(it => it != null && (ReferenceEquals(it, upgradeTarget) || RuntimeHelpers.GetHashCode(it) == RuntimeHelpers.GetHashCode(upgradeTarget)));
                        if (found != null)
                        {
                            // prefer previous quality from pre-snapshot data keyed to the original reference (if any)
                            int prevQ = expectedPreQuality;
                            int prevV = found.m_variant;
                            try
                            {
                                // try direct lookup by reference first
                                if (_preCraftSnapshotData != null && _preCraftSnapshotData.TryGetValue(upgradeTarget, out var tupleVal) && TryUnpackQualityVariant(tupleVal, out int pq, out int pv))
                                {
                                    prevQ = pq;
                                    prevV = pv;
                                }
                                else
                                {
                                    // try lookup by hash if available
                                    int h = RuntimeHelpers.GetHashCode(found);
                                    if (_preCraftSnapshotHashQuality != null && _preCraftSnapshotHashQuality.TryGetValue(h, out int prevHashQ))
                                    {
                                        prevQ = prevHashQ;
                                        var kv = _preCraftSnapshotData?.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == h);
                                        if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && TryUnpackQualityVariant(kv.Value, out int pq2, out int pv2))
                                        {
                                            prevV = pv2;
                                        }
                                    }
                                }
                            }
                            catch { }

                            if (found.m_quality > prevQ)
                            {
                                LogInfo($"ForceRevertAfterRemoval: reverting target item itemHash={RuntimeHelpers.GetHashCode(found):X} name={found.m_shared?.m_name} oldQ={found.m_quality} -> {prevQ}");
                                found.m_quality = prevQ;
                                try { found.m_variant = prevV; } catch { }
                            }
                            return;
                        }
                    }
                    catch { /* fail to target revert -> continue to fallback */ }
                }

                // 2) Conservative fallback: if we have a hashmap of pre-snapshot hash->quality, revert at most one item
                try
                {
                    if (_preCraftSnapshotHashQuality != null && _preCraftSnapshotHashQuality.Count > 0)
                    {
                        foreach (var it in all)
                        {
                            if (it == null || it.m_shared == null) continue;
                            int h = RuntimeHelpers.GetHashCode(it);
                            if (_preCraftSnapshotHashQuality.TryGetValue(h, out int prevQ))
                            {
                                if (it.m_quality > prevQ)
                                {
                                    // revert only this single item and stop
                                    LogInfo($"ForceRevertAfterRemoval: reverting by-hash item itemHash={h:X} name={it.m_shared.m_name} oldQ={it.m_quality} -> {prevQ}");
                                    it.m_quality = prevQ;
                                    var kv = _preCraftSnapshotData?.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == h);
                                    if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && TryUnpackQualityVariant(kv.Value, out int pqf, out int pvf))
                                    {
                                        try { it.m_variant = pvf; } catch { }
                                    }
                                    return;
                                }
                            }
                        }
                    }
                }
                catch { }

                // 3) Last resort (VERY conservative): find any single inventory item matching the resultName that has quality > expectedPreQuality,
                // revert only the first one found.
                try
                {
                    foreach (var it in all)
                    {
                        if (it == null || it.m_shared == null) continue;
                        if (!string.Equals(it.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (it.m_quality > expectedPreQuality)
                        {
                            LogInfo($"ForceRevertAfterRemoval: last-resort revert itemHash={RuntimeHelpers.GetHashCode(it):X} name={it.m_shared.m_name} oldQ={it.m_quality} -> {expectedPreQuality}");
                            it.m_quality = expectedPreQuality;
                            // try restore variant from snapshot if available
                            var kv = _preCraftSnapshotData?.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == RuntimeHelpers.GetHashCode(it));
                            if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && TryUnpackQualityVariant(kv.Value, out int pq3, out int pv3))
                            {
                                try { it.m_variant = pv3; } catch { }
                            }
                            return;
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        // Add this helper method somewhere in ChanceCraft.cs near other inventory helper methods
        // (e.g. after RemoveRequiredResourcesUpgrade or near other private static helpers).
        private static void RemoveAmountFromInventoryLocal(string resName, int amount)
        {
            try
            {
                if (string.IsNullOrEmpty(resName) || amount <= 0) return;
                var player = Player.m_localPlayer;
                if (player == null) return;
                var inv = player.GetInventory();
                if (inv == null) return;

                int toRemove = amount;

                // iterate backwards to prefer newer stacks (similar to other removal logic in file)
                var items = inv.GetAllItems();
                for (int i = items.Count - 1; i >= 0 && toRemove > 0; i--)
                {
                    var item = items[i];
                    if (item == null || item.m_shared == null) continue;
                    if (!string.Equals(item.m_shared.m_name, resName, StringComparison.OrdinalIgnoreCase)) continue;

                    int remove = Math.Min(item.m_stack, toRemove);
                    item.m_stack -= remove;
                    toRemove -= remove;
                    if (item.m_stack <= 0)
                    {
                        try { inv.RemoveItem(item); } catch { }
                    }
                }

                if (toRemove > 0)
                {
                    // Fallback: try contains-like match using IndexOf (works on Unity's runtime)
                    for (int i = items.Count - 1; i >= 0 && toRemove > 0; i--)
                    {
                        var item = items[i];
                        if (item == null || item.m_shared == null) continue;
                        if (item.m_shared.m_name.IndexOf(resName, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        int remove = Math.Min(item.m_stack, toRemove);
                        item.m_stack -= remove;
                        toRemove -= remove;
                        if (item.m_stack <= 0)
                        {
                            try { inv.RemoveItem(item); } catch { }
                        }
                    }
                }

                LogInfo($"RemoveAmountFromInventoryLocal: requested={amount} name={resName} removed={amount - toRemove}");
            }
            catch (Exception ex)
            {
                LogWarning($"RemoveAmountFromInventoryLocal exception: {ex}");
            }
        }

        private static int RemoveAmountFromInventorySkippingTarget(Inventory inventory, ItemDrop.ItemData upgradeTargetItem, string resourceName, int amount)
        {
            if (inventory == null || string.IsNullOrEmpty(resourceName) || amount <= 0) return 0;

            int remaining = amount;
            var items = inventory.GetAllItems();
            try
            {
                // 1) exact match
                for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    var it = items[i];
                    if (it == null || it.m_shared == null) continue;
                    if (upgradeTargetItem != null && ReferenceEquals(it, upgradeTargetItem)) continue;
                    if (it.m_shared.m_name != resourceName) continue;
                    int toRemove = Math.Min(it.m_stack, remaining);
                    it.m_stack -= toRemove;
                    remaining -= toRemove;
                    if (it.m_stack <= 0) inventory.RemoveItem(it);
                }

                // 2) case-insensitive exact
                for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    var it = items[i];
                    if (it == null || it.m_shared == null) continue;
                    if (upgradeTargetItem != null && ReferenceEquals(it, upgradeTargetItem)) continue;
                    if (!string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase)) continue;
                    int toRemove = Math.Min(it.m_stack, remaining);
                    it.m_stack -= toRemove;
                    remaining -= toRemove;
                    if (it.m_stack <= 0) inventory.RemoveItem(it);
                }

                // 3) contains
                for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    var it = items[i];
                    if (it == null || it.m_shared == null) continue;
                    if (upgradeTargetItem != null && ReferenceEquals(it, upgradeTargetItem)) continue;
                    var name = it.m_shared.m_name;
                    if (name == null || resourceName == null) continue;
                    if (name.IndexOf(resourceName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int toRemove = Math.Min(it.m_stack, remaining);
                    it.m_stack -= toRemove;
                    remaining -= toRemove;
                    if (it.m_stack <= 0) inventory.RemoveItem(it);
                }

                // 4) reverse contains
                for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    var it = items[i];
                    if (it == null || it.m_shared == null) continue;
                    if (upgradeTargetItem != null && ReferenceEquals(it, upgradeTargetItem)) continue;
                    var name = it.m_shared.m_name;
                    if (name == null || resourceName == null) continue;
                    if (resourceName.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int toRemove = Math.Min(it.m_stack, remaining);
                    it.m_stack -= toRemove;
                    remaining -= toRemove;
                    if (it.m_stack <= 0) inventory.RemoveItem(it);
                }
            }
            catch { }

            if (remaining > 0)
            {
                try { inventory.RemoveItem(resourceName, remaining); remaining = 0; } catch { }
            }

            int removedTotal = amount - remaining;
            try
            {
                var targetHash = upgradeTargetItem != null ? RuntimeHelpers.GetHashCode(upgradeTargetItem).ToString("X") : "null";
                LogInfo($"RemoveAmountFromInventorySkippingTarget: requested={amount} removed={removedTotal} resource={resourceName} targetHash={targetHash}");
            }
            catch { }

            return amount - remaining;
        }

        #endregion

        #region GUI refresh & requirement parsing

        private static void RefreshCraftingPanel(InventoryGui gui)
        {
            if (gui == null) return;
            var t = gui.GetType();

            string[] methods = new[]
            {
                "UpdateCraftingPanel", "UpdateRecipeList", "UpdateAvailableRecipes", "UpdateCrafting",
                "UpdateAvailableCrafting", "UpdateRecipes", "UpdateSelectedItem", "Refresh",
                "RefreshList", "UpdateIcons", "Setup", "OnOpen", "OnShow"
            };

            foreach (var name in methods)
            {
                try
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m != null && m.GetParameters().Length == 0)
                    {
                        try { m.Invoke(gui, null); } catch { }
                    }
                }
                catch { }
            }

            try
            {
                var selField = t.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (selField != null)
                {
                    var cur = selField.GetValue(gui);
                    try { selField.SetValue(gui, null); } catch { }
                    try { selField.SetValue(gui, cur); } catch { }
                }
            }
            catch { }

            try
            {
                var inventoryField = t.GetField("m_playerInventory", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                 ?? t.GetField("m_inventory", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var invObj = inventoryField?.GetValue(gui);
                if (invObj != null)
                {
                    var invType = invObj.GetType();
                    foreach (var name in new[] { "Refresh", "UpdateIfNeeded", "Update", "OnChanged" })
                    {
                        try
                        {
                            var rm = invType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (rm != null && rm.GetParameters().Length == 0)
                            {
                                try { rm.Invoke(invObj, null); } catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // Diagnostic: dump InventoryGui fields and GameObject children to the plugin log (enable loggingEnabled = true)
        private static void DumpInventoryGuiStructure(InventoryGui gui)
        {
            try
            {
                if (gui == null) { LogInfo("DumpInventoryGuiStructure: gui is null"); return; }
                var t = gui.GetType();
                LogInfo("DumpInventoryGuiStructure: InventoryGui type = " + t.FullName);
                // Fields summary
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        object val = null;
                        try { val = f.GetValue(gui); } catch { val = "<unreadable>"; }
                        string valType = val == null ? "null" : (val.GetType().FullName + (val is System.Collections.IEnumerable ? " (IEnumerable)" : ""));
                        LogInfo($"Field: {f.Name} : {f.FieldType.FullName} => {valType}");
                    }
                    catch { }
                }
                // Properties summary
                foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        object val = null;
                        if (p.GetIndexParameters().Length == 0 && p.CanRead)
                        {
                            try { val = p.GetValue(gui); } catch { val = "<unreadable>"; }
                        }
                        string valType = val == null ? "null" : (val.GetType().FullName + (val is System.Collections.IEnumerable ? " (IEnumerable)" : ""));
                        LogInfo($"Property: {p.Name} : {p.PropertyType.FullName} => {valType}");
                    }
                    catch { }
                }

                // Dump selected recipe if present
                try
                {
                    var selField = t.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (selField != null)
                    {
                        var selVal = selField.GetValue(gui);
                        if (selVal != null) LogInfo("m_selectedRecipe value type = " + selVal.GetType().FullName);
                        else LogInfo("m_selectedRecipe is null");
                    }
                }
                catch { }

                // Dump GameObject children (UI controls) — limited depth to avoid huge logs
                try
                {
                    var rootGo = (gui as Component)?.gameObject;
                    if (rootGo == null) rootGo = typeof(InventoryGui).GetField("m_root", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(gui) as GameObject;
                    if (rootGo == null) LogInfo("DumpInventoryGuiStructure: gui.gameObject unknown");
                    else
                    {
                        LogInfo("DumpInventoryGuiStructure: dumping child hierarchy (depth 3) for " + rootGo.name);
                        void DumpChildren(UnityEngine.Transform tr, int depth)
                        {
                            if (tr == null || depth <= 0) return;
                            for (int i = 0; i < tr.childCount; i++)
                            {
                                var c = tr.GetChild(i);
                                string info = $"GO: {new string(' ', (3 - depth) * 2)}{c.name}";
                                // detect UI components
                                var btn = c.GetComponent<UnityEngine.UI.Button>();
                                if (btn != null) info += " [Button]";
                                var tog = c.GetComponent<UnityEngine.UI.Toggle>();
                                if (tog != null) info += " [Toggle]";
                                var txt = c.GetComponent<UnityEngine.UI.Text>();
                                if (txt != null) info += $" [Text='{txt.text}']";
                                LogInfo(info);
                                DumpChildren(c, depth - 1);
                            }
                        }
                        DumpChildren(rootGo.transform, 3);
                    }
                }
                catch (Exception ex) { LogWarning("DumpInventoryGuiStructure: child dump failed: " + ex); }
            }
            catch (Exception ex)
            {
                LogWarning("DumpInventoryGuiStructure failed: " + ex);
            }
        }

        public static void ForceSimulateTabSwitchRefresh(InventoryGui gui)
        {
            if (gui == null) return;
            try
            {
                // Start coroutine that does the simulated clicks
                gui.StartCoroutine(ForceSimulateTabSwitchRefreshCoroutine(gui));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] ForceSimulateTabSwitchRefresh: failed to start coroutine: {ex}");
                // Fallback to prior conservative refresh
                try { RefreshUpgradeTabInner(gui); } catch { }
            }
        }

        public static void RefreshUpgradeTabInner(InventoryGui gui)
        {
            if (gui == null) return;
            try
            {
                var t = gui.GetType();

                // 1) Try to refresh known InventoryGui update methods first (non-destructive).
                string[] igMethods = new[]
                {
                    "UpdateCraftingPanel", "UpdateRecipeList", "UpdateAvailableRecipes", "UpdateCrafting",
                    "UpdateAvailableCrafting", "UpdateRecipes", "UpdateSelectedItem", "Refresh",
                    "RefreshList", "RefreshRequirements", "OnOpen", "OnShow"
                };
                foreach (var name in igMethods)
                {
                    try
                    {
                        var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (m != null && m.GetParameters().Length == 0)
                        {
                            try { m.Invoke(gui, null); UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: invoked InventoryGui.{name}()"); } catch { }
                        }
                    }
                    catch { /* ignore */ }
                }

                // 2) If InventoryGui holds a wrapper in m_selectedRecipe or similar, try to refresh it
                FieldInfo selField = t.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                   ?? t.GetField("m_upgradeRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                   ?? t.GetField("m_selectedUpgradeRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                object wrapper = null;
                if (selField != null)
                {
                    try { wrapper = selField.GetValue(gui); } catch { wrapper = null; }
                }
                else
                {
                    // Try property fallback
                    var selProp = t.GetProperty("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?? t.GetProperty("SelectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (selProp != null && selProp.CanRead)
                    {
                        try { wrapper = selProp.GetValue(gui); } catch { wrapper = null; }
                    }
                }

                if (wrapper != null)
                {
                    var wt = wrapper.GetType();

                    // Try common refresh methods on the wrapper itself
                    string[] wrapperMethods = new[] { "Refresh", "Update", "UpdateIfNeeded", "RefreshRequirements", "RefreshList", "OnChanged", "Setup" };
                    foreach (var name in wrapperMethods)
                    {
                        try
                        {
                            var m = wt.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (m != null && m.GetParameters().Length == 0)
                            {
                                try { m.Invoke(wrapper, null); UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: invoked wrapper.{name}()"); } catch { }
                            }
                        }
                        catch { /* ignore per-method */ }
                    }

                    // If wrapper exposes a Recipe property, toggle it to force rebind in the GUI
                    try
                    {
                        var rp = wt.GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (rp != null && rp.CanRead && rp.CanWrite)
                        {
                            try
                            {
                                var val = rp.GetValue(wrapper);
                                rp.SetValue(wrapper, null);
                                rp.SetValue(wrapper, val);
                                UnityEngine.Debug.LogWarning("[ChanceCraft] RefreshUpgradeTabInner: toggled wrapper.Recipe to force UI rebind");
                            }
                            catch { /* best-effort */ }
                        }
                    }
                    catch { /* ignore */ }
                }

                // 3) Try to refresh the requirement list field(s) that InventoryGui may expose (m_reqList / m_requirements)
                try
                {
                    var reqField = t.GetField("m_reqList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                ?? t.GetField("m_requirements", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (reqField != null)
                    {
                        var reqObj = reqField.GetValue(gui);
                        if (reqObj != null)
                        {
                            var rt = reqObj.GetType();
                            foreach (var name in new[] { "Refresh", "Update", "UpdateIfNeeded", "OnChanged" })
                            {
                                try
                                {
                                    var m = rt.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (m != null && m.GetParameters().Length == 0)
                                    {
                                        try { m.Invoke(reqObj, null); UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: invoked reqObj.{name}()"); } catch { }
                                    }
                                }
                                catch { /* ignore per-method */ }
                            }
                        }
                    }
                }
                catch { /* ignore */ }

                // 4) Schedule a delayed refresh next frame to let Valheim finish internal changes (safe/elegant)
                try
                {
                    gui.StartCoroutine(DelayedRefreshCraftingPanel(gui, 1));
                    UnityEngine.Debug.LogWarning("[ChanceCraft] RefreshUpgradeTabInner: scheduled DelayedRefreshCraftingPanel(gui,1)");
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: could not start coroutine: {ex}");
                    // As fallback, call synchronous RefreshCraftingPanel
                    try { RefreshCraftingPanel(gui); } catch { }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: unexpected exception: {ex}");
            }
        }

        private static IEnumerator ForceSimulateTabSwitchRefreshCoroutine(InventoryGui gui)
        {
            if (gui == null) yield break;

            // Small helper to get fields/properties like before
            object TryGetMember(string name)
            {
                try
                {
                    var t = gui.GetType();
                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null) return f.GetValue(gui);
                    var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (p != null && p.CanRead) return p.GetValue(gui);
                }
                catch { }
                return null;
            }

            // Try to find an explicit tab GameObject via common names first
            string[] tabCandidates = new[] { "m_tabUpgrade", "m_tabCraft", "m_tabCrafting", "m_tab", "m_crafting", "m_tabPanel", "m_tabs" };
            GameObject upgradeTabGO = null;
            foreach (var nm in tabCandidates)
            {
                try
                {
                    var val = TryGetMember(nm);
                    if (val != null)
                    {
                        if (val is GameObject g) { upgradeTabGO = g; break; }
                        if (val is Component c) { upgradeTabGO = c.gameObject; break; }
                        if (val is System.Collections.IEnumerable en && !(val is string))
                        {
                            foreach (var e in en)
                            {
                                if (e is GameObject ge) { upgradeTabGO = ge; break; }
                                if (e is Component ce) { upgradeTabGO = ce.gameObject; break; }
                            }
                            if (upgradeTabGO != null) break;
                        }
                    }
                }
                catch { }
            }

            // Fallback: find button/toggle under the InventoryGui whose name hints at "craft" or "upgrade"
            Button backButton = null;
            Toggle backToggle = null;

            try
            {
                var comp = gui as Component;
                if (upgradeTabGO != null)
                {
                    // If explicit GO found, search inside it for tab control
                    backButton = upgradeTabGO.GetComponentInChildren<Button>(true);
                    backToggle = upgradeTabGO.GetComponentInChildren<Toggle>(true);
                }
                else if (comp != null)
                {
                    // scan the InventoryGui children for named buttons/toggles
                    var btns = comp.GetComponentsInChildren<Button>(true);
                    var tgls = comp.GetComponentsInChildren<Toggle>(true);

                    // prefer names containing 'craft'/'upgrade'/'tab'
                    backButton = btns.FirstOrDefault(b => b.gameObject.name.IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0
                                                         || b.gameObject.name.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                                                         || b.gameObject.name.IndexOf("tab", StringComparison.OrdinalIgnoreCase) >= 0)
                              ?? btns.FirstOrDefault();
                    backToggle = tgls.FirstOrDefault(t => t.gameObject.name.IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0
                                                         || t.gameObject.name.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                                                         || t.gameObject.name.IndexOf("tab", StringComparison.OrdinalIgnoreCase) >= 0)
                              ?? tgls.FirstOrDefault();
                }
            }
            catch { /* ignore scanning errors */ }

            // Now pick one "other" tab to click away to. Prefer any other Button/Toggle that is not the same as the backButton/backToggle
            Button awayButton = null;
            Toggle awayToggle = null;
            try
            {
                var comp = gui as Component;
                if (comp != null)
                {
                    var btnsAll = comp.GetComponentsInChildren<Button>(true);
                    var tglsAll = comp.GetComponentsInChildren<Toggle>(true);

                    awayButton = btnsAll.FirstOrDefault(b => b != backButton && !b.gameObject.name.Equals("Close", StringComparison.OrdinalIgnoreCase));
                    awayToggle = tglsAll.FirstOrDefault(t => t != backToggle);
                }
            }
            catch { }

            // If we have nothing sensible, call the conservative refresh and exit
            if (backButton == null && backToggle == null)
            {
                try { RefreshUpgradeTabInner(gui); } catch { }
                yield break;
            }

            // Simulate: click away, wait a frame, click back, wait a frame
            try
            {
                // Click away if possible
                if (awayButton != null)
                {
                    try { awayButton.onClick?.Invoke(); UnityEngine.Debug.LogWarning("[ChanceCraft] ForceSimulateTabSwitch: invoked awayButton.onClick"); } catch { }
                }
                else if (awayToggle != null)
                {
                    try { awayToggle.isOn = !awayToggle.isOn; UnityEngine.Debug.LogWarning("[ChanceCraft] ForceSimulateTabSwitch: toggled awayToggle"); } catch { }
                }
                else
                {
                    // If we don't have a found away control, briefly deactivate a small candidate GO (safe fallback)
                    try
                    {
                        var comp = gui as Component;
                        var child = (comp?.transform?.childCount > 0) ? comp.transform.GetChild(0).gameObject : null;
                        if (child != null) { child.SetActive(false); }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] ForceSimulateTabSwitch: away-click failed: {ex}");
            }

            // wait a frame
            yield return null;

            // Now click back to upgrade tab
            try
            {
                if (backButton != null)
                {
                    try { backButton.onClick?.Invoke(); UnityEngine.Debug.LogWarning("[ChanceCraft] ForceSimulateTabSwitch: invoked backButton.onClick"); } catch { }
                }
                else if (backToggle != null)
                {
                    try { backToggle.isOn = true; UnityEngine.Debug.LogWarning("[ChanceCraft] ForceSimulateTabSwitch: set backToggle.isOn = true"); } catch { }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] ForceSimulateTabSwitch: back-click failed: {ex}");
            }

            // Wait another frame for UI to rebind, then call conservative refresh helpers
            yield return null;
            try { RefreshUpgradeTabInner(gui); } catch { }
            try { RefreshInventoryGui(gui); } catch { }
            try { RefreshCraftingPanel(gui); } catch { }

            yield break;
        }

        private static IEnumerator DelayedRefreshCraftingPanel(InventoryGui gui, int delayFrames = 1)
        {
            if (gui == null) yield break;
            for (int i = 0; i < Math.Max(1, delayFrames); i++) yield return null;
            try { RefreshCraftingPanel(gui); } catch { }
        }

        private static void RefreshInventoryGui(InventoryGui gui)
        {
            if (gui == null) return;
            var t = gui.GetType();
            var candidates = new[]
            {
                "UpdateCraftingPanel","UpdateRecipeList","UpdateAvailableRecipes","UpdateCrafting",
                "Refresh","RefreshList","UpdateAvailableCrafting","UpdateRecipes","UpdateInventory",
                "UpdateSelectedItem","OnInventoryChanged","RefreshInventory","UpdateIcons","Setup","OnOpen","OnShow"
            };
            foreach (var name in candidates)
            {
                try
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m != null && m.GetParameters().Length == 0) m.Invoke(gui, null);
                }
                catch { }
            }

            try
            {
                var inventoryField = t.GetField("m_playerInventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?? t.GetField("m_inventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var inventoryObj = inventoryField?.GetValue(gui);
                if (inventoryObj != null)
                {
                    var invType = inventoryObj.GetType();
                    foreach (var rm in new[] { "Refresh", "UpdateIfNeeded", "Update", "OnChanged" })
                    {
                        try
                        {
                            var rmMethod = invType.GetMethod(rm, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (rmMethod != null && rmMethod.GetParameters().Length == 0) rmMethod.Invoke(inventoryObj, null);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static bool TryGetRequirementsFromGui(InventoryGui gui, out List<(string name, int amount)> requirements)
        {
            requirements = null;
            if (gui == null) return false;

            try
            {
                var tGui = typeof(InventoryGui);

                List<(string name, int amount)> ParseRequirementEnumerable(IEnumerable reqEnum)
                {
                    var outList = new List<(string name, int amount)>();
                    if (reqEnum == null) return outList;
                    int idx = 0;
                    foreach (var elem in reqEnum)
                    {
                        idx++;
                        if (elem == null) { if (idx > 200) break; else continue; }
                        try
                        {
                            var et = elem.GetType();

                            object resItem = null;
                            var fResItem = et.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var pResItem = et.GetProperty("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fResItem != null) resItem = fResItem.GetValue(elem);
                            else if (pResItem != null) resItem = pResItem.GetValue(elem);

                            string resName = null;
                            if (resItem != null)
                            {
                                var itemDataField = resItem.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var itemDataProp = resItem.GetType().GetProperty("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                object itemDataVal = itemDataField != null ? itemDataField.GetValue(resItem) : itemDataProp?.GetValue(resItem);
                                if (itemDataVal != null)
                                {
                                    var sharedField = itemDataVal.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    var sharedProp = itemDataVal.GetType().GetProperty("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    object sharedVal = sharedField != null ? sharedField.GetValue(itemDataVal) : sharedProp?.GetValue(itemDataVal);
                                    if (sharedVal != null)
                                    {
                                        var nameField = sharedVal.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        var nameProp = sharedVal.GetType().GetProperty("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        resName = nameField != null ? nameField.GetValue(sharedVal) as string : nameProp?.GetValue(sharedVal) as string;
                                    }
                                }
                            }

                            int parsedAmount = 0;
                            var fAmount = et.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var pAmount = et.GetProperty("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            object amountObj = fAmount != null ? fAmount.GetValue(elem) : pAmount?.GetValue(elem);

                            var fAmountPerLevel = et.GetField("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var pAmountPerLevel = et.GetProperty("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            object amountPerLevelObj = fAmountPerLevel != null ? fAmountPerLevel.GetValue(elem) : pAmountPerLevel?.GetValue(elem);

                            if (amountObj != null)
                            {
                                try { parsedAmount = Convert.ToInt32(amountObj); } catch { parsedAmount = 0; }
                            }
                            if (parsedAmount <= 0 && amountPerLevelObj != null)
                            {
                                try { parsedAmount = Convert.ToInt32(amountPerLevelObj); } catch { parsedAmount = 0; }
                            }

                            if (!string.IsNullOrEmpty(resName) && parsedAmount > 0) outList.Add((resName, parsedAmount));
                        }
                        catch { }
                        if (idx > 200) break;
                    }
                    return outList;
                }

                try
                {
                    var reqListField = tGui.GetField("m_reqList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (reqListField != null)
                    {
                        var reqListObj = reqListField.GetValue(gui) as IEnumerable;
                        if (reqListObj != null)
                        {
                            var list = ParseRequirementEnumerable(reqListObj);
                            if (list.Count > 0) { requirements = list; return true; }
                        }
                    }
                }
                catch { }

                try
                {
                    var selField = tGui.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (selField != null)
                    {
                        var selVal = selField.GetValue(gui);
                        if (selVal != null)
                        {
                            var wrapperType = selVal.GetType();
                            var candNames = new[] { "m_resources", "m_requirements", "resources", "requirements", "m_recipeResources", "m_resourceList", "m_reqList" };
                            foreach (var name in candNames)
                            {
                                try
                                {
                                    var f = wrapperType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                    if (f != null)
                                    {
                                        var maybe = f.GetValue(selVal) as IEnumerable;
                                        if (maybe != null)
                                        {
                                            var parsed = ParseRequirementEnumerable(maybe);
                                            if (parsed.Count > 0) { requirements = parsed; return true; }
                                        }
                                    }
                                    var p = wrapperType.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                    if (p != null && p.CanRead)
                                    {
                                        var maybe2 = p.GetValue(selVal) as IEnumerable;
                                        if (maybe2 != null)
                                        {
                                            var parsed2 = ParseRequirementEnumerable(maybe2);
                                            if (parsed2.Count > 0) { requirements = parsed2; return true; }
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (TryExtractRecipeFromWrapper(selVal, null, out var innerRecipe, out var path))
                            {
                                var resourcesField2 = innerRecipe?.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var resObj = resourcesField2?.GetValue(innerRecipe) as IEnumerable;
                                var parsed = ParseRequirementEnumerable(resObj);
                                if (parsed.Count > 0) { requirements = parsed; return true; }
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    var fields = tGui.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var f in fields)
                    {
                        try
                        {
                            var val = f.GetValue(gui);
                            if (val is IEnumerable en && !(val is string))
                            {
                                var parsed = ParseRequirementEnumerable(en);
                                if (parsed.Count > 0) { requirements = parsed; return true; }
                            }
                        }
                        catch { }
                    }

                    var props = tGui.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var p in props)
                    {
                        try
                        {
                            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                            var val = p.GetValue(gui);
                            if (val is IEnumerable en2 && !(val is string))
                            {
                                var parsed = ParseRequirementEnumerable(en2);
                                if (parsed.Count > 0) { requirements = parsed; return true; }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                return false;
            }
            catch (Exception ex)
            {
                LogWarning($"TryGetRequirementsFromGui exception: {ex}");
                requirements = null;
                return false;
            }
        }

        #endregion

        #region Upgrade candidate search and detection

        private static Recipe FindBestUpgradeRecipeCandidate(Recipe craftRecipe)
        {
            try
            {
                if (craftRecipe == null) return null;
                string resultName = craftRecipe.m_item?.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(resultName) || ObjectDB.instance == null) return null;

                Recipe best = null;
                int bestScore = int.MaxValue;

                var dbRecipesEnumerable = ObjectDB.instance.m_recipes as IEnumerable;
                if (dbRecipesEnumerable == null) return null;

                foreach (var rObj in dbRecipesEnumerable)
                {
                    var r = rObj as Recipe;
                    if (r == null) continue;
                    try
                    {
                        var rn = r.m_item.m_itemData?.m_shared?.m_name;
                        var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var resourcesObj = resourcesField?.GetValue(r) as IEnumerable;
                        if (resourcesObj == null) continue;

                        int total = 0;
                        bool consumesResult = false;
                        var distinctNames = new List<string>();

                        foreach (var req in resourcesObj)
                        {
                            if (req == null) continue;
                            try
                            {
                                var t = req.GetType();
                                var amountObj = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                int amount = amountObj != null ? Convert.ToInt32(amountObj) : 0;
                                total += Math.Max(0, amount);

                                var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                var itemData = resItem?.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                                var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                var resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                if (!string.IsNullOrEmpty(resName))
                                {
                                    // manual unique-add to avoid HashSet dependency
                                    if (!distinctNames.Any(n => string.Equals(n, resName, StringComparison.OrdinalIgnoreCase)))
                                        distinctNames.Add(resName);
                                    if (string.Equals(resName, resultName, StringComparison.OrdinalIgnoreCase)) consumesResult = true;
                                }
                            }
                            catch { }
                        }

                        bool resultNameMatch = !string.IsNullOrEmpty(rn) && string.Equals(rn, resultName, StringComparison.OrdinalIgnoreCase);

                        int score = total;
                        if (consumesResult) score -= 2000;
                        if (resultNameMatch) score -= 1500;
                        score = score * 10 + distinctNames.Count;

                        bool containsWood = distinctNames.Any(n => n != null && n.IndexOf("wood", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!containsWood) score -= 100;

                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = r;
                        }
                    }
                    catch { }
                }

                return best;
            }
            catch (Exception ex)
            {
                LogWarning($"FindBestUpgradeRecipeCandidate exception: {ex}");
                return null;
            }
        }

        private static bool IsUpgradeOperation(InventoryGui gui, Recipe recipe)
        {
            if (recipe == null || gui == null) return false;

            try
            {
                var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                if (craftUpgradeField != null)
                {
                    var cv = craftUpgradeField.GetValue(gui);
                    if (cv is int v && v > 1) return true;
                }
            }
            catch { }

            try
            {
                var craftedName = recipe.m_item?.m_itemData?.m_shared?.m_name;
                int craftedQuality = recipe.m_item?.m_itemData?.m_quality ?? 0;
                var localPlayer = Player.m_localPlayer;
                if (!string.IsNullOrEmpty(craftedName) && craftedQuality > 0 && localPlayer != null)
                {
                    var inv = localPlayer.GetInventory();
                    if (inv != null)
                    {
                        foreach (var it in inv.GetAllItems())
                        {
                            if (it == null || it.m_shared == null) continue;
                            if (it.m_shared.m_name == craftedName && it.m_quality < craftedQuality) return true;
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (RecipeConsumesResult(recipe)) return true;
            }
            catch { }

            return false;
        }

        #endregion
    }
}