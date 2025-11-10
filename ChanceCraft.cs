using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

// url = https:////github.com/xzahalko/ChanceCraft/blob/00802f513f538751dc926ed56fb553c671d124bf/ChanceCraft.cs
// ChanceCraft.cs - main plugin class (refactored: UI & Resource helpers moved to separate files)
// Minor adjustments made so helper classes can access shared state.

namespace ChanceCraft
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public class ChanceCraftPlugin : BaseUnityPlugin
    {
        public const string pluginID = "deep.ChanceCraft";
        public const string pluginName = "Chance Craft";
        public const string pluginVersion = "1.1.4";

        private Harmony _harmony;

        // Config entries (keep as public so helper classes can read them)
        public static ConfigEntry<float> weaponSuccessChance;
        public static ConfigEntry<float> armorSuccessChance;
        public static ConfigEntry<float> arrowSuccessChance;
        public static ConfigEntry<float> weaponSuccessUpgrade;
        public static ConfigEntry<float> armorSuccessUpgrade;
        public static ConfigEntry<float> arrowSuccessUpgrade;
        public static ConfigEntry<bool> loggingEnabled;

        // Runtime shared state (made public so helpers can access them)
        public static bool IsDoCraft;
        public static List<ItemDrop.ItemData> _preCraftSnapshot;
        public static Recipe _snapshotRecipe;
        public static Dictionary<ItemDrop.ItemData, (int quality, int variant)> _preCraftSnapshotData;
        public static Dictionary<int, int> _preCraftSnapshotHashQuality;

        // Replace HashSet<string> with List<string> to avoid ISet<> assembly requirement
        public static List<string> _suppressedRecipeKeys = new List<string>();
        public static readonly object _suppressedRecipeKeysLock = new object();

        public static readonly HashSet<string> _recentRemovalKeys = new HashSet<string>();
        public static readonly object _recentRemovalKeysLock = new object();

        public static ItemDrop.ItemData _upgradeTargetItem;
        public static Recipe _upgradeRecipe;
        public static Recipe _upgradeGuiRecipe;
        public static bool _isUpgradeDetected;
        public static int _upgradeTargetItemIndex = -1;
        public static List<(string name, int amount)> _upgradeGuiRequirements;

        public static bool VERBOSE_DEBUG = false; // set false when done

        public static readonly object _savedResourcesLock = new object();

        void Awake()
        {
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), "ChanceCraft");

            loggingEnabled = Config.Bind("Logging", "Logging Enabled", true, "Enable logging");
            weaponSuccessChance = Config.Bind("General", "WeaponSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft weapons (0.0 - 1.0)"));
            armorSuccessChance = Config.Bind("General", "ArmorSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft armors (0.0 - 1.0)"));
            arrowSuccessChance = Config.Bind("General", "ArrowSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft arrows (0.0 - 1.0)"));

            // New upgrade-specific chances (separate from crafting)
            weaponSuccessUpgrade = Config.Bind("General", "WeaponSuccessUpgrade", weaponSuccessChance.Value, new ConfigDescription("Chance to successfully upgrade weapons (0.0 - 1.0)"));
            armorSuccessUpgrade = Config.Bind("General", "ArmorSuccessUpgrade", armorSuccessChance.Value, new ConfigDescription("Chance to successfully upgrade armors (0.0 - 1.0)"));
            arrowSuccessUpgrade = Config.Bind("General", "ArrowSuccessUpgrade", arrowSuccessChance.Value, new ConfigDescription("Chance to successfully upgrade arrows (0.0 - 1.0)"));

            LogInfo($"ChanceCraft loaded: craft weapon={{weaponSuccessChance.Value}}, armor={{armorSuccessChance.Value}}, arrow={{arrowSuccessChance.Value}}; upgrade weapon={{weaponSuccessUpgrade.Value}},[...] ");
            Game.isModded = true;
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        #region Logging & small helpers (kept here so method calls don't require many namespace changes)

        private static void LogWarning(string msg)
        {
            if (loggingEnabled?.Value ?? false) UnityEngine.Debug.LogWarning($"[ChanceCraft] {{msg}}");
        }

        private static void LogInfo(string msg)
        {
            if (loggingEnabled?.Value ?? false) UnityEngine.Debug.Log($"[ChanceCraft] {{msg}}");
        }

        private static string ItemInfo(ItemDrop.ItemData it)
        {
            if (it == null) return "<null>";
            try
            {
                return $"[{{it.GetHashCode():X8}}] name='{{it.m_shared?.m_name}}' q={{it.m_quality}} v={{it.m_variant}} stack={{it.m_stack}}";
            }
            catch
            {
                return "<bad item>";
            }
        }

        public static string RecipeInfo(Recipe r)
        {
            if (r == null) return "<null>";
            try
            {
                var name = r.m_item?.m_itemData?.m_shared?.m_name ?? "<no-result>";
                int amount = r.m_amount;
                var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (resourcesField == null) return $"Recipe(result='{{name}}', amount={{amount}})";
                var res = resourcesField.GetValue(r) as IEnumerable;
                if (res == null) return $"Recipe(result='{{name}}', amount={{amount}})";
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
                        parts.Add($"{{resName ?? "<unknown>"}}:{{amt}});
                    }
                    catch { parts.Add("<res-err>"); }
                }
                return $"Recipe(result='{{name}}', amount={{amount}}, resources=[{{string.Join(", ", parts)}}])";
            }
            catch { return "<bad recipe>"; }
        }

        public static string RecipeFingerprint(Recipe r)
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
                            parts.Add($"{{rname ?? "<unknown>"}}:{{reqAmt}});
                        }
                        catch { parts.Add("<res-err>"); }
                    }
                }
                return $"{{name}}|{{r.m_amount}}|{{string.Join(",", parts)}}";
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

        public static ItemDrop.ItemData GetSelectedInventoryItem(InventoryGui gui)
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

        private class WrapperQueueNode
        {
            public object Obj;
            public string Path;
            public int Depth;
            public WrapperQueueNode(object obj, string path, int depth) { Obj = obj; Path = path; Depth = depth; }
        }

        // A small compatibility wrapper that delegates to UIHelpers.TryExtractRecipeFromWrapper (moved)
        private static bool TryExtractRecipeFromWrapper(object wrapper, Recipe excludeRecipe, out Recipe foundRecipe, out string foundPath, int maxDepth = 3)
        {
            return ChanceCraftUIHelpers.TryExtractRecipeFromWrapper(wrapper, excludeRecipe, out foundRecipe, out foundPath, maxDepth);
        }

        private static Recipe GetUpgradeRecipeFromGui(InventoryGui gui)
        {
            return ChanceCraftUIHelpers.GetUpgradeRecipeFromGui(gui);
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
                    return $"{{name}}|q{{quality}}|v{{variant}}";
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
                            var candidateEarly = ChanceCraftRecipeHelpers.FindBestUpgradeRecipeCandidate(selectedRecipe);
                            if (candidateEarly != null)
                            {
                                _upgradeRecipe = candidateEarly;
                                if (RecipeConsumesResult(candidateEarly)) _isUpgradeDetected = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Prefix early discovery exception: {{ex}});
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
                        if (ChanceCraftUIHelpers.TryGetRequirementsFromGui(__instance, out var guiReqs) && guiReqs != null && guiReqs.Count > 0)
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

                            if (guiIndicatesUpgrade)
                            {
                                int levelsToUpgrade = 1;
                                try
                                {
                                    Recipe recipeForQuality = _upgradeGuiRecipe ?? ChanceCraftRecipeHelpers.FindBestUpgradeRecipeCandidate(selectedRecipe) ?? selectedRecipe;
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
                                var dbCandidate = ChanceCraftRecipeHelpers.FindBestUpgradeRecipeCandidate(selectedRecipe);
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
                                LogInfo($"Prefix-DBG: GUI requirement list indicates UPGRADE -> {{dbgJoined}});
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Prefix try-get-reqs exception: {{ex}});
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
                                            LogInfo($"Prefix snapshot: found existing {{ItemInfo(it)}});
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
                        LogWarning($"Prefix snapshot/build exception: {{ex}});
                    }
                }
                catch (Exception ex)
                {
                    LogWarning($"Prefix outer exception: {{ex}});
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
                        if (_isUpgradeDetected || ChanceCraftRecipeHelpers.IsUpgradeOperation(__instance, recipeForLogic))
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
                                LogWarning($"Postfix removal exception: {{ex}});
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Postfix logic exception: {{ex}});
                    }
                }
                catch (Exception ex)
                {
                    LogWarning($"Postfix outer exception: {{ex}});
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;
                }
            }
        }

        #endregion

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

        #region TrySpawnCraftEffect and related logic (kept in main file)

        // NOTE: This method still calls helper classes for some chores (resource removal, UI refresh, recipe helpers).
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

            // Determine craft chance and upgrade chance separately
            float craftChance = 0.6f;
            float upgradeChance = 0.6f;

            if (itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.Bow ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft)
            {
                craftChance = weaponSuccessChance.Value;
                upgradeChance = weaponSuccessUpgrade.Value;
            }
            else if (itemType == ItemDrop.ItemData.ItemType.Shield ||
                     itemType == ItemDrop.ItemData.ItemType.Helmet ||
                     itemType == ItemDrop.ItemData.ItemType.Chest ||
                     itemType == ItemDrop.ItemData.ItemType.Legs)
            {
                craftChance = armorSuccessChance.Value;
                upgradeChance = armorSuccessUpgrade.Value;
            }
            else if (itemType == ItemDrop.ItemData.ItemType.Ammo)
            {
                craftChance = arrowSuccessChance.Value;
                upgradeChance = arrowSuccessUpgrade.Value;
            }

            int qualityLevel = selectedRecipe.m_item?.m_itemData?.m_quality ?? 1;
            float qualityScalePerLevel = 0.05f;
            float qualityFactor = 1f + qualityScalePerLevel * Math.Max(0, qualityLevel - 1);

            float craftChanceAdjusted = Mathf.Clamp01(craftChance * qualityFactor);
            float upgradeChanceAdjusted = Mathf.Clamp01(upgradeChance * qualityFactor);

            // Use consistent, conservative helper for upgrade detection
            bool isUpgradeNow = ChanceCraftRecipeHelpers.ShouldTreatAsUpgrade(gui, selectedRecipe, isUpgradeCall);

            float randVal = UnityEngine.Random.value;

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
                float chosenChance = isUpgradeNow ? upgradeChanceAdjusted : craftChanceAdjusted;
                LogInfo($"TrySpawnCraftEffect-DBG: recipe={{recipeKeyDbg}} itemType={{itemType}} quality={{qualityLevel}} craftChance={{craftChanceAdjusted:F3}} upgradeChance={{upgradeChanceAdjusted:F3}} [...] ");
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
                try { guiHasUpgradeRecipe = ChanceCraftUIHelpers.GetUpgradeRecipeFromGui(gui) != null; } catch { guiHasUpgradeRecipe = false; }
                LogInfo($"TSCE-DBG-DETAIL: isUpgradeCall={{isUpgradeCall}} _isUpgradeDetected={{_isUpgradeDetected}} IsUpgradeOperation={{ChanceCraftRecipeHelpers.IsUpgradeOperation(gui, selectedReci[...]" 
            }
            catch { }

            float finalChosenChance = isUpgradeNow ? upgradeChanceAdjusted : craftChanceAdjusted;

            if (randVal <= finalChosenChance)
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
                        var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? ChanceCraftUIHelpers.GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                        if (ReferenceEquals(recipeToUse, selectedRecipe))
                        {
                            var candidate = ChanceCraftRecipeHelpers.FindBestUpgradeRecipeCandidate(selectedRecipe);
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
                            LogInfo($"TrySpawnCraftEffect-DBG SUCCESS UPGRADE: recipeToUse={{recipeKey}} targetHash={{targetHash}} preSnapshotCount={{snapshotCount}} preSnapshotData={{snapshotDataCount}});
                        }
                        catch { }

                        ChanceCraftResourceHelpers.RemoveRequiredResourcesUpgrade(gui, player, recipeToUse, _upgradeTargetItem, true);

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
                        LogWarning($"TrySpawnCraftEffect success/upgrade removal exception: {{ex}});
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
                            bool treatAsUpgrade = ChanceCraftRecipeHelpers.ShouldTreatAsUpgrade(gui, selectedRecipe, false);
                            if (treatAsUpgrade)
                            {
                                var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? ChanceCraftUIHelpers.GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                                if (ReferenceEquals(recipeToUse, selectedRecipe))
                                {
                                    var candidate = ChanceCraftRecipeHelpers.FindBestUpgradeRecipeCandidate(selectedRecipe);
                                    if (candidate != null) recipeToUse = candidate;
                                }

                                var targetItem = _upgradeTargetItem ?? GetSelectedInventoryItem(gui);
                                var targetHashLog = targetItem != null ? RuntimeHelpers.GetHashCode(targetItem).ToString("X") : "null";
                                LogInfo($"TrySpawnCraftEffect-DBG suppressed-success treating as UPGRADE: recipe={{RecipeFingerprint(recipeToUse)}} target={{targetHashLog}});

                                ChanceCraftResourceHelpers.RemoveRequiredResourcesUpgrade(gui, player, recipeToUse, targetItem, true);
                            }
                            else
                            {
                                ChanceCraftResourceHelpers.RemoveRequiredResources(gui, player, selectedRecipe, true, false);
                            }
                        }
                        catch (Exception ex) { LogWarning($"TrySpawnCraftEffect success removal exception: {{ex}}); }

                        // Clear GUI-captured upgrade state after this branch so stale GUI data won't persist.
                        ClearCapturedUpgradeGui();
                    }
                    return null;
                }
            }
            else
            {
                // FAILURE
                if (isUpgradeCall || ChanceCraftRecipeHelpers.IsUpgradeOperation(gui, selectedRecipe) || _isUpgradeDetected || isUpgradeNow)
                {
                    try
                    {
                        // Diagnostic: show failure + pre-snapshot state
                        try
                        {
                            var recipeKey = selectedRecipe != null ? RecipeFingerprint(selectedRecipe) : "null";
                            int preSnapshotEntries = _preCraftSnapshotData != null ? _preCraftSnapshotData.Count : 0;
                            LogInfo($"TrySpawnCraftEffect-DBG FAILURE UPGRADE: recipe={{recipeKey}} preSnapshotData={{preSnapshotEntries}} upgradeTargetIndex={{_upgradeTargetItemIndex}});
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
                                            if (ChanceCraftResourceHelpers.TryUnpackQualityVariant(pre, out int pq, out int pv))
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

                                            if (ChanceCraftResourceHelpers.TryUnpackQualityVariant(pre, out int pq2, out int pv2))
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
                                        var preQs = _preCraftSnapshotData.Values.Select(v => { if (ChanceCraftResourceHelpers.TryUnpackQualityVariant(v, out int a, out int b)) return a; return 0; });
                                        // additional logic using preQs if needed
                                    }
                                }
                                catch { }
                            }
                        }
                        catch (Exception revertEx)
                        {
                            LogWarning($"Revert operations failed: {{revertEx}});
                        }
                    }
                }
                return null;
            }
        }

        #endregion
    }
}
