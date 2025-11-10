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
    public class ChanceCraft : BaseUnityPlugin
    {
        public const string pluginID = "deep.ChanceCraft";
        public const string pluginName = "Chance Craft";
        public const string pluginVersion = "1.1.3";

        private Harmony _harmony;

        internal static ConfigEntry<float> weaponSuccessChance;
        internal static ConfigEntry<float> armorSuccessChance;
        internal static ConfigEntry<float> arrowSuccessChance;
        internal static ConfigEntry<float> weaponSuccessUpgrade;
        internal static ConfigEntry<float> armorSuccessUpgrade;
        internal static ConfigEntry<float> arrowSuccessUpgrade;
        internal static ConfigEntry<bool> loggingEnabled;

        // Runtime shared state (exposed to helpers as internal)
        internal static bool IsDoCraft;
        internal static List<ItemDrop.ItemData> _preCraftSnapshot;
        internal static Recipe _snapshotRecipe;
        internal static Dictionary<ItemDrop.ItemData, (int quality, int variant)> _preCraftSnapshotData;
        internal static Dictionary<int, int> _preCraftSnapshotHashQuality;

        internal static List<string> _suppressedRecipeKeys = new List<string>();
        internal static readonly object _suppressedRecipeKeysLock = new object();

        internal static readonly HashSet<string> _recentRemovalKeys = new HashSet<string>();
        internal static readonly object _recentRemovalKeysLock = new object();

        internal static ItemDrop.ItemData _upgradeTargetItem;
        internal static Recipe _upgradeRecipe;
        internal static Recipe _upgradeGuiRecipe;
        internal static bool _isUpgradeDetected;
        internal static int _upgradeTargetItemIndex = -1;
        internal static List<(string name, int amount)> _upgradeGuiRequirements;

        internal static bool VERBOSE_DEBUG = false; // set false when done

        internal static readonly object _savedResourcesLock = new object();

        void Awake()
        {
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), "ChanceCraft");

            loggingEnabled = Config.Bind("Logging", "Logging Enabled", true, "Enable logging");
            weaponSuccessChance = Config.Bind("General", "WeaponSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft weapons (0.0 - 1.0)"));
            armorSuccessChance = Config.Bind("General", "ArmorSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft armors (0.0 - 1.0)"));
            arrowSuccessChance = Config.Bind("General", "ArrowSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft arrows (0.0 - 1.0)"));

            weaponSuccessUpgrade = Config.Bind("General", "WeaponSuccessUpgrade", weaponSuccessChance.Value, new ConfigDescription("Chance to successfully upgrade weapons (0.0 - 1.0)"));
            armorSuccessUpgrade = Config.Bind("General", "ArmorSuccessUpgrade", armorSuccessChance.Value, new ConfigDescription("Chance to successfully upgrade armors (0.0 - 1.0)"));
            arrowSuccessUpgrade = Config.Bind("General", "ArrowSuccessUpgrade", arrowSuccessChance.Value, new ConfigDescription("Chance to successfully upgrade arrows (0.0 - 1.0)"));

            LogInfo($"ChanceCraft loaded: craft weapon={weaponSuccessChance.Value}, armor={armorSuccessChance.Value}, arrow={arrowSuccessChance.Value}; upgrade weapon={weaponSuccessUpgrade.Value}, armor={armorSuccessUpgrade.Value}, arrow={arrowSuccessUpgrade.Value}");
            Game.isModded = true;
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        #region Logging & helpers (exposed as internal so helper classes can call)

        public static void LogWarning(string msg)
        {
            if (loggingEnabled?.Value ?? false) UnityEngine.Debug.LogWarning($"[ChanceCraft] {msg}");
        }

        public static void LogInfo(string msg)
        {
            if (loggingEnabled?.Value ?? false) UnityEngine.Debug.Log($"[ChanceCraft] {msg}");
        }

        public static void LogDebugIf(bool cond, string msg)
        {
            if (cond) LogInfo(msg);
        }

        #endregion

        #region InventoryGui.DoCrafting patch (kept as-is but referencing helpers)

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        static class InventoryGuiDoCraftingPatch
        {
            private static readonly Dictionary<string, object> _savedResources = new Dictionary<string, object>();
            private static readonly object _savedResourcesLockLocal = new object();

            private static string GetRecipeKey(Recipe r)
            {
                if (r == null) return null;
                try
                {
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

                    try
                    {
                        if (value != null)
                        {
                            if (ChanceCraftRecipeHelpers.TryExtractRecipeFromWrapper(value, selectedRecipe, out var extracted, out var path))
                            {
                                _upgradeGuiRecipe = extracted;
                                try
                                {
                                    var keyg = ChanceCraftRecipeHelpers.RecipeFingerprint(extracted);
                                    lock (_suppressedRecipeKeysLock)
                                    {
                                        if (!_suppressedRecipeKeys.Contains(keyg)) _suppressedRecipeKeys.Add(keyg);
                                    }
                                }
                                catch { }
                            }
                            else
                            {
                                var earlyGuiRecipe = ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(__instance);
                                if (earlyGuiRecipe != null)
                                {
                                    _upgradeGuiRecipe = earlyGuiRecipe;
                                    try
                                    {
                                        var keyg = ChanceCraftRecipeHelpers.RecipeFingerprint(earlyGuiRecipe);
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
                                if (ChanceCraftRecipeHelpers.RecipeConsumesResult(candidateEarly)) _isUpgradeDetected = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Prefix early discovery exception: {ex}");
                    }

                    _savedRecipeForCall = selectedRecipe;

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

                    try
                    {
                        if (ChanceCraftUIHelpers.TryGetRequirementsFromGui(__instance, out var guiReqs) && guiReqs != null && guiReqs.Count > 0)
                        {
                            bool guiIndicatesUpgrade = false;

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

                            try
                            {
                                var finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                if (_upgradeTargetItem != null && finalQuality > _upgradeTargetItem.m_quality) guiIndicatesUpgrade = true;
                            }
                            catch { }

                            try { if (ChanceCraftRecipeHelpers.RecipeConsumesResult(selectedRecipe)) guiIndicatesUpgrade = true; } catch { }

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
                                    var selInvItem = ChanceCraftRecipeHelpers.GetSelectedInventoryItem(__instance);
                                    var finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                    if (selInvItem != null && selInvItem.m_shared != null && !string.IsNullOrEmpty(selectedRecipe.m_item?.m_itemData?.m_shared?.m_name))
                                    {
                                        if (selInvItem.m_shared.m_name == selectedRecipe.m_item.m_itemData.m_shared.m_name && selInvItem.m_quality < finalQuality)
                                            selectedInvItemLowerQuality = true;
                                    }
                                }
                                catch { selectedInvItemLowerQuality = false; }

                                bool consumesResult = false;
                                try { if (ChanceCraftRecipeHelpers.RecipeConsumesResult(selectedRecipe)) consumesResult = true; } catch { consumesResult = false; }

                                if (hasGuiMismatch && (explicitCraftUpgrade || selectedInvItemLowerQuality || consumesResult))
                                {
                                    guiIndicatesUpgrade = true;
                                }
                            }
                            catch { /* ignore */ }

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
                                LogInfo("Prefix-DBG: GUI requirement list indicates UPGRADE -> " + dbgJoined);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Prefix try-get-reqs exception: {ex}");
                    }

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
                                _upgradeGuiRecipe = ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(__instance);
                                _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                                _upgradeTargetItem = ChanceCraftRecipeHelpers.GetSelectedInventoryItem(__instance);
                                return;
                            }
                        }
                    }
                    catch { }

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
                                            LogInfo($"Prefix snapshot: found existing {ChanceCraftRecipeHelpers.ItemInfo(it)}");
                                        }
                                    }
                                    lock (typeof(ChanceCraft))
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

                        try
                        {
                            var craftedName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                            int craftedQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                            var selectedInventoryItem = ChanceCraftRecipeHelpers.GetSelectedInventoryItem(__instance);

                            if (selectedInventoryItem != null &&
                                selectedInventoryItem.m_shared != null &&
                                !string.IsNullOrEmpty(craftedName) &&
                                selectedInventoryItem.m_shared.m_name == craftedName &&
                                selectedInventoryItem.m_quality < craftedQuality)
                            {
                                _isUpgradeDetected = true;
                                _upgradeTargetItem = selectedInventoryItem;
                                _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                                _upgradeGuiRecipe = ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
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
                                        _upgradeGuiRecipe = ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                                        _savedRecipeForCall = null;
                                        return;
                                    }
                                }
                            }
                        }
                        catch { }

                        try
                        {
                            if (ChanceCraftRecipeHelpers.RecipeConsumesResult(selectedRecipe))
                            {
                                _isUpgradeDetected = true;
                                _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                                _upgradeGuiRecipe = ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                                _savedRecipeForCall = null;
                                return;
                            }
                        }
                        catch { }

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
                            var key = ChanceCraftRecipeHelpers.RecipeFingerprint(selectedRecipe);
                            lock (_suppressedRecipeKeysLock)
                            {
                                if (!_suppressedRecipeKeys.Contains(key)) _suppressedRecipeKeys.Add(key);
                            }
                        }
                        catch { }

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
                        if (_isUpgradeDetected || ChanceCraftRecipeHelpers.IsUpgradeOperation(__instance, recipeForLogic))
                        {
                            if (_upgradeGuiRecipe == null) _upgradeGuiRecipe = ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(__instance);
                            if (_upgradeTargetItem == null) _upgradeTargetItem = ChanceCraftRecipeHelpers.GetSelectedInventoryItem(__instance);

                            var resultFromTry = TrySpawnCraftEffect(__instance, recipeForLogic, true);
                            if (resultFromTry == null)
                            {
                                try
                                {
                                    if (recipeForLogic != null)
                                    {
                                        var key = ChanceCraftRecipeHelpers.RecipeFingerprint(recipeForLogic);
                                        lock (_suppressedRecipeKeysLock) { _suppressedRecipeKeys.Remove(key); }
                                    }
                                    if (_upgradeGuiRecipe != null)
                                    {
                                        var keyg = ChanceCraftRecipeHelpers.RecipeFingerprint(_upgradeGuiRecipe);
                                        lock (_suppressedRecipeKeysLock) { _suppressedRecipeKeys.Remove(keyg); }
                                    }
                                }
                                catch { }

                                lock (typeof(ChanceCraft))
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
                                lock (typeof(ChanceCraft))
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

        #region TrySpawnCraftEffect and related logic (uses helpers)

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

            bool isUpgradeNow = ShouldTreatAsUpgrade(gui, selectedRecipe, isUpgradeCall);

            float randVal = UnityEngine.Random.value;

            bool suppressedThisOperation = IsDoCraft;
            try
            {
                var key = ChanceCraftRecipeHelpers.RecipeFingerprint(selectedRecipe);
                lock (_suppressedRecipeKeysLock)
                {
                    if (_suppressedRecipeKeys.Contains(key)) suppressedThisOperation = true;
                }
            }
            catch { }

            try
            {
                lock (typeof(ChanceCraft))
                {
                    if (_snapshotRecipe != null && _snapshotRecipe == selectedRecipe && _preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                    {
                        suppressedThisOperation = true;
                    }
                }
            }
            catch { }

            try
            {
                var recipeKeyDbg = selectedRecipe != null ? ChanceCraftRecipeHelpers.RecipeFingerprint(selectedRecipe) : "null";
                float chosenChance = isUpgradeNow ? upgradeChanceAdjusted : craftChanceAdjusted;
                LogInfo($"TrySpawnCraftEffect-DBG: recipe={recipeKeyDbg} itemType={itemType} quality={qualityLevel} craftChance={craftChanceAdjusted:F3} upgradeChance={upgradeChanceAdjusted:F3} chosenChance={chosenChance:F3} rand={randVal:F3} suppressed={suppressedThisOperation} isUpgradeCall={isUpgradeCall}");
            }
            catch { }

            float finalChosenChance = isUpgradeNow ? upgradeChanceAdjusted : craftChanceAdjusted;

            if (randVal <= finalChosenChance)
            {
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
                    var key = ChanceCraftRecipeHelpers.RecipeFingerprint(selectedRecipe);
                    lock (_suppressedRecipeKeysLock)
                    {
                        if (_suppressedRecipeKeys.Contains(key)) suppressedThisOperationLocal = true;
                    }
                }
                catch { }

                try
                {
                    lock (typeof(ChanceCraft))
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
                        var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                        if (ReferenceEquals(recipeToUse, selectedRecipe))
                        {
                            var candidate = ChanceCraftRecipeHelpers.FindBestUpgradeRecipeCandidate(selectedRecipe);
                            if (candidate != null) recipeToUse = candidate;
                        }

                        if (_upgradeTargetItem == null) _upgradeTargetItem = ChanceCraftRecipeHelpers.GetSelectedInventoryItem(gui);

                        try
                        {
                            var recipeKey = recipeToUse != null ? ChanceCraftRecipeHelpers.RecipeFingerprint(recipeToUse) : "null";
                            var targetHash = _upgradeTargetItem != null ? RuntimeHelpers.GetHashCode(_upgradeTargetItem).ToString("X") : "null";
                            var snapshotCount = (_preCraftSnapshot != null) ? _preCraftSnapshot.Count : 0;
                            var snapshotDataCount = (_preCraftSnapshotData != null) ? _preCraftSnapshotData.Count : 0;
                            LogInfo($"TrySpawnCraftEffect-DBG SUCCESS UPGRADE: recipeToUse={recipeKey} targetHash={targetHash} preSnapshotCount={snapshotCount} preSnapshotData={snapshotDataCount}");
                        }
                        catch { }

                        ChanceCraftResourceHelpers.RemoveRequiredResourcesUpgrade(gui, player, recipeToUse, _upgradeTargetItem, true);

                        lock (typeof(ChanceCraft))
                        {
                            _preCraftSnapshot = null;
                            _preCraftSnapshotData = null;
                            _snapshotRecipe = null;
                            _upgradeTargetItemIndex = -1;
                            _preCraftSnapshotHashQuality = null;
                        }

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
                            bool treatAsUpgrade = ShouldTreatAsUpgrade(gui, selectedRecipe, false);
                            if (treatAsUpgrade)
                            {
                                var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                                if (ReferenceEquals(recipeToUse, selectedRecipe))
                                {
                                    var candidate = ChanceCraftRecipeHelpers.FindBestUpgradeRecipeCandidate(selectedRecipe);
                                    if (candidate != null) recipeToUse = candidate;
                                }

                                var targetItem = _upgradeTargetItem ?? ChanceCraftRecipeHelpers.GetSelectedInventoryItem(gui);
                                var targetHashLog = targetItem != null ? RuntimeHelpers.GetHashCode(targetItem).ToString("X") : "null";
                                LogInfo($"TrySpawnCraftEffect-DBG suppressed-success treating as UPGRADE: recipe={ChanceCraftRecipeHelpers.RecipeFingerprint(recipeToUse)} target={targetHashLog}");

                                ChanceCraftResourceHelpers.RemoveRequiredResourcesUpgrade(gui, player, recipeToUse, targetItem, true);
                            }
                            else
                            {
                                ChanceCraftResourceHelpers.RemoveRequiredResources(gui, player, selectedRecipe, true, false);
                            }
                        }
                        catch (Exception ex) { LogWarning($"TrySpawnCraftEffect success removal exception: {ex}"); }

                        ClearCapturedUpgradeGui();
                    }
                    return null;
                }
            }
            else
            {
                if (isUpgradeCall || ChanceCraftRecipeHelpers.IsUpgradeOperation(gui, selectedRecipe) || _isUpgradeDetected || isUpgradeNow)
                {
                    try
                    {
                        try
                        {
                            var recipeKey = selectedRecipe != null ? ChanceCraftRecipeHelpers.RecipeFingerprint(selectedRecipe) : "null";
                            int preSnapshotEntries = _preCraftSnapshotData != null ? _preCraftSnapshotData.Count : 0;
                            LogInfo($"TrySpawnCraftEffect-DBG FAILURE UPGRADE: recipe={recipeKey} preSnapshotData={preSnapshotEntries} upgradeTargetIndex={_upgradeTargetItemIndex}");
                        }
                        catch { }

                        bool didRevertAny = false;
                        lock (typeof(ChanceCraft))
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
                                            if (ChanceCraftRecipeHelpers.TryUnpackQualityVariant(pre, out int pq, out int pv))
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

                                            if (ChanceCraftRecipeHelpers.TryUnpackQualityVariant(pre, out int pq2, out int pv2))
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
                                        var preQs = _preCraftSnapshotData.Values.Select(v => { if (ChanceCraftRecipeHelpers.TryUnpackQualityVariant(v, out int a, out int b)) return a; return 0; }).ToList();
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
                                                if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && ChanceCraftRecipeHelpers.TryUnpackQualityVariant(kv.Value, out int pq3, out int pv3))
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
                                                    if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && ChanceCraftRecipeHelpers.TryUnpackQualityVariant(kv.Value, out int pq4, out int pv4))
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

                            try
                            {
                                ChanceCraftUIHelpers.ForceSimulateTabSwitchRefresh(__instance);
                                try { ChanceCraftUIHelpers.RefreshInventoryGui(__instance); } catch { }
                                try { ChanceCraftUIHelpers.RefreshCraftingPanel(__instance); } catch { }
                                try { __instance?.StartCoroutine(ChanceCraftUIHelpers.DelayedRefreshCraftingPanel(__instance, 1)); } catch { }
                                UnityEngine.Debug.LogWarning("[ChanceCraft] TrySpawnCraftEffect: performed UI refresh after revert attempts.");
                            }
                            catch (Exception exRefresh)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: UI refresh after revert failed: {exRefresh}");
                            }

                            try { Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=red>Upgrade failed!</color>"); } catch { }
                        }

                        try
                        {
                            var target = _upgradeTargetItem ?? ChanceCraftRecipeHelpers.GetSelectedInventoryItem(__instance);
                            var targetHash = target != null ? RuntimeHelpers.GetHashCode(target).ToString("X") : "null";
                            var inv = Player.m_localPlayer?.GetInventory();
                            int woodBefore = 0, scrapBefore = 0, hideBefore = 0;
                            if (inv != null)
                            {
                                var all = inv.GetAllItems();
                                try { woodBefore = all.Where(it => it != null && it.m_shared != null && string.Equals(it.m_shared.m_name, "$item_wood", StringComparison.OrdinalIgnoreCase)).Sum(i => i.m_stack); } catch { }
                                try { scrapBefore = all.Where(it => it != null && it.m_shared != null && string.Equals(it.m_shared.m_name, "$item_leatherscraps", StringComparison.OrdinalIgnoreCase)).Sum(i => i.m_stack); } catch { }
                                try { hideBefore = all.Where(it => it != null && it.m_shared != null && string.Equals(it.m_shared.m_name, "$item_deerhide", StringComparison.OrdinalIgnoreCase)).Sum(i => i.m_stack); } catch { }
                            }
                            LogInfo($"TrySpawnCraftEffect-DBG BEFORE removal: targetHash={targetHash} wood={woodBefore} scraps={scrapBefore} hides={hideBefore} didRevertAny={false}");
                        }
                        catch { }

                        var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                        if (ReferenceEquals(recipeToUse, selectedRecipe))
                        {
                            var candidate = ChanceCraftRecipeHelpers.FindBestUpgradeRecipeCandidate(selectedRecipe);
                            if (candidate != null) recipeToUse = candidate;
                        }

                        var upgradeTarget = _upgradeTargetItem ?? ChanceCraftRecipeHelpers.GetSelectedInventoryItem(__instance);
                        ChanceCraftResourceHelpers.RemoveRequiredResourcesUpgrade(__instance, Player.m_localPlayer, recipeToUse, upgradeTarget, false);

                        try { ChanceCraftUIHelpers.ForceRevertAfterRemoval(__instance, recipeToUse, upgradeTarget); } catch { }

                        lock (typeof(ChanceCraft))
                        {
                            _preCraftSnapshot = null;
                            _preCraftSnapshotData = null;
                            _snapshotRecipe = null;
                            _upgradeTargetItemIndex = -1;
                            _preCraftSnapshotHashQuality = null;
                        }

                        ClearCapturedUpgradeGui();
                    }
                    catch { }

                    return null;
                }
                else
                {
                    try
                    {
                        bool gameAlreadyHandledNormal = false;
                        lock (typeof(ChanceCraft))
                        {
                            if (_snapshotRecipe != null && _snapshotRecipe == selectedRecipe && _preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                            {
                                foreach (var kv in _preCraftSnapshotData)
                                {
                                    var item = kv.Key;
                                    var pre = kv.Value;
                                    if (item == null || item.m_shared == null) continue;
                                    if (ChanceCraftRecipeHelpers.TryUnpackQualityVariant(pre, out int pq6, out int pv6))
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
                            lock (typeof(ChanceCraft))
                            {
                                _preCraftSnapshot = null;
                                _preCraftSnapshotData = null;
                                _snapshotRecipe = null;
                                _upgradeTargetItemIndex = -1;
                                _preCraftSnapshotHashQuality = null;
                            }

                            ClearCapturedUpgradeGui();
                            return null;
                        }
                    }
                    catch { }

                    try { ChanceCraftResourceHelpers.RemoveRequiredResources(__instance, Player.m_localPlayer, selectedRecipe, false, false); } catch { }
                    try { Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=red>Crafting failed!</color>"); } catch { }

                    ClearCapturedUpgradeGui();
                    return selectedRecipe;
                }
            }
        }

        #endregion

        private static bool ShouldTreatAsUpgrade(InventoryGui gui, Recipe selectedRecipe, bool isUpgradeCall)
        {
            try
            {
                if (isUpgradeCall) return true;
                if (_isUpgradeDetected) return true;
                if (ChanceCraftRecipeHelpers.IsUpgradeOperation(gui, selectedRecipe)) return true;

                bool guiHasUpgradeRecipe = false;
                try { guiHasUpgradeRecipe = ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(gui) != null; } catch { guiHasUpgradeRecipe = false; }

                ItemDrop.ItemData target = null;
                try { target = _upgradeTargetItem ?? ChanceCraftRecipeHelpers.GetSelectedInventoryItem(gui); } catch { target = _upgradeTargetItem; }

                if (target == null) return false;

                string recipeResultName = null;
                try { recipeResultName = selectedRecipe?.m_item?.m_itemData?.m_shared?.m_name; } catch { recipeResultName = null; }
                string targetName = null;
                try { targetName = target?.m_shared?.m_name; } catch { targetName = null; }

                if (!string.IsNullOrEmpty(recipeResultName) && !string.IsNullOrEmpty(targetName) &&
                    string.Equals(recipeResultName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    int finalQ = selectedRecipe?.m_item?.m_itemData?.m_quality ?? 0;
                    if (target.m_quality < finalQ) return true;
                }

                if (_upgradeGuiRecipe != null && selectedRecipe != null)
                {
                    try
                    {
                        if (ChanceCraftRecipeHelpers.RecipeFingerprint(_upgradeGuiRecipe) == ChanceCraftRecipeHelpers.RecipeFingerprint(selectedRecipe) && target != null) return true;
                    }
                    catch { }
                }

                return false;
            }
            catch { return false; }
        }
    }
}