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
        public const string pluginVersion = "1.1.5";

        private Harmony _harmony;

        internal static ConfigEntry<float> weaponSuccessChance;
        internal static ConfigEntry<float> armorSuccessChance;
        internal static ConfigEntry<float> arrowSuccessChance;
        internal static ConfigEntry<float> weaponSuccessUpgrade;
        internal static ConfigEntry<float> armorSuccessUpgrade;
        internal static ConfigEntry<float> arrowSuccessUpgrade;
        internal static ConfigEntry<bool> loggingEnabled;

        // Config entries per-line
        private ConfigEntry<float> line1_increase;
        private ConfigEntry<string> line1_item;

        private ConfigEntry<float> line2_increase;
        private ConfigEntry<string> line2_item;

        private ConfigEntry<float> line3_increase;
        private ConfigEntry<string> line3_item;

        private ConfigEntry<float> line4_increase;
        private ConfigEntry<string> line4_item;

        private ConfigEntry<float> line5_increase;
        private ConfigEntry<string> line5_item;

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

        internal static bool VERBOSE_DEBUG = true; // set false when done

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

            // Bind each line under its own group so the config file is clear.
            // The key names match the user's requested "increase craft percentage" and "item prefab".
            line1_increase = Config.Bind(
                "ChanceCraft.Line1",
                "increase craft percentage",
                0.05f,
                new ConfigDescription("Line1: Increase craft percentage (0.05 = 5%). Range 0.0 - 1.0", new AcceptableValueRange<float>(0f, 1f))
            );
            line1_item = Config.Bind(
                "ChanceCraft.Line1",
                "item prefab",
                "KnifeFlint",
                new ConfigDescription("Line1: Valheim prefab name for the item (e.g. KnifeFlint). Leave empty to disable this line.")
            );

            line2_increase = Config.Bind(
                "ChanceCraft.Line2",
                "increase craft percentage",
                0.1f,
                new ConfigDescription("Line2: Increase craft percentage (0.1 = 10%).", new AcceptableValueRange<float>(0f, 1f))
            );
            line2_item = Config.Bind(
                "ChanceCraft.Line2",
                "item prefab",
                string.Empty,
                new ConfigDescription("Line2: Valheim prefab name for the item. Leave empty to disable this line.")
            );

            line3_increase = Config.Bind(
                "ChanceCraft.Line3",
                "increase craft percentage",
                0.15f,
                new ConfigDescription("Line3: Increase craft percentage (0.15 = 15%).", new AcceptableValueRange<float>(0f, 1f))
            );
            line3_item = Config.Bind(
                "ChanceCraft.Line3",
                "item prefab",
                string.Empty,
                new ConfigDescription("Line3: Valheim prefab name for the item. Leave empty to disable this line.")
            );

            line4_increase = Config.Bind(
                "ChanceCraft.Line4",
                "increase craft percentage",
                0.2f,
                new ConfigDescription("Line4: Increase craft percentage (0.2 = 20%).", new AcceptableValueRange<float>(0f, 1f))
            );
            line4_item = Config.Bind(
                "ChanceCraft.Line4",
                "item prefab",
                string.Empty,
                new ConfigDescription("Line4: Valheim prefab name for the item. Leave empty to disable this line.")
            );

            line5_increase = Config.Bind(
                "ChanceCraft.Line5",
                "increase craft percentage",
                0.3f,
                new ConfigDescription("Line5: Increase craft percentage (0.3 = 30%).", new AcceptableValueRange<float>(0f, 1f))
            );
            line5_item = Config.Bind(
                "ChanceCraft.Line5",
                "item prefab",
                string.Empty,
                new ConfigDescription("Line5: Valheim prefab name for the item. Leave empty to disable this line.")
            );

            // Example: log current config at startup
            Logger.LogInfo("ChanceCraft configured lines:");
            foreach (var line in GetConfiguredLines())
            {
                Logger.LogInfo($"- prefab='{line.ItemPrefab ?? "<empty>"}' increase={line.IncreaseCraftPercentage} active={line.IsActive}");
            }

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

        public static void LogDebugIf (bool cond, string msg)
        {
            if (cond) LogInfo(msg);
        }

        private void TryValidatePrefabs()
        {
            try
            {
                if (ZNetScene.instance == null) return;
                foreach (var line in GetConfiguredLines().Where(l => l.IsActive))
                {
                    var pf = ZNetScene.instance.GetPrefab(line.ItemPrefab);
                    if (pf == null)
                    {
                        Logger.LogWarning($"ChanceCraft: configured prefab '{line.ItemPrefab}' not found in ZNetScene. This line will not match at runtime.");
                    }
                }
            }
            catch
            {
                // If ZNetScene isn't available at Awake, skip validation silently.
            }
        }

        #endregion

        // A simple DTO to represent a configured line
        public class ChanceCraftLine
        {
            public float IncreaseCraftPercentage;
            public string ItemPrefab;
            public bool IsActive => !string.IsNullOrWhiteSpace(ItemPrefab);
        }

        // Returns the configured lines in order (line1..line5)
        public List<ChanceCraftLine> GetConfiguredLines()
        {
            return new List<ChanceCraftLine>
        {
            new ChanceCraftLine { IncreaseCraftPercentage = line1_increase.Value, ItemPrefab = line1_item.Value },
            new ChanceCraftLine { IncreaseCraftPercentage = line2_increase.Value, ItemPrefab = line2_item.Value },
            new ChanceCraftLine { IncreaseCraftPercentage = line3_increase.Value, ItemPrefab = line3_item.Value },
            new ChanceCraftLine { IncreaseCraftPercentage = line4_increase.Value, ItemPrefab = line4_item.Value },
            new ChanceCraftLine { IncreaseCraftPercentage = line5_increase.Value, ItemPrefab = line5_item.Value },
        };
        }

        // Computes total extra chance for a given crafted prefab name by summing all matching active lines.
        // Returns value in range [0, 1].
        public float GetAdditionalChanceForPrefab(string craftedPrefab)
        {
            if (string.IsNullOrWhiteSpace(craftedPrefab)) return 0f;

            float sum = 0f;
            foreach (var line in GetConfiguredLines())
            {
                if (!line.IsActive) continue;
                // exact match on prefab name (case-sensitive to match Valheim prefab naming)
                if (line.ItemPrefab == craftedPrefab)
                {
                    sum += line.IncreaseCraftPercentage;
                }
            }
            // clamp to 1.0 max
            return Mathf.Clamp01(sum);
        }

        public float ApplyExtraChanceToBase(string craftedPrefab, float baseChance)
        {
            float extra = GetAdditionalChanceForPrefab(craftedPrefab);
            float result = baseChance + extra;
            return Mathf.Clamp01(result);
        }

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

        // Try to find a prefab name from InventoryGui instance by searching fields/properties for GameObject or ItemDrop references.
        private static string TryGetPrefabNameFromInventoryGui(InventoryGui gui)
        {
            if (gui == null) return null;

            // 1) Check common named fields/properties first (faster, if present)
            var quickNames = new[] { "m_selectedItem", "m_selected", "m_selectedPiece", "m_craftPrefab", "m_currentItem", "m_item" };
            foreach (var name in quickNames)
            {
                var fi = GetField(gui.GetType(), name);
                if (fi != null)
                {
                    var val = fi.GetValue(gui);
                    var nameFromVal = ExtractPrefabNameFromObject(val);
                    if (!string.IsNullOrWhiteSpace(nameFromVal)) return nameFromVal;
                }
                var pi = GetProperty(gui.GetType(), name);
                if (pi != null)
                {
                    var val = pi.GetValue(gui);
                    var nameFromVal = ExtractPrefabNameFromObject(val);
                    if (!string.IsNullOrWhiteSpace(nameFromVal)) return nameFromVal;
                }
            }

            // 2) Search all fields/properties for a GameObject or ItemDrop or item data object and try to extract prefab name.
            // Search fields
            var fields = gui.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var f in fields)
            {
                try
                {
                    var val = f.GetValue(gui);
                    var nameFromVal = ExtractPrefabNameFromObject(val);
                    if (!string.IsNullOrWhiteSpace(nameFromVal)) return nameFromVal;
                }
                catch { /* best-effort */ }
            }

            // Search properties
            var props = gui.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var p in props)
            {
                try
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    var val = p.GetValue(gui);
                    var nameFromVal = ExtractPrefabNameFromObject(val);
                    if (!string.IsNullOrWhiteSpace(nameFromVal)) return nameFromVal;
                }
                catch { /* best-effort */ }
            }

            return null;
        }

        // Try to get ItemType from InventoryGui (best-effort via reflection).
        private static ItemDrop.ItemData.ItemType? TryGetItemTypeFromInventoryGui(InventoryGui gui)
        {
            if (gui == null) return null;

            // Try to locate an ItemDrop.ItemData object in fields/properties
            var fields = gui.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var f in fields)
            {
                try
                {
                    var val = f.GetValue(gui);
                    var itemType = ExtractItemTypeFromObject(val);
                    if (itemType.HasValue) return itemType;
                }
                catch { }
            }

            var props = gui.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var p in props)
            {
                try
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    var val = p.GetValue(gui);
                    var itemType = ExtractItemTypeFromObject(val);
                    if (itemType.HasValue) return itemType;
                }
                catch { }
            }

            return null;
        }

        // Extract prefab name from a candidate object (GameObject, ItemDrop, ItemData, strings).
        private static string ExtractPrefabNameFromObject(object obj)
        {
            if (obj == null) return null;

            // Direct GameObject
            if (obj is GameObject go)
            {
                return go.name.Replace("(Clone)", "");
            }

            // If object has a 'gameObject' property
            var goProp = obj.GetType().GetProperty("gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (goProp != null)
            {
                try
                {
                    var goVal = goProp.GetValue(obj) as GameObject;
                    if (goVal != null) return goVal.name.Replace("(Clone)", "");
                }
                catch { }
            }

            // If object has a field or property 'm_dropPrefab' or 'm_prefab' or 'm_shared' -> m_dropPrefab
            var dropPrefabField = obj.GetType().GetField("m_dropPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (dropPrefabField != null)
            {
                try
                {
                    var pf = dropPrefabField.GetValue(obj) as GameObject;
                    if (pf != null) return pf.name.Replace("(Clone)", "");
                }
                catch { }
            }

            var dropPrefabProp = obj.GetType().GetProperty("m_dropPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (dropPrefabProp != null)
            {
                try
                {
                    var pf = dropPrefabProp.GetValue(obj) as GameObject;
                    if (pf != null) return pf.name.Replace("(Clone)", "");
                }
                catch { }
            }

            // If object is ItemDrop (has field 'm_itemData' or property 'm_itemData') try to dig deeper.
            var itemDataField = obj.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (itemDataField != null)
            {
                try
                {
                    var itemData = itemDataField.GetValue(obj);
                    if (itemData != null)
                    {
                        // try shared drop prefab
                        var sharedDropField = itemData.GetType().GetField("m_dropPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (sharedDropField != null)
                        {
                            var pf = sharedDropField.GetValue(itemData) as GameObject;
                            if (pf != null) return pf.name.Replace("(Clone)", "");
                        }

                        var sharedProp = itemData.GetType().GetProperty("m_dropPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (sharedProp != null)
                        {
                            var pf = sharedProp.GetValue(itemData) as GameObject;
                            if (pf != null) return pf.name.Replace("(Clone)", "");
                        }
                    }
                }
                catch { }
            }

            // If object has a 'name' field or property
            var nameField = obj.GetType().GetField("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (nameField != null)
            {
                try
                {
                    var n = nameField.GetValue(obj) as string;
                    if (!string.IsNullOrWhiteSpace(n)) return n.Replace("(Clone)", "");
                }
                catch { }
            }
            var nameProp = obj.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (nameProp != null)
            {
                try
                {
                    var n = nameProp.GetValue(obj) as string;
                    if (!string.IsNullOrWhiteSpace(n)) return n.Replace("(Clone)", "");
                }
                catch { }
            }

            return null;
        }

        // Extract ItemType from a candidate object (ItemDrop, ItemData, Shared, etc.)
        private static ItemDrop.ItemData.ItemType? ExtractItemTypeFromObject(object obj)
        {
            if (obj == null) return null;

            // If object itself is ItemDrop.ItemData
            if (obj.GetType().Name == "ItemDrop" || obj.GetType().Name == "ItemDrop+ItemData" || obj.GetType().Name == "ItemDrop.ItemData")
            {
                // Try common patterns to get shared -> item type
                var sharedField = obj.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sharedField != null)
                {
                    try
                    {
                        var shared = sharedField.GetValue(obj);
                        if (shared != null)
                        {
                            var itemTypeField = shared.GetType().GetField("m_itemType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (itemTypeField != null)
                            {
                                var val = itemTypeField.GetValue(shared);
                                if (val is ItemDrop.ItemData.ItemType it) return it;
                            }

                            var itemTypeProp = shared.GetType().GetProperty("m_itemType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (itemTypeProp != null)
                            {
                                var val = itemTypeProp.GetValue(shared);
                                if (val is ItemDrop.ItemData.ItemType it2) return it2;
                            }
                        }
                    }
                    catch { }
                }

                // Try a property or field directly on the item data for item type
                var itemTypeFieldDirect = obj.GetType().GetField("m_itemType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (itemTypeFieldDirect != null)
                {
                    try
                    {
                        var val = itemTypeFieldDirect.GetValue(obj);
                        if (val is ItemDrop.ItemData.ItemType it) return it;
                    }
                    catch { }
                }

                var itemTypePropDirect = obj.GetType().GetProperty("m_itemType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (itemTypePropDirect != null)
                {
                    try
                    {
                        var val = itemTypePropDirect.GetValue(obj);
                        if (val is ItemDrop.ItemData.ItemType it) return it;
                    }
                    catch { }
                }
            }

            // If object is GameObject, try to get ItemDrop component and then item data
            if (obj is GameObject go)
            {
                try
                {
                    var itemDrop = go.GetComponent<ItemDrop>();
                    if (itemDrop != null)
                    {
                        var itemDataProp = itemDrop.GetType().GetProperty("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (itemDataProp != null)
                        {
                            var itemData = itemDataProp.GetValue(itemDrop);
                            var res = ExtractItemTypeFromObject(itemData);
                            if (res.HasValue) return res;
                        }
                    }
                }
                catch { }
            }

            // If object has a field/property 'm_itemData' or 'm_shared', try to dig in
            var field = obj.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try
                {
                    var id = field.GetValue(obj);
                    var res = ExtractItemTypeFromObject(id);
                    if (res.HasValue) return res;
                }
                catch { }
            }

            var prop = obj.GetType().GetProperty("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                try
                {
                    var id = prop.GetValue(obj);
                    var res = ExtractItemTypeFromObject(id);
                    if (res.HasValue) return res;
                }
                catch { }
            }

            return null;
        }

        // Helpers to get private fields/properties
        private static FieldInfo GetField(Type t, string name)
        {
            while (t != null)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f;
                t = t.BaseType;
            }
            return null;
        }

        private static PropertyInfo GetProperty(Type t, string name)
        {
            while (t != null)
            {
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p;
                t = t.BaseType;
            }
            return null;
        }

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

            ItemDrop.ItemData.ItemType? itemType = null;
            try
            {
                itemType = selectedRecipe.m_item?.m_itemData?.m_shared?.m_itemType;
                LogDebugIf(VERBOSE_DEBUG, $"Extracted itemType = {(itemType.HasValue ? itemType.Value.ToString() : "<null>")}");
            }
            catch (Exception ex)
            {
                LogWarning("Failed to extract itemType via direct access: " + ex);
                itemType = null;
            }

            // This boolean names the types that the plugin should affect (we apply failure chance to these types).
            bool isEligibleType =
                itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.Bow ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                itemType == ItemDrop.ItemData.ItemType.Shield ||
                itemType == ItemDrop.ItemData.ItemType.Helmet ||
                itemType == ItemDrop.ItemData.ItemType.Chest ||
                itemType == ItemDrop.ItemData.ItemType.Legs ||
                itemType == ItemDrop.ItemData.ItemType.Ammo;

            LogDebugIf(VERBOSE_DEBUG, $"isEligibleType = {isEligibleType}");

            // If the crafted item is not one of the eligible types, this plugin does not apply.
            if (!isEligibleType)
            {
                LogDebugIf(VERBOSE_DEBUG, "Item type is not eligible for ChanceCraft -> skipping plugin logic.");
                return null;
            }

            // total extra chance accumulated from configured helper items found in player's inventory
            float totalModifiedPercentage = 0f;
            bool foundAnyConfiguredMatch = false;

            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<ChanceCraft>();

            // For eligible types, check player's inventory for configured helper prefabs and consume them (best-effort).
            if (plugin == null)
            {
                LogWarning("ChanceCraft plugin instance not found (plugin == null). Can't access configured lines. Skipping inventory boost checks.");
            }
            else
            {
                var playerInv = Player.m_localPlayer;
                if (playerInv == null)
                {
                    LogWarning("Player.m_localPlayer is null; cannot check inventory.");
                }
                else
                {
                    var inv = playerInv.GetInventory();
                    if (inv == null)
                    {
                        LogWarning("Player inventory (GetInventory()) returned null.");
                    }
                    else
                    {
                        // Try to obtain inventory items via GetAllItems() (disambiguate overloads)
                        System.Collections.IEnumerable invItems = null;
                        var getAll = inv.GetType().GetMethod("GetAllItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                        if (getAll != null)
                        {
                            try
                            {
                                invItems = (System.Collections.IEnumerable)getAll.Invoke(inv, null);
                                LogDebugIf(VERBOSE_DEBUG, "Obtained inventory items via GetAllItems().");
                            }
                            catch (Exception ex)
                            {
                                LogWarning("GetAllItems() invocation failed: " + ex);
                                invItems = null;
                            }
                        }

                        // fallback: try common inventory fields
                        if (invItems == null)
                        {
                            var fi = inv.GetType().GetField("m_inventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                     ?? inv.GetType().GetField("m_items", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fi != null)
                            {
                                try
                                {
                                    invItems = fi.GetValue(inv) as System.Collections.IEnumerable;
                                    LogDebugIf(VERBOSE_DEBUG, $"Obtained inventory items via field '{fi.Name}'.");
                                }
                                catch (Exception ex)
                                {
                                    LogWarning("Accessing inventory field failed: " + ex);
                                    invItems = null;
                                }
                            }
                            else
                            {
                                LogDebugIf(VERBOSE_DEBUG, "No inventory field found (m_inventory / m_items).");
                            }
                        }

                        if (invItems == null)
                        {
                            LogWarning("Failed to enumerate inventory items (invItems == null).");
                        }
                        else
                        {
                            // helper: best-effort extract prefab name from inventory object
                            Func<object, string> extractPrefabName = (obj) =>
                            {
                                if (obj == null) return null;
                                try
                                {
                                    var t = obj.GetType();
                                    var sharedField = t.GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    object shared = sharedField != null ? sharedField.GetValue(obj) : null;
                                    if (shared != null)
                                    {
                                        var sharedType = shared.GetType();
                                        var pfField = sharedType.GetField("m_dropPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        if (pfField != null)
                                        {
                                            var pf = pfField.GetValue(shared) as GameObject;
                                            if (pf != null) return pf.name.Replace("(Clone)", "");
                                        }
                                        var pfProp = sharedType.GetProperty("m_dropPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        if (pfProp != null)
                                        {
                                            var pf = pfProp.GetValue(shared) as GameObject;
                                            if (pf != null) return pf.name.Replace("(Clone)", "");
                                        }
                                    }

                                    var pfField2 = t.GetField("m_dropPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (pfField2 != null)
                                    {
                                        var pf = pfField2.GetValue(obj) as GameObject;
                                        if (pf != null) return pf.name.Replace("(Clone)", "");
                                    }
                                    var pfProp2 = t.GetProperty("m_dropPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (pfProp2 != null)
                                    {
                                        var pf = pfProp2.GetValue(obj) as GameObject;
                                        if (pf != null) return pf.name.Replace("(Clone)", "");
                                    }

                                    var goProp = t.GetProperty("gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (goProp != null)
                                    {
                                        var go = goProp.GetValue(obj) as GameObject;
                                        if (go != null) return go.name.Replace("(Clone)", "");
                                    }

                                    var nameField = t.GetField("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (nameField != null)
                                    {
                                        var n = nameField.GetValue(obj) as string;
                                        if (!string.IsNullOrWhiteSpace(n)) return n.Replace("(Clone)", "");
                                    }
                                    var nameProp = t.GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (nameProp != null)
                                    {
                                        var n = nameProp.GetValue(obj) as string;
                                        if (!string.IsNullOrWhiteSpace(n)) return n.Replace("(Clone)", "");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogDebugIf(VERBOSE_DEBUG, "extractPrefabName exception: " + ex);
                                }
                                return null;
                            };

                            // helper to remove one instance (best-effort) from the player's inventory
                            Action<object> removeOne = (invItemObj) =>
                            {
                                try
                                {
                                    var itemTypeInv = invItemObj.GetType();

                                    // 1) Inventory.RemoveItem(itemData, int)
                                    var removeMethod = inv.GetType().GetMethod("RemoveItem", new Type[] { itemTypeInv, typeof(int) });
                                    if (removeMethod != null)
                                    {
                                        try
                                        {
                                            removeMethod.Invoke(inv, new object[] { invItemObj, 1 });
                                            LogDebugIf(VERBOSE_DEBUG, "Removed 1 item (RemoveItem(itemData, int) used).");
                                            return;
                                        }
                                        catch (Exception ex) { LogDebugIf(VERBOSE_DEBUG, "RemoveItem(itemData, int) failed: " + ex); }
                                    }

                                    // 2) Inventory.RemoveItem(string, int)
                                    var sharedField = itemTypeInv.GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    string displayName = null;
                                    if (sharedField != null)
                                    {
                                        try
                                        {
                                            var shared = sharedField.GetValue(invItemObj);
                                            if (shared != null)
                                            {
                                                var nameField = shared.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                                if (nameField != null) displayName = nameField.GetValue(shared) as string;
                                                if (string.IsNullOrWhiteSpace(displayName))
                                                {
                                                    var nameProp = shared.GetType().GetProperty("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                                    if (nameProp != null) displayName = nameProp.GetValue(shared) as string;
                                                }
                                            }
                                        }
                                        catch (Exception ex) { LogDebugIf(VERBOSE_DEBUG, "Failed to read shared.m_name: " + ex); displayName = null; }
                                    }

                                    if (!string.IsNullOrWhiteSpace(displayName))
                                    {
                                        var removeByName = inv.GetType().GetMethod("RemoveItem", new Type[] { typeof(string), typeof(int) });
                                        if (removeByName != null)
                                        {
                                            try
                                            {
                                                removeByName.Invoke(inv, new object[] { displayName, 1 });
                                                LogDebugIf(VERBOSE_DEBUG, $"Removed 1 item by name ('{displayName}') via RemoveItem(string, int).");
                                                return;
                                            }
                                            catch (Exception ex) { LogDebugIf(VERBOSE_DEBUG, "RemoveItem(string,int) failed: " + ex); }
                                        }
                                    }

                                    // 3) Inventory.RemoveItem(itemData)
                                    var removeMethodSimple = inv.GetType().GetMethod("RemoveItem", new Type[] { itemTypeInv });
                                    if (removeMethodSimple != null)
                                    {
                                        try
                                        {
                                            removeMethodSimple.Invoke(inv, new object[] { invItemObj });
                                            LogDebugIf(VERBOSE_DEBUG, "Removed 1 item (RemoveItem(itemData) used).");
                                            return;
                                        }
                                        catch (Exception ex) { LogDebugIf(VERBOSE_DEBUG, "RemoveItem(itemData) failed: " + ex); }
                                    }

                                    // fallback: decrement stack
                                    var stackField = itemTypeInv.GetField("m_stack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (stackField != null)
                                    {
                                        try
                                        {
                                            var stackVal = stackField.GetValue(invItemObj);
                                            if (stackVal is int sv && sv > 1)
                                            {
                                                stackField.SetValue(invItemObj, sv - 1);
                                                LogDebugIf(VERBOSE_DEBUG, "Decremented m_stack by 1 as a fallback removal.");
                                                return;
                                            }
                                            else
                                            {
                                                LogDebugIf(VERBOSE_DEBUG, "m_stack present but not >1; cannot decrement to remove one reliably.");
                                            }
                                        }
                                        catch (Exception ex) { LogWarning("m_stack decrement failed: " + ex); }
                                    }

                                    LogWarning("Could not remove item via any known method; item may remain in inventory.");
                                }
                                catch (Exception ex) { LogDebugIf(VERBOSE_DEBUG, "removeOne outer exception: " + ex); }
                            };

                            var configLines = plugin.GetConfiguredLines();
                            LogDebugIf(VERBOSE_DEBUG, $"Configured lines count: {(configLines == null ? "<null>" : configLines.Count.ToString())}");

                            if (configLines != null)
                            {
                                foreach (var line in configLines)
                                {
                                    if (line == null || !line.IsActive)
                                    {
                                        LogDebugIf(VERBOSE_DEBUG, "Skipping inactive or null config line.");
                                        continue;
                                    }

                                    LogDebugIf(VERBOSE_DEBUG, $"Checking config line: ItemPrefab='{line.ItemPrefab}', IncreaseCraftPercentage={line.IncreaseCraftPercentage}");

                                    object matchedItemObj = null;
                                    int checkedCount = 0;
                                    foreach (var invObj in invItems)
                                    {
                                        checkedCount++;
                                        if (invObj == null) continue;
                                        var invPrefabName = extractPrefabName(invObj);
                                        if (string.IsNullOrWhiteSpace(invPrefabName)) continue;
                                        if (invPrefabName == line.ItemPrefab)
                                        {
                                            matchedItemObj = invObj;
                                            break;
                                        }
                                    }
                                    LogDebugIf(VERBOSE_DEBUG, $"Checked {checkedCount} inventory entries for prefab '{line.ItemPrefab}'.");

                                    if (matchedItemObj != null)
                                    {
                                        totalModifiedPercentage += line.IncreaseCraftPercentage;
                                        foundAnyConfiguredMatch = true;
                                        LogDebugIf(VERBOSE_DEBUG, $"Matched inventory prefab '{line.ItemPrefab}'. Increase +{line.IncreaseCraftPercentage}. Attempting to remove one instance.");
                                        removeOne(matchedItemObj);
                                        LogDebugIf(VERBOSE_DEBUG, $"Attempted removal for '{line.ItemPrefab}'.");
                                    }
                                    else
                                    {
                                        LogDebugIf(VERBOSE_DEBUG, $"No inventory match found for prefab '{line.ItemPrefab}'.");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // No early return when no configured helper items are found; base chance still applies.
            try
            {
                if (plugin != null && totalModifiedPercentage > 0f)
                {
                    LogInfo($"ChanceCraft: total modified percentage from inventory consumed items = {totalModifiedPercentage * 100f}%");
                }
            }
            catch (Exception ex) { LogDebugIf(VERBOSE_DEBUG, "Final logging failed: " + ex); }

            var player = Player.m_localPlayer;

            // Base chances by type
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

            // Apply extra percentage from config (already clamped to reasonable sums in plugin helpers)
            craftChance = Mathf.Clamp01(craftChance + totalModifiedPercentage);

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
                    
                    if (foundAnyConfiguredMatch)
                    {
                        float percent = totalModifiedPercentage * 100f;                        
                        String text = $"<color=yellow>Percentage increased by {percent:0.##}%</color>";
                        LogInfo("Displaying HUD message to player: " + text);

                        if (MessageHud.instance != null)
                        {
                            MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, text);
                        }
                        else if (Player.m_localPlayer != null)
                        {
                            Player.m_localPlayer.Message(MessageHud.MessageType.Center, text);
                        }
                    }
                    return null;
                }
            }
            else
            {
                // Failure path & revert logic kept as-is (unchanged)
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
                                ChanceCraftUIHelpers.ForceSimulateTabSwitchRefresh(gui);
                                try { ChanceCraftUIHelpers.RefreshInventoryGui(gui); } catch { }
                                try { ChanceCraftUIHelpers.RefreshCraftingPanel(gui); } catch { }
                                try { gui?.StartCoroutine(ChanceCraftUIHelpers.DelayedRefreshCraftingPanel(gui, 1)); } catch { }
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
                            var target = _upgradeTargetItem ?? ChanceCraftRecipeHelpers.GetSelectedInventoryItem(gui);
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

                        var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? ChanceCraftRecipeHelpers.GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                        if (ReferenceEquals(recipeToUse, selectedRecipe))
                        {
                            var candidate = ChanceCraftRecipeHelpers.FindBestUpgradeRecipeCandidate(selectedRecipe);
                            if (candidate != null) recipeToUse = candidate;
                        }

                        var upgradeTarget = _upgradeTargetItem ?? ChanceCraftRecipeHelpers.GetSelectedInventoryItem(gui);
                        ChanceCraftResourceHelpers.RemoveRequiredResourcesUpgrade(gui, Player.m_localPlayer, recipeToUse, upgradeTarget, false);

                        try { ChanceCraftUIHelpers.ForceRevertAfterRemoval(gui, recipeToUse, upgradeTarget); } catch { }

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

                    try { ChanceCraftResourceHelpers.RemoveRequiredResources(gui, Player.m_localPlayer, selectedRecipe, false, false); } catch { }
                    string text = "<color=red>Crafting failed!</color>";

                    if (foundAnyConfiguredMatch)
                    {
                        float percent = totalModifiedPercentage * 100f;
                        text = $"<color=red>Crafting failed even percentage increased by {percent:0.##}%</color>";
                        LogInfo("Displaying HUD message to player: " + text);
                    } 

                    if (MessageHud.instance != null)
                    {
                        MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, text);
                    }
                    else if (Player.m_localPlayer != null)
                    {
                        Player.m_localPlayer.Message(MessageHud.MessageType.Center, text);
                    }

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