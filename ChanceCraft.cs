using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using static HitData;

namespace ChanceCraft
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public class ChanceCraft : BaseUnityPlugin
    {
        private static ConfigEntry<float> weaponSuccessChance;
        private static ConfigEntry<float> armorSuccessChance;
        private static ConfigEntry<float> arrowSuccessChance;

        public const string pluginID = "deep.ChanceCraft";
        public const string pluginName = "Chance Craft";
        public const string pluginVersion = "1.1.0";

        private Harmony _harmony;
        private static bool IsDoCraft;

        // Snapshot & upgrade detection state (single-call snapshot)
        private static HashSet<ItemDrop.ItemData> _preCraftSnapshot = null;
        private static Recipe _snapshotRecipe = null;

        // NEW: store pre-craft qualities so we can reliably detect in-place upgrades
        private static Dictionary<ItemDrop.ItemData, (int quality, int variant)> _preCraftSnapshotData = null;

        // Track suppressed logical recipes across GUI reopenings by a fingerprint
        private static HashSet<string> _suppressedRecipeKeys = new HashSet<string>();
        // Lock to protect access to _suppressedRecipeKeys
        private static readonly object _suppressedRecipeKeysLock = new object();

        private static ItemDrop.ItemData _upgradeTargetItem = null;   // exact inventory item being upgraded (if detected)
        private static Recipe _upgradeRecipe = null;
        private static Recipe _upgradeGuiRecipe = null;
        private static bool _isUpgradeDetected = false;

        private static ConfigEntry<bool> _loggingEnabled;

        [UsedImplicitly]
        void Awake()
        {
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), "ChanceCraft");

            _loggingEnabled = Config.Bind("Logging", "Logging Enabled", true, "Enable logging");
            weaponSuccessChance = Config.Bind("General", "WeaponSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft weapons (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));
            armorSuccessChance = Config.Bind("General", "ArmorSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft armors (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));
            arrowSuccessChance = Config.Bind("General", "ArrowSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft arrows (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));

            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting weapons success chance set to {weaponSuccessChance.Value * 100}%.");
            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting armors success chance set to {armorSuccessChance.Value * 100}%.");
            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting arrow success chance set to {arrowSuccessChance.Value * 100}%.");

            UnityEngine.Debug.LogWarning("[ChanceCraft] Awake called!");
            Game.isModded = true;
        }

        [UsedImplicitly]
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        // Helper for readable item logging
        private static string ItemInfo(ItemDrop.ItemData it)
        {
            if (it == null) return "<null>";
            try
            {
                return $"[{it.GetHashCode():X8}] name='{it.m_shared?.m_name}' q={it.m_quality} v={it.m_variant} stack={it.m_stack}";
            }
            catch { return "<bad item>"; }
        }

        // Helper for recipe logging
        private static string RecipeInfo(Recipe r)
        {
            if (r == null) return "<null>";
            try
            {
                var name = r.m_item?.m_itemData?.m_shared?.m_name ?? "<no-result>";
                var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                string resList = "";
                if (resourcesField != null)
                {
                    var resObj = resourcesField.GetValue(r) as System.Collections.IEnumerable;
                    if (resObj != null)
                    {
                        var parts = new List<string>();
                        foreach (var req in resObj)
                        {
                            try
                            {
                                var t = req.GetType();
                                var amount = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req) ?? "?";
                                var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                string resName = null;
                                try
                                {
                                    var itemData = resItem?.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                                    var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                    resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                }
                                catch { }
                                parts.Add($"{resName ?? "<unknown>"}:{amount}");
                            }
                            catch { parts.Add("<res-err>"); }
                        }
                        resList = string.Join(", ", parts);
                    }
                }
                return $"Recipe(result='{name}', amount={r.m_amount}, resources=[{resList}])";
            }
            catch
            {
                return "<bad recipe>";
            }
        }

        // Create a compact fingerprint for a recipe that is stable across different Recipe instances
        private static string RecipeFingerprint(Recipe r)
        {
            if (r == null || r.m_item == null) return "<null>";
            try
            {
                var name = r.m_item.m_itemData?.m_shared?.m_name ?? "<no-result>";
                var amount = r.m_amount;
                var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var resObj = resourcesField?.GetValue(r) as System.Collections.IEnumerable;
                var parts = new List<string>();
                if (resObj != null)
                {
                    foreach (var req in resObj)
                    {
                        try
                        {
                            var t = req.GetType();
                            var amountObj = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                            int reqAmount = amountObj != null ? Convert.ToInt32(amountObj) : 0;
                            var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                            var itemData = resItem?.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                            var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                            var rname = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                            parts.Add($"{rname ?? "<unknown>"}:{reqAmount}");
                        }
                        catch { parts.Add("<res-err>"); }
                    }
                }
                return $"{name}|{amount}|{string.Join(",", parts)}";
            }
            catch { return "<fingerprint-err>"; }
        }

        // Helper: detect when a recipe consumes the same item it creates (common for upgrades)
        private static bool RecipeConsumesResult(Recipe recipe)
        {
            if (recipe == null || recipe.m_item == null) return false;
            try
            {
                var craftedName = recipe.m_item.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(craftedName)) return false;

                var resourcesField = recipe.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (resourcesField == null) return false;
                var resourcesObj = resourcesField.GetValue(recipe) as System.Collections.IEnumerable;
                if (resourcesObj == null) return false;

                foreach (var req in resourcesObj)
                {
                    if (req == null) continue;
                    try
                    {
                        var t = req.GetType();
                        var resItemField = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var resItem = resItemField?.GetValue(req);
                        var itemDataField = resItem?.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var itemData = itemDataField?.GetValue(resItem);
                        var sharedField = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var shared = sharedField?.GetValue(itemData);
                        var nameField = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var resName = nameField?.GetValue(shared) as string;
                        if (!string.IsNullOrEmpty(resName) && string.Equals(resName, craftedName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        // ignore malformed resource entry
                    }
                }
            }
            catch
            {
                // ignore
            }
            return false;
        }

        // Inserted helper: detect whether the current recipe invocation is an upgrade operation
        private static bool IsUpgradeOperation(InventoryGui gui, Recipe recipe)
        {
            if (recipe == null || gui == null) return false;

            try
            {
                // 1) explicit GUI flag (m_craftUpgrade > 1)
                var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                if (craftUpgradeField != null)
                {
                    try
                    {
                        object cv = craftUpgradeField.GetValue(gui);
                        if (cv is int v && v > 1)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation: m_craftUpgrade={v} -> upgrade detected");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation craftUpgrade check exception: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation: craftUpgrade reflection outer exception: {ex}");
            }

            // 2) inventory-based detection: look for a lower-quality item of the same result in player's inventory
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
                            if (it.m_shared.m_name == craftedName && it.m_quality < craftedQuality)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation: found lower-quality inventory item {ItemInfo(it)} -> upgrade detected");
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation inventory check exception: {ex}");
            }

            // 3) recipe consumes its own result (some upgrade recipes explicitly list the item as a requirement)
            try
            {
                if (RecipeConsumesResult(recipe))
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] IsUpgradeOperation: recipe consumes its result -> upgrade detected.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation RecipeConsumesResult exception: {ex}");
            }

            return false;
        }

        // Try to read the selected inventory item from InventoryGui using candidate fields
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

            var candidates = new[] { "m_selectedItem", "m_selected", "m_selectedItemData", "m_currentItem", "m_selectedInventoryItem", "m_selectedSlot", "m_selectedIndex" };
            foreach (var c in candidates)
            {
                try
                {
                    var val = TryGet(c);
                    if (val is ItemDrop.ItemData idata)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] GetSelectedInventoryItem: got ItemData from field '{c}': {ItemInfo(idata)}");
                        return idata;
                    }
                    if (val != null)
                    {
                        var valType = val.GetType();
                        var innerField = valType.GetField("m_item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (innerField != null)
                        {
                            var inner = innerField.GetValue(val);
                            if (inner is ItemDrop.ItemData ii)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] GetSelectedInventoryItem: got inner ItemData from '{c}.m_item': {ItemInfo(ii)}");
                                return ii;
                            }
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
                        if (idx >= 0 && idx < all.Count)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] GetSelectedInventoryItem: inferred selected by slot idx {idx}: {ItemInfo(all[idx])}");
                            return all[idx];
                        }
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        // Try to read an upgrade recipe object from InventoryGui
        private static Recipe GetUpgradeRecipeFromGui(InventoryGui gui)
        {
            if (gui == null) return null;
            try
            {
                var t = typeof(InventoryGui);
                var names = new[] { "m_upgradeRecipe", "m_selectedUpgradeRecipe", "m_selectedRecipe", "m_currentRecipe", "m_selectedUpgrade" };
                foreach (var name in names)
                {
                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null)
                    {
                        var val = f.GetValue(gui);
                        if (val is Recipe r)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: found Recipe in field '{name}': {RecipeInfo(r)}");
                            return r;
                        }
                        var p = val?.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (p != null)
                        {
                            var r2 = p.GetValue(val) as Recipe;
                            if (r2 != null)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: found Recipe via '{name}.Recipe': {RecipeInfo(r2)}");
                                return r2;
                            }
                        }
                    }

                    var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(gui);
                        if (val is Recipe r2)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: found Recipe in property '{name}': {RecipeInfo(r2)}");
                            return r2;
                        }
                    }
                }
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui exception: {ex}"); }
            return null;
        }

        // --- InventoryGui.DoCrafting patch and helpers ---
        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        static class InventoryGuiDoCraftingPatch
        {
            private static readonly Dictionary<Recipe, object> _savedResources = new Dictionary<Recipe, object>();

            // Per-call state
            private static bool _suppressedThisCall = false;
            private static Recipe _savedRecipeForCall = null;

            [UsedImplicitly]
            static void Prefix(InventoryGui __instance)
            {
                _suppressedThisCall = false;
                _savedRecipeForCall = null;

                // reset upgrade/snapshot per call
                _isUpgradeDetected = false;
                _upgradeTargetItem = null;
                _upgradeRecipe = null;
                _upgradeGuiRecipe = null;
                _preCraftSnapshot = null;
                _preCraftSnapshotData = null;
                _snapshotRecipe = null;

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

                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: selectedRecipe = {RecipeInfo(selectedRecipe)}");

                    // Save exact recipe instance for the call (so Postfix uses same object)
                    _savedRecipeForCall = selectedRecipe;

                    // 1) Fast-path: check InventoryGui.m_craftUpgrade if available (explicit upgrade multiplier)
                    var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (craftUpgradeField != null)
                    {
                        try
                        {
                            object cv = craftUpgradeField.GetValue(__instance);
                            if (cv is int v && v > 1)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: detected explicit m_craftUpgrade={v} - treating as upgrade, skipping suppression.");
                                _savedRecipeForCall = null;
                                _isUpgradeDetected = true;
                                _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance);
                                _upgradeRecipe = selectedRecipe;
                                _upgradeTargetItem = GetSelectedInventoryItem(__instance);
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: captured _upgradeGuiRecipe={RecipeInfo(_upgradeGuiRecipe)} _upgradeTargetItem={ItemInfo(_upgradeTargetItem)}");
                                return;
                            }
                        }
                        catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix craftUpgrade check exception: {ex}"); }
                    }

                    // Build positive resource list and snapshot inventory items of the result type early
                    var resourcesField = selectedRecipe.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (resourcesField == null) return;
                    var resourcesObj = resourcesField.GetValue(selectedRecipe) as System.Collections.IEnumerable;
                    if (resourcesObj == null) return;
                    var resourceList = resourcesObj.Cast<object>().ToList();

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
                    object GetNested(object obj, params string[] names)
                    {
                        object cur = obj;
                        foreach (var n in names)
                        {
                            if (cur == null) return null;
                            cur = GetMember(cur, n);
                        }
                        return cur;
                    }
                    int ToInt(object v)
                    {
                        if (v == null) return 0;
                        try { return Convert.ToInt32(v); } catch { return 0; }
                    }

                    // Snapshot existing inventory entries for crafted name so we can distinguish new items later.
                    try
                    {
                        var craftedName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                        var localPlayer = Player.m_localPlayer;
                        if (!string.IsNullOrEmpty(craftedName) && localPlayer != null)
                        {
                            var inv = localPlayer.GetInventory();
                            if (inv != null)
                            {
                                var existing = new HashSet<ItemDrop.ItemData>();
                                var existingData = new Dictionary<ItemDrop.ItemData, (int quality, int variant)>();
                                foreach (var it in inv.GetAllItems())
                                {
                                    if (it == null || it.m_shared == null) continue;
                                    if (it.m_shared.m_name == craftedName)
                                    {
                                        // record the instance reference for Postfix removal logic
                                        existing.Add(it);
                                        // record the pre-craft quality & variant for reliable in-place-upgrade detection
                                        existingData[it] = (it.m_quality, it.m_variant);
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: snapshot contains {ItemInfo(it)} (recorded q={it.m_quality} v={it.m_variant})");
                                    }
                                }
                                lock (typeof(ChanceCraft))
                                {
                                    _preCraftSnapshot = existing;
                                    _preCraftSnapshotData = existingData;
                                    _snapshotRecipe = selectedRecipe;
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: snapshot stored for recipe {RecipeInfo(selectedRecipe)} with {existing.Count} matching items");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix snapshot exception: {ex}"); }

                    // Inventory-based upgrade detection: try to capture the exact selected inventory item first
                    try
                    {
                        var craftedName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                        int craftedQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                        var localPlayer = Player.m_localPlayer;

                        var selectedInventoryItem = GetSelectedInventoryItem(__instance);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: selectedInventoryItem candidate = {ItemInfo(selectedInventoryItem)}");

                        if (selectedInventoryItem != null &&
                            selectedInventoryItem.m_shared != null &&
                            !string.IsNullOrEmpty(craftedName) &&
                            selectedInventoryItem.m_shared.m_name == craftedName &&
                            selectedInventoryItem.m_quality < craftedQuality)
                        {
                            UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: exact selected inventory item detected as upgrade target.");
                            _isUpgradeDetected = true;
                            _upgradeTargetItem = selectedInventoryItem;
                            _upgradeRecipe = selectedRecipe;
                            _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                            _savedRecipeForCall = null;
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: captured upgrade target {ItemInfo(_upgradeTargetItem)} upgradeGuiRecipe={RecipeInfo(_upgradeGuiRecipe)}");
                            return;
                        }

                        if (!string.IsNullOrEmpty(craftedName) && craftedQuality > 0 && localPlayer != null)
                        {
                            var inv = localPlayer.GetInventory();
                            if (inv != null)
                            {
                                foreach (var it in inv.GetAllItems())
                                {
                                    if (it == null || it.m_shared == null) continue;
                                    if (it.m_shared.m_name == craftedName && it.m_quality < craftedQuality)
                                    {
                                        UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: detected lower-quality item in inventory - treating as upgrade, skipping suppression.");
                                        _isUpgradeDetected = true;
                                        _upgradeTargetItem = it;
                                        _upgradeRecipe = selectedRecipe;
                                        _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                                        _savedRecipeForCall = null;
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: captured upgrade target {ItemInfo(it)} and upgradeGuiRecipe={RecipeInfo(_upgradeGuiRecipe)}");
                                        return;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix upgrade detection exception: {ex}"); }

                    // 3) Recipe-consumes-result check (explicitly treat recipes that consume their result as upgrades)
                    try
                    {
                        if (RecipeConsumesResult(selectedRecipe))
                        {
                            UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: recipe consumes result item - treating as upgrade, skipping suppression.");
                            _isUpgradeDetected = true;
                            _upgradeRecipe = selectedRecipe;
                            _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                            _savedRecipeForCall = null;
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: captured upgradeGuiRecipe={RecipeInfo(_upgradeGuiRecipe)}");
                            return;
                        }
                    }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix RecipeConsumesResult exception: {ex}"); }

                    var validReqs = new List<object>();
                    foreach (var req in resourceList)
                    {
                        var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                        string rname = nameObj as string;
                        if (string.IsNullOrEmpty(rname)) continue;
                        int ramount = ToInt(GetMember(req, "m_amount"));
                        if (ramount <= 0) continue;
                        validReqs.Add(req);
                    }

                    // Only suppress multi-resource recipes (we implement keep-one-on-fail)
                    if (validReqs.Count <= 1)
                    {
                        UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: recipe has <=1 valid req -> skipping suppression.");
                        return;
                    }

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

                    if (!isEligible)
                    {
                        UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: item type not eligible -> skipping suppression.");
                        return;
                    }

                    // Save original resources collection for later restore (keyed by Recipe instance)
                    lock (_savedResources)
                    {
                        if (!_savedResources.ContainsKey(selectedRecipe))
                            _savedResources[selectedRecipe] = resourcesObj;
                    }

                    // IMPORTANT: compute and record fingerprint BEFORE we modify the recipe's m_resources
                    try
                    {
                        var key = RecipeFingerprint(selectedRecipe);
                        lock (_suppressedRecipeKeysLock)
                        {
                            _suppressedRecipeKeys.Add(key);
                        }
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: recorded suppressed recipe fingerprint: {key}");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: exception recording suppressed fingerprint: {ex}");
                    }

                    // Now suppress the resources collection on the live recipe instance so the game won't consume them.
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
                        UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: suppressed resources for plugin-managed keep-one-on-fail behavior.");
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix exception: {ex}");
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;
                }
            }

            [UsedImplicitly]
            static void Postfix(InventoryGui __instance, Player player)
            {
                try
                {
                    // Restore saved resources if needed (use exact recipe instance)
                    if (_savedRecipeForCall != null && _suppressedThisCall)
                    {
                        lock (_savedResources)
                        {
                            if (_savedResources.TryGetValue(_savedRecipeForCall, out var saved))
                            {
                                var resourcesField = _savedRecipeForCall.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (resourcesField != null)
                                {
                                    resourcesField.SetValue(_savedRecipeForCall, saved);
                                }
                                _savedResources.Remove(_savedRecipeForCall);
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: restored resources for savedRecipeForCall: {RecipeInfo(_savedRecipeForCall)}");
                            }
                        }
                    }

                    // If we didn't suppress, do nothing (the game handled removal or upgrade)
                    if (!_suppressedThisCall)
                    {
                        _suppressedThisCall = false;
                        _savedRecipeForCall = null;
                        IsDoCraft = false;
                        return;
                    }

                    // We suppressed: run chance logic for the same recipe instance
                    var recipeForLogic = _savedRecipeForCall;
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;

                    try
                    {
                        // If this call was detected as an upgrade in Prefix, handle upgrade-specific removal using upgrade recipe and preserve selected item.
                        if (_isUpgradeDetected || IsUpgradeOperation(__instance, recipeForLogic))
                        {
                            UnityEngine.Debug.LogWarning("[ChanceCraft] Postfix: upgrade detected — using upgrade recipe to remove resources and preserving upgrade target.");

                            try
                            {
                                // Ensure we try to capture GUI recipe and selected item now if we haven't earlier (defensive)
                                if (_upgradeGuiRecipe == null) _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance);
                                if (_upgradeTargetItem == null) _upgradeTargetItem = GetSelectedInventoryItem(__instance);

                                // Prefer previously-captured GUI upgrade recipe, then previously-captured upgrade recipe, then (as a last resort) the crafting recipe
                                var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? recipeForLogic;

                                // Logging to help diagnosis — which recipe instance we ended up using
                                var which = recipeToUse == _upgradeGuiRecipe ? "upgradeGuiRecipe"
                                          : recipeToUse == _upgradeRecipe ? "upgradeRecipe"
                                          : recipeToUse == recipeForLogic ? "craftingRecipe(fallback)"
                                          : "other";
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: selected recipeToUse = {which} : {RecipeInfo(recipeToUse)}");
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: upgrade target = {ItemInfo(_upgradeTargetItem)}");

                                if (recipeToUse != null)
                                {
                                    // Use dedicated upgrade removal which preserves the selected upgrade target item and only removes resources on failure.
                                    RemoveRequiredResourcesUpgrade(__instance, player, recipeToUse, _upgradeTargetItem, false);
                                    UnityEngine.Debug.LogWarning("[ChanceCraft] Postfix: removed resources for failed upgrade using selected recipe (preserved upgrade target).");
                                }
                            }
                            catch (Exception ex)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: Exception while removing resources for upgrade: {ex}");
                            }

                            // cleanup fingerprint, snapshot and upgrade state
                            try
                            {
                                if (recipeForLogic != null)
                                {
                                    var key = RecipeFingerprint(recipeForLogic);
                                    lock (_suppressedRecipeKeysLock) { _suppressedRecipeKeys.Remove(key); }
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: removed suppressed fingerprint {key}");
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

                        // Non-upgrade suppressed craft: run plugin chance-logic (this may return a Recipe to indicate a failed craft)
                        Recipe recept = ChanceCraft.TrySpawnCraftEffect(__instance, recipeForLogic);

                        if (player != null && recept != null)
                        {
                            try
                            {
                                UnityEngine.Debug.LogWarning("[ChanceCraft] Postfix: failed craft detected for multi-resource recipe — removing newly-created crafted items only.");

                                // Use snapshot if it matches the recipe; otherwise null
                                HashSet<ItemDrop.ItemData> beforeSet = null;
                                lock (typeof(ChanceCraft))
                                {
                                    if (_snapshotRecipe != null && _snapshotRecipe == recept)
                                    {
                                        beforeSet = _preCraftSnapshot;
                                    }
                                    _preCraftSnapshot = null;
                                    _preCraftSnapshotData = null;
                                    _snapshotRecipe = null;
                                }

                                // Remove up to recipe.m_amount of crafted items, preferring items not in beforeSet.
                                int toRemoveCount = recept.m_amount > 0 ? recept.m_amount : 1;
                                var invItems = player.GetInventory()?.GetAllItems();
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: attempting to remove up to {toRemoveCount} of result type {recept.m_item?.m_itemData?.m_shared?.m_name}; snapshotBeforeCount={(beforeSet?.Count ?? -1)}");
                                if (invItems != null)
                                {
                                    int removedTotal = 0;

                                    // First pass: remove items that match recipe and are NOT in beforeSet (newly created instances)
                                    for (int i = invItems.Count - 1; i >= 0 && toRemoveCount > 0; i--)
                                    {
                                        var item = invItems[i];
                                        if (item == null || item.m_shared == null) continue;
                                        var craftedName = recept.m_item?.m_itemData?.m_shared?.m_name;
                                        var craftedQuality = recept.m_item?.m_itemData?.m_quality ?? 0;
                                        var craftedVariant = recept.m_item?.m_itemData?.m_variant ?? 0;

                                        if (item.m_shared.m_name == craftedName && item.m_quality == craftedQuality && item.m_variant == craftedVariant)
                                        {
                                            // Never remove the exact upgrade target item (safety)
                                            if (item == _upgradeTargetItem)
                                            {
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: skipping removal of upgrade target item {ItemInfo(item)}");
                                                continue;
                                            }

                                            if (beforeSet == null || !beforeSet.Contains(item))
                                            {
                                                int remove = Math.Min(item.m_stack, toRemoveCount);
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: removing {remove} from newly created item {ItemInfo(item)}");
                                                item.m_stack -= remove;
                                                toRemoveCount -= remove;
                                                removedTotal += remove;
                                                if (item.m_stack <= 0)
                                                {
                                                    try { player.GetInventory().RemoveItem(item); } catch { }
                                                }
                                            }
                                            else
                                            {
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: not removing existing pre-craft item {ItemInfo(item)} (in snapshot)");
                                            }
                                        }
                                    }

                                    // If nothing new was found (edge case) and we have a snapshot, DO NOT fall back to deleting by name:
                                    if (toRemoveCount > 0)
                                    {
                                        if (beforeSet != null && removedTotal == 0)
                                        {
                                            UnityEngine.Debug.LogWarning("[ChanceCraft] Postfix: no newly-created items found (all matching items were in snapshot) — skipping fallback removal to avoid destroying pre-existing items.");
                                            // won't remove anything further
                                        }
                                        else
                                        {
                                            // Fallback: remove by matching name/quality/variant (legacy behavior)
                                            for (int i = invItems.Count - 1; i >= 0 && toRemoveCount > 0; i--)
                                            {
                                                var item = invItems[i];
                                                if (item == null || item.m_shared == null) continue;
                                                var craftedName = recept.m_item?.m_itemData?.m_shared?.m_name;
                                                var craftedQuality = recept.m_item?.m_itemData?.m_quality ?? 0;
                                                var craftedVariant = recept.m_item?.m_itemData?.m_variant ?? 0;

                                                if (item.m_shared.m_name == craftedName && item.m_quality == craftedQuality && item.m_variant == craftedVariant)
                                                {
                                                    if (item == _upgradeTargetItem)
                                                    {
                                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix (fallback): skipping upgrade target {ItemInfo(item)}");
                                                        continue;
                                                    }

                                                    int remove = Math.Min(item.m_stack, toRemoveCount);
                                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix (fallback): removing {remove} from {ItemInfo(item)}");
                                                    item.m_stack -= remove;
                                                    toRemoveCount -= remove;
                                                    if (item.m_stack <= 0)
                                                    {
                                                        try { player.GetInventory().RemoveItem(item); } catch { }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: Exception while removing crafted item: {ex}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: Exception in upgrade-check/ChanceCraft logic: {ex}");
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix exception: {ex}");
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;
                }
            }
        }

        // TrySpawnCraftEffect (restored and upgrade-safe)
        public static Recipe TrySpawnCraftEffect(InventoryGui gui, Recipe forcedRecipe = null)
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

            if (selectedRecipe == null || Player.m_localPlayer == null)
                return null;

            UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect called for recipe: {RecipeInfo(selectedRecipe)}");

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
                UnityEngine.Debug.LogWarning("[ChanceCraft] Item type not eligible for TrySpawnCraftEffect.");
                return null;
            }

            UnityEngine.Debug.LogWarning("[chancecraft] before rand");
            var player = Player.m_localPlayer;

            UnityEngine.Debug.LogWarning($"[chancecraft] rand = {UnityEngine.Random.value}");
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

            if (UnityEngine.Random.value <= chance)
            {
                UnityEngine.Debug.LogWarning("[chancecraft] success");
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
                            createMethod.Invoke(m_craftItemEffects, new object[] { player.transform.position, Quaternion.identity });
                        }
                    }
                }

                // Upgrade success handling:
                if (IsUpgradeOperation(gui, selectedRecipe) || _isUpgradeDetected)
                {
                    // Determine whether this operation was suppressed earlier.
                    bool suppressedThisOperation = IsDoCraft;

                    // fingerprint-based check (recognizes suppression even if the recipe instance changed)
                    try
                    {
                        var key = RecipeFingerprint(selectedRecipe);
                        lock (_suppressedRecipeKeysLock)
                        {
                            if (_suppressedRecipeKeys.Contains(key))
                            {
                                suppressedThisOperation = true;
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: detected suppressed recipe fingerprint {key} -> treating as suppressed");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: exception checking suppressed fingerprint: {ex}");
                    }

                    // Also treat the presence of a matching pre-craft snapshot as evidence we suppressed earlier.
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
                    catch { /* ignore */ }

                    if (suppressedThisOperation)
                    {
                        try
                        {
                            // Prefer GUI upgrade recipe if available
                            var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                            if (_upgradeTargetItem == null) _upgradeTargetItem = GetSelectedInventoryItem(gui);

                            UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: upgrade success detected with suppression - removing resources using {RecipeInfo(recipeToUse)}; target={ItemInfo(_upgradeTargetItem)}");

                            // Remove upgrade resources for successful upgrade (plugin-managed because we suppressed)
                            RemoveRequiredResourcesUpgrade(gui, player, recipeToUse, _upgradeTargetItem, true);

                            // remove fingerprint & clear snapshot state - we preserved/handled upgrade
                            try
                            {
                                var key = RecipeFingerprint(selectedRecipe);
                                lock (_suppressedRecipeKeysLock) { _suppressedRecipeKeys.Remove(key); }
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: removed suppressed fingerprint {key}");
                            }
                            catch { }

                            lock (typeof(ChanceCraft))
                            {
                                _preCraftSnapshot = null;
                                _preCraftSnapshotData = null;
                                _snapshotRecipe = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: Exception while removing upgrade resources after success: {ex}");
                        }
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning("[ChanceCraft] TrySpawnCraftEffect: upgrade success detected, skipping plugin resource removal so the game handles it.");
                    }

                    // Return null so Postfix will not attempt to remove crafted/upgraded item.
                    return null;
                }

                // Success -> remove resources (plugin-managed path) for normal crafting
                RemoveRequiredResources(gui, player, selectedRecipe, true, false);

                UnityEngine.Debug.LogWarning("[chancecraft] removed materials ok ...");
                return null;
            }
            else
            {
                UnityEngine.Debug.LogWarning("[chancecraft] failed");

                // If this failed attempt looks like an upgrade, remove resources using upgrade recipe if available,
                // but do NOT remove the base/upgrading item.
                if (IsUpgradeOperation(gui, selectedRecipe) || _isUpgradeDetected)
                {
                    try
                    {
                        // Prefer already-captured GUI upgrade recipe, then upgradeRecipe, then attempt to get GUI recipe now,
                        // finally fall back to selectedRecipe.
                        var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;

                        // Defensive capture of target item if not already captured
                        if (_upgradeTargetItem == null) _upgradeTargetItem = GetSelectedInventoryItem(gui);

                        var which = recipeToUse == _upgradeGuiRecipe ? "upgradeGuiRecipe"
                                  : recipeToUse == _upgradeRecipe ? "upgradeRecipe"
                                  : recipeToUse == selectedRecipe ? "craftingRecipe(fallback)"
                                  : "other";
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: removing upgrade resources using {which} : {RecipeInfo(recipeToUse)}");
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: upgrade target = {ItemInfo(_upgradeTargetItem)}");

                        // Use dedicated upgrade removal which preserves the selected upgrade target item and only removes resources on failure.
                        RemoveRequiredResourcesUpgrade(gui, player, recipeToUse, _upgradeTargetItem, false);
                        player.Message(MessageHud.MessageType.Center, "<color=red>Upgrade failed — materials consumed, item preserved.</color>");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: Exception while removing upgrade resources: {ex}");
                    }

                    // clear any per-call snapshot (we preserved upgrade target)
                    lock (typeof(ChanceCraft))
                    {
                        _preCraftSnapshot = null;
                        _preCraftSnapshotData = null;
                        _snapshotRecipe = null;
                    }

                    // Return null so Postfix will not attempt to remove crafted/upgraded item.
                    return null;
                }

                // Before we treat this as a normal failed craft and remove resources, check the pre-craft snapshot
                // to see whether the game actually performed an in-place upgrade (i.e. mutated an existing ItemData).
                // Use the saved pre-craft quality/variant values to detect a real change.
                bool gameAlreadyHandled = false;
                try
                {
                    lock (typeof(ChanceCraft))
                    {
                        if (_snapshotRecipe != null && _snapshotRecipe == selectedRecipe && _preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                        {
                            var resultQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                            var resultVariant = selectedRecipe.m_item?.m_itemData?.m_variant ?? 0;

                            foreach (var kv in _preCraftSnapshotData)
                            {
                                var item = kv.Key;
                                var pre = kv.Value; // (quality, variant)
                                if (item == null || item.m_shared == null) continue;

                                int currentQuality = item.m_quality;
                                int currentVariant = item.m_variant;

                                // Detect an actual in-place upgrade: currentQuality > pre.quality (and variant match)
                                if (currentQuality > pre.quality && currentVariant == resultVariant)
                                {
                                    gameAlreadyHandled = true;
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: detected pre-snapshot item upgraded in-place: {ItemInfo(item)} (was q={pre.quality} v={pre.variant} -> now q={currentQuality} v={currentVariant}) -> treating as success, skipping plugin removal.");
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: exception while checking snapshot for in-place upgrade: {ex}");
                }

                if (gameAlreadyHandled)
                {
                    lock (typeof(ChanceCraft))
                    {
                        _preCraftSnapshot = null;
                        _preCraftSnapshotData = null;
                        _snapshotRecipe = null;
                    }
                    return null;
                }

                // Normal failed craft on suppressed/multi-resource path -> remove resources (keep-one) and return Recipe
                RemoveRequiredResources(gui, player, selectedRecipe, false, false);

                // Show red message
                player.Message(MessageHud.MessageType.Center, "<color=red>Crafting failed!</color>");
                return selectedRecipe;
            }

            // Defensive final return to satisfy the compiler: no path should fall through without a return.
            return null;
        }

        // RemoveRequiredResources now supports skipping removal of the resource that equals the recipe result (used for failed upgrades).
        public static void RemoveRequiredResources(InventoryGui gui, Player player, Recipe selectedRecipe, Boolean crafted, bool skipRemovingResultResource = false)
        {
            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources called recipe={RecipeInfo(selectedRecipe)} crafted={crafted} skipResult={skipRemovingResultResource}");
            if (player == null || selectedRecipe == null) return;
            var inventory = player.GetInventory();
            if (inventory == null) return;

            // Preserve craft upgrade multiplier if present
            var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
            int craftUpgrade = 1;
            if (craftUpgradeField != null)
            {
                try
                {
                    object value = craftUpgradeField.GetValue(gui);
                    if (value is int q && q > 1)
                        craftUpgrade = q;
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: craftUpgrade={craftUpgrade}");
                }
                catch { /* ignore */ }
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

            object GetNested(object obj, params string[] names)
            {
                object cur = obj;
                foreach (var n in names)
                {
                    if (cur == null) return null;
                    cur = GetMember(cur, n);
                }
                return cur;
            }

            int ToInt(object v)
            {
                if (v == null) return 0;
                try { return Convert.ToInt32(v); } catch { return 0; }
            }

            // Resolve the recipe result name (used when skipRemovingResultResource is requested)
            string resultName = null;
            try
            {
                resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: resultName='{resultName}'");
            }
            catch { resultName = null; }

            // Get resources collection
            var resourcesObj = GetMember(selectedRecipe, "m_resources");
            var resources = resourcesObj as System.Collections.IEnumerable;
            if (resources == null)
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResources: no resources found on recipe");
                return;
            }

            // Build list to iterate multiple times
            var resourceList = resources.Cast<object>().ToList();

            // Helper: remove amount from player's inventory by scanning stacks with matching heuristics.
            int RemoveAmountFromInventory(string resourceName, int amount)
            {
                if (string.IsNullOrEmpty(resourceName) || amount <= 0) return 0;

                int remaining = amount;
                var items = inventory.GetAllItems();

                // Attempt removal by predicate helper
                void TryRemove(Func<ItemDrop.ItemData, bool> predicate)
                {
                    if (remaining <= 0) return;
                    for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                    {
                        var it = items[i];
                        if (it == null || it.m_shared == null) continue;
                        if (!predicate(it)) continue;
                        int toRemove = Math.Min(it.m_stack, remaining);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventory: removing {toRemove} from {ItemInfo(it)} for resource '{resourceName}'");
                        it.m_stack -= toRemove;
                        remaining -= toRemove;
                        if (it.m_stack <= 0)
                        {
                            try { inventory.RemoveItem(it); } catch { /* best-effort */ }
                        }
                    }
                }

                try
                {
                    // 1) exact token match
                    TryRemove(it => it.m_shared.m_name == resourceName);

                    // 2) exact case-insensitive
                    TryRemove(it => string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase));

                    // 3) token contains (inventory name contains resource token)
                    TryRemove(it => it.m_shared.m_name != null && resourceName != null &&
                                     it.m_shared.m_name.IndexOf(resourceName, StringComparison.OrdinalIgnoreCase) >= 0);

                    // 4) token contained in resource name (reverse)
                    TryRemove(it => it.m_shared.m_name != null && resourceName != null &&
                                     resourceName.IndexOf(it.m_shared.m_name, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventory: exception during attempts: {ex}");
                }

                // 5) fallback: try RemoveItem API (best-effort)
                if (remaining > 0)
                {
                    try
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventory: fallback RemoveItem by name '{resourceName}' remaining={remaining}");
                        inventory.RemoveItem(resourceName, remaining);
                        remaining = 0;
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventory: RemoveItem exception: {ex}");
                        // swallow - best-effort
                    }
                }

                return amount - remaining; // removed count
            }

            // Build compact list of (name, amount) for requirements
            var validReqs = new List<(object req, string name, int amount)>();
            foreach (var req in resourceList)
            {
                var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                if (shared == null) continue;
                var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                string resourceName = nameObj as string;
                if (string.IsNullOrEmpty(resourceName)) continue;
                int amount = ToInt(GetMember(req, "m_amount")) * ((ToInt(GetMember(req, "m_amountPerLevel")) > 0 && craftUpgrade > 1) ? craftUpgrade : 1);
                if (amount <= 0) continue;
                validReqs.Add((req, resourceName, amount));
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: requirement '{resourceName}' amount={amount}");
            }

            // Build filtered list that excludes the recipe result resource if requested.
            List<(object req, string name, int amount)> validReqsFiltered = validReqs;
            if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName))
            {
                validReqsFiltered = validReqs.Where(v => !string.Equals(v.name, resultName, StringComparison.OrdinalIgnoreCase)).ToList();
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: validReqsFiltered count={validReqsFiltered.Count}");
            }

            // SINGLE-RESOURCE CASE
            if (validReqs.Count == 1 && resourceList.Count <= 1)
            {
                var single = validReqs[0];
                if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) && string.Equals(single.name, resultName, StringComparison.OrdinalIgnoreCase))
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResources: single-resource recipe is upgrade-result — skipping resource removal of result item.");
                    return;
                }
                try
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: single-resource removal '{single.name}' amount={single.amount}");
                    RemoveAmountFromInventory(single.name, single.amount);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources single-resource removal failed: {ex}");
                }
                return;
            }

            // If crafting failed: remove all required resources EXCEPT one random resource.
            if (!crafted)
            {
                if (validReqs.Count == 0) return;
                if (validReqsFiltered.Count == 0) return;

                int keepIndex = UnityEngine.Random.Range(0, validReqsFiltered.Count);
                var keepTuple = validReqsFiltered[keepIndex];
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: keeping resource '{keepTuple.name}' amount={keepTuple.amount}");

                bool skippedKeep = false;
                foreach (var req in resourceList)
                {
                    var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                    if (shared == null) continue;

                    var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                    string resourceName = nameObj as string;
                    if (string.IsNullOrEmpty(resourceName)) continue;

                    if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) &&
                        string.Equals(resourceName, resultName, StringComparison.OrdinalIgnoreCase))
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: preserving result-resource '{resourceName}'");
                        continue;
                    }

                    int amount = ToInt(GetMember(req, "m_amount")) * ((ToInt(GetMember(req, "m_amountPerLevel")) > 0 && craftUpgrade > 1) ? craftUpgrade : 1);
                    if (amount <= 0) continue;

                    if (!skippedKeep && resourceName == keepTuple.name && amount == keepTuple.amount)
                    {
                        skippedKeep = true;
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: keep-one -> skipping '{resourceName}' amount={amount}");
                        continue;
                    }

                    try
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: removing '{resourceName}' amount={amount}");
                        RemoveAmountFromInventory(resourceName, amount);
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources removal failed: {ex}");
                    }
                }

                return;
            }

            // crafted == true: remove all required resources (plugin-managed)
            foreach (var req in resourceList)
            {
                var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                if (shared == null) continue;

                var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                string resourceName = nameObj as string;
                if (string.IsNullOrEmpty(resourceName)) continue;

                if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) &&
                    string.Equals(resourceName, resultName, StringComparison.OrdinalIgnoreCase))
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: skipping removal of recipe-result resource '{resourceName}' on crafted=true");
                    continue;
                }

                int amount = ToInt(GetMember(req, "m_amount")) * ((ToInt(GetMember(req, "m_amountPerLevel")) > 0 && craftUpgrade > 1) ? craftUpgrade : 1);
                if (amount <= 0) continue;

                try
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: crafted removal of '{resourceName}' amount={amount}");
                    RemoveAmountFromInventory(resourceName, amount);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources removal failed: {ex}");
                }
            }
        }

        // New helper specifically for upgrade removal.
        // This method uses the upgrade recipe (passed as selectedRecipe) and will:
        // - Prefer the GUI upgrade recipe if available.
        // - Apply craft-upgrade multiplier logic (m_amountPerLevel & m_craftUpgrade).
        // - Support both crafted==true (success) and crafted==false (failure) behavior.
        // - Always skip removing any resource that equals the recipe result.
        // - Never remove the exact inventory item instance specified by upgradeTargetItem.
        public static void RemoveRequiredResourcesUpgrade(InventoryGui gui, Player player, Recipe selectedRecipe, ItemDrop.ItemData upgradeTargetItem, bool crafted)
        {
            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade called. incoming recipe: {RecipeInfo(selectedRecipe)} upgradeTarget={ItemInfo(upgradeTargetItem)} crafted={crafted}");
            if (player == null || selectedRecipe == null) return;
            var inventory = player.GetInventory();
            if (inventory == null) return;

            // Prefer explicit GUI upgrade recipe if available (so we don't end up using the crafting recipe)
            Recipe recipeToUse = selectedRecipe;
            try
            {
                var guiRecipe = GetUpgradeRecipeFromGui(gui);
                if (guiRecipe != null)
                {
                    recipeToUse = guiRecipe;
                    UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: found GUI upgrade recipe and will use it: " + RecipeInfo(guiRecipe));
                }
                else
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: no GUI upgrade recipe found; using passed recipe");
                }
            }
            catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: GetUpgradeRecipeFromGui exception: {ex}"); }

            // Preserve craft upgrade multiplier if present
            var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
            int craftUpgrade = 1;
            if (craftUpgradeField != null)
            {
                try
                {
                    object value = craftUpgradeField.GetValue(gui);
                    if (value is int q && q > 1)
                        craftUpgrade = q;
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: craftUpgrade={craftUpgrade}");
                }
                catch { /* ignore */ }
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
            object GetNested(object obj, params string[] names)
            {
                object cur = obj;
                foreach (var n in names)
                {
                    if (cur == null) return null;
                    cur = GetMember(cur, n);
                }
                return cur;
            }
            int ToInt(object v)
            {
                if (v == null) return 0;
                try { return Convert.ToInt32(v); } catch { return 0; }
            }

            string resultName = null;
            try
            {
                resultName = recipeToUse.m_item?.m_itemData?.m_shared?.m_name;
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: recipeToUse resultName='{resultName}'");
            }
            catch { resultName = null; }

            var resourcesObj = GetMember(recipeToUse, "m_resources");
            var resources = resourcesObj as System.Collections.IEnumerable;
            if (resources == null)
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: no resources found on recipeToUse");
                return;
            }

            var resourceList = resources.Cast<object>().ToList();
            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: resourcesCount={resourceList.Count}");

            var validReqs = new List<(object req, string name, int amount)>();
            foreach (var req in resourceList)
            {
                var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                if (shared == null) continue;
                var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                string resourceName = nameObj as string;
                if (string.IsNullOrEmpty(resourceName)) continue;

                int baseAmount = ToInt(GetMember(req, "m_amount"));
                int perLevel = ToInt(GetMember(req, "m_amountPerLevel"));
                int amount = baseAmount * ((perLevel > 0 && craftUpgrade > 1) ? craftUpgrade : 1);

                if (amount <= 0) continue;
                validReqs.Add((req, resourceName, amount));
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: req found '{resourceName}' amount={amount} (base={baseAmount} perLevel={perLevel})");
            }

            var validReqsFiltered = validReqs.Where(v => !string.Equals(v.name, resultName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (validReqsFiltered.Count == 0)
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: filtered valid reqs empty (all resources were result-resource?) -> nothing to remove.");
                return;
            }

            int RemoveAmountFromInventorySkippingTarget(string resourceName, int amount)
            {
                if (string.IsNullOrEmpty(resourceName) || amount <= 0) return 0;

                int remaining = amount;
                var items = inventory.GetAllItems();

                void TryRemove(Func<ItemDrop.ItemData, bool> predicate)
                {
                    if (remaining <= 0) return;
                    for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                    {
                        var it = items[i];
                        if (it == null || it.m_shared == null) continue;
                        if (upgradeTargetItem != null && ReferenceEquals(it, upgradeTargetItem))
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: skipping exact upgrade target {ItemInfo(it)}");
                            continue;
                        }
                        if (!predicate(it)) continue;
                        int toRemove = Math.Min(it.m_stack, remaining);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: removing {toRemove} from {ItemInfo(it)} matching '{resourceName}'");
                        it.m_stack -= toRemove;
                        remaining -= toRemove;
                        if (it.m_stack <= 0)
                        {
                            try { inventory.RemoveItem(it); } catch { /* best-effort */ }
                        }
                    }
                }

                // 1) exact token match
                TryRemove(it => it.m_shared.m_name == resourceName);

                // 2) exact case-insensitive
                TryRemove(it => string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase));

                // 3) token contains
                TryRemove(it => it.m_shared.m_name != null && resourceName != null &&
                                 it.m_shared.m_name.IndexOf(resourceName, StringComparison.OrdinalIgnoreCase) >= 0);

                // 4) reverse contains
                TryRemove(it => it.m_shared.m_name != null && resourceName != null &&
                                 resourceName.IndexOf(it.m_shared.m_name, StringComparison.OrdinalIgnoreCase) >= 0);

                if (remaining > 0)
                {
                    try
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: fallback RemoveItem by name '{resourceName}' remaining={remaining}");
                        inventory.RemoveItem(resourceName, remaining);
                        remaining = 0;
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: RemoveItem exception: {ex}");
                    }
                }

                return amount - remaining;
            }

            // crafted == true: success path after suppression
            if (crafted)
            {
                foreach (var req in validReqs)
                {
                    if (!string.IsNullOrEmpty(resultName) &&
                        string.Equals(req.name, resultName, StringComparison.OrdinalIgnoreCase))
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: skipping removal of result-resource '{req.name}' on crafted=true");
                        continue;
                    }

                    try
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (crafted): removing '{req.name}' amount={req.amount}");
                        int removed = RemoveAmountFromInventorySkippingTarget(req.name, req.amount);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (crafted): removed {removed}/{req.amount} of '{req.name}'");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (crafted) removal failed: {ex}");
                    }
                }
                return;
            }

            // Failure behavior (keep-one except result resource)
            if (validReqs.Count == 0)
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: no valid requirements found -> nothing to remove.");
                return;
            }

            int keepIndex = UnityEngine.Random.Range(0, validReqsFiltered.Count);
            var keepTuple = validReqsFiltered[keepIndex];
            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: keepIndex={keepIndex} keepTuple={keepTuple.name}:{keepTuple.amount}");

            bool skippedKeep = false;
            foreach (var req in resourceList)
            {
                var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                if (shared == null) continue;

                var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                string resourceName = nameObj as string;
                if (string.IsNullOrEmpty(resourceName)) continue;

                if (!string.IsNullOrEmpty(resultName) &&
                    string.Equals(resourceName, resultName, StringComparison.OrdinalIgnoreCase))
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: preserving result-resource '{resourceName}'");
                    continue;
                }

                int baseAmount = ToInt(GetMember(req, "m_amount"));
                int perLevel = ToInt(GetMember(req, "m_amountPerLevel"));
                int amount = baseAmount * ((perLevel > 0 && craftUpgrade > 1) ? craftUpgrade : 1);
                if (amount <= 0) continue;

                if (!skippedKeep && resourceName == keepTuple.name && amount == keepTuple.amount)
                {
                    skippedKeep = true;
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: keeping resource '{resourceName}' amount={amount} (keep-one)");
                    continue;
                }

                try
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: removing resource '{resourceName}' amount={amount}");
                    int removed = RemoveAmountFromInventorySkippingTarget(resourceName, amount);
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: removed {removed}/{amount} of '{resourceName}'");
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade removal failed: {ex}");
                }
            }
        }
    }
}