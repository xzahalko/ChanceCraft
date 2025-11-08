// Updated ChanceCraft.cs
// Fixes / changes in this iteration:
// - Do NOT remove crafting resources on a successful upgrade: if the operation is an upgrade, let the game handle success removal.
// - In Postfix upgrade branch, make a last-attempt capture of the GUI upgrade recipe and the selected inventory item (upgrade target)
//   so we avoid falling back to the crafting recipe or the wrong inventory instance.
// - In TrySpawnCraftEffect (failed-upgrade branch) ensure _upgradeTargetItem is captured if still null before calling upgrade removal.
// - Added targeted logs to help confirm which recipe and which target instance are used when removing resources.
// These changes are defensive patches to reduce incorrect removals when upgrade detection wasn't captured earlier.
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
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
        private static ConfigEntry<float> myBossEithkyrStrength;

        public const string pluginID = "deep.ChanceCraft";
        public const string pluginName = "Chance Craft";
        public const string pluginVersion = "1.1.0";

        private readonly Harmony harmony = new Harmony(pluginID);

        public static ChanceCraft instance;

        public static ConfigEntry<bool> modEnabled;
        public static ConfigEntry<bool> configLocked;
        public static ConfigEntry<bool> loggingEnabled;

        private static ConfigEntry<bool> _loggingEnabled;

        private Harmony _harmony;
        private static bool IsDoCraft;

        // Snapshot & upgrade detection state (single-call snapshot)
        private static HashSet<ItemDrop.ItemData> _preCraftSnapshot = null;
        private static Recipe _snapshotRecipe = null;

        private static ItemDrop.ItemData _upgradeTargetItem = null;   // exact inventory item being upgraded (if detected)
        private static Recipe _upgradeRecipe = null;
        private static Recipe _upgradeGuiRecipe = null;
        private static bool _isUpgradeDetected = false;

        [UsedImplicitly]
        void Awake()
        {
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), "ChanceCraft");

            instance = this;

            _loggingEnabled = Config.Bind("Logging", "Logging Enabled", true, "Enable logging");
            weaponSuccessChance = Config.Bind("General", "WeaponSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft weapons (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));
            armorSuccessChance = Config.Bind("General", "ArmorSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft armors (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));
            arrowSuccessChance = Config.Bind("General", "ArrowSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft arrows (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));

            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting weapons success chance set to {weaponSuccessChance.Value * 100}%.");
            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting armors success chance set to {armorSuccessChance.Value * 100}%.");
            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting arrow success chance set to {arrowSuccessChance.Value * 100}%.");

            UnityEngine.Debug.LogWarning("[ChanceCraft] Awake called!");

            try
            {
                var method = AccessTools.Method(typeof(InventoryGui), "DoCrafting");
                if (method == null)
                    UnityEngine.Debug.LogError("[ChanceCraft] Could not find InventoryGui.DoCrafting for patching!");
                else
                    UnityEngine.Debug.LogWarning("[ChanceCraft] InventoryGui.DoCrafting found for patching.");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[ChanceCraft] Exception while finding DoCrafting: {ex}");
            }

            Game.isModded = true;
        }

        [UsedImplicitly]
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        void Start()
        {
            UnityEngine.Debug.LogWarning("[ChanceCraft] Start called!");
        }

        void OnEnable()
        {
            Harmony.DEBUG = true;
            UnityEngine.Debug.LogWarning("[ChanceCraft] OnEnable called! Harmony.DEBUG enabled.");
        }

        public void UpdateBossStrength()
        {
            var eikthyr = Jotunn.Managers.PrefabManager.Instance.GetPrefab("Eikthyr");
            if (eikthyr != null)
            {
                var character = eikthyr.GetComponent<Character>();
                if (character != null)
                {
                    character.m_health = myBossEithkyrStrength.Value * 1000;
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Eikthyr health set to {myBossEithkyrStrength.Value}");
                }
                else
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] Eikthyr prefab does not have a Character component!");
                }
            }
            else
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] Eikthyr prefab not found!");
            }
        }

        // Remove up to recipe.m_amount (or 1) of the crafted item (stack-aware)
        public static void RemoveCraftedItem(Player player, Recipe recipe)
        {
            if (player == null || recipe == null || recipe.m_item == null) return;
            var inventory = player.GetInventory();
            UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveCraftedItem called!");

            if (inventory == null) return;

            int craftedCount = recipe.m_amount > 0 ? recipe.m_amount : 1;
            var craftedName = recipe.m_item.m_itemData.m_shared.m_name;
            var craftedQuality = recipe.m_item.m_itemData.m_quality;
            var craftedVariant = recipe.m_item.m_itemData.m_variant;

            var items = inventory.GetAllItems();
            for (int i = items.Count - 1; i >= 0 && craftedCount > 0; i--)
            {
                var item = items[i];
                if (item == null || item.m_shared == null) continue;
                if (item.m_shared.m_name == craftedName &&
                    item.m_quality == craftedQuality &&
                    item.m_variant == craftedVariant)
                {
                    int toRemove = Math.Min(item.m_stack, craftedCount);
                    item.m_stack -= toRemove;
                    craftedCount -= toRemove;
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveCraftedItem removed {toRemove} from stack, new stack: {item.m_stack}");
                    if (item.m_stack <= 0)
                    {
                        try { inventory.RemoveItem(item); } catch { }
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveCraftedItem removed item (stack now 0): {item.m_shared.m_name}");
                    }
                }
            }

            if (craftedCount > 0)
            {
                try { inventory.RemoveItem(craftedName, craftedCount); } catch { }
                UnityEngine.Debug.LogWarning($"[ChanceCraft] Remove crafted item by name and stack: attempted {craftedCount}");
            }
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
                    // Try to read m_resItem -> m_itemData -> m_shared -> m_name
                    object resItem = null;
                    try
                    {
                        var t = req.GetType();
                        var resItemField = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        resItem = resItemField?.GetValue(req);
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

        // Try to read the selected inventory item from InventoryGui using a few known field/property names.
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

            // Common candidates used in various versions/mods:
            var candidates = new[] { "m_selectedItem", "m_selected", "m_selectedItemData", "m_currentItem", "m_selectedInventoryItem" };
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

            // If not directly available, try to infer from selected slot index (some GUIs store index)
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

        // Try to read an upgrade recipe object from InventoryGui (if the GUI stores a specific upgrade recipe).
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
                        if (val is Recipe r) return r;
                        var p = val?.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (p != null)
                        {
                            var r2 = p.GetValue(val) as Recipe;
                            if (r2 != null) return r2;
                        }
                    }

                    var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(gui);
                        if (val is Recipe r2) return r2;
                    }
                }
            }
            catch { /* ignore */ }
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
                                // Detected explicit upgrade -> do not suppress. Clear saved recipe so Postfix ignores.
                                UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: detected explicit m_craftUpgrade > 1 - treating as upgrade, skipping suppression.");
                                _savedRecipeForCall = null;
                                _isUpgradeDetected = true;
                                // try capture upgrade recipe and selected item
                                _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance);
                                _upgradeRecipe = selectedRecipe;
                                _upgradeTargetItem = GetSelectedInventoryItem(__instance);
                                return;
                            }
                        }
                        catch { /* ignore */ }
                    }

                    // Build positive resource list and snapshot inventory items of the result type early
                    var resourcesField = selectedRecipe.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (resourcesField == null) return;
                    var resourcesObj = resourcesField.GetValue(selectedRecipe) as System.Collections.IEnumerable;
                    if (resourcesObj == null) return;
                    var resourceList = resourcesObj.Cast<object>().ToList();

                    // Reflection helpers
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
                                foreach (var it in inv.GetAllItems())
                                {
                                    if (it == null || it.m_shared == null) continue;
                                    if (it.m_shared.m_name == craftedName)
                                        existing.Add(it);
                                }
                                lock (typeof(ChanceCraft))
                                {
                                    _preCraftSnapshot = existing;
                                    _snapshotRecipe = selectedRecipe;
                                }
                            }
                        }
                    }
                    catch { /* continue if snapshot failed */ }

                    // Inventory-based upgrade detection: try to capture the exact selected inventory item first
                    try
                    {
                        var craftedName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                        int craftedQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                        var localPlayer = Player.m_localPlayer;

                        var selectedInventoryItem = GetSelectedInventoryItem(__instance);
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
                                        // Detected upgrade: save the exact item instance and recipe for possible Postfix behavior,
                                        // and treat this as an upgrade so we skip suppression.
                                        UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: detected lower-quality item in inventory - treating as upgrade, skipping suppression.");
                                        _isUpgradeDetected = true;
                                        _upgradeTargetItem = it;            // exact instance in inventory
                                        _upgradeRecipe = selectedRecipe;    // the recipe we're running as upgrade
                                        _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                                        _savedRecipeForCall = null;
                                        return;
                                    }
                                }
                            }
                        }
                    }
                    catch { /* ignore detection failures - fallback to non-upgrade path */ }

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
                            return;
                        }
                    }
                    catch { /* ignore */ }

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
                        // don't suppress single-resource recipes
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

                    if (!isEligible) return;

                    lock (_savedResources)
                    {
                        if (!_savedResources.ContainsKey(selectedRecipe))
                            _savedResources[selectedRecipe] = resourcesObj;
                    }

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
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: selected recipeToUse = {which}");

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

                            // cleanup snapshot and upgrade state
                            lock (typeof(ChanceCraft))
                            {
                                _preCraftSnapshot = null;
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
                                    _snapshotRecipe = null;
                                }

                                // Remove up to recipe.m_amount of crafted items, preferring items not in beforeSet.
                                int toRemoveCount = recept.m_amount > 0 ? recept.m_amount : 1;
                                var invItems = player.GetInventory()?.GetAllItems();
                                if (invItems != null)
                                {
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
                                            if (item == _upgradeTargetItem) continue;

                                            if (beforeSet == null || !beforeSet.Contains(item))
                                            {
                                                int remove = Math.Min(item.m_stack, toRemoveCount);
                                                item.m_stack -= remove;
                                                toRemoveCount -= remove;
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: removed {remove} from newly created stack {item.m_shared.m_name}");
                                                if (item.m_stack <= 0)
                                                {
                                                    try { player.GetInventory().RemoveItem(item); } catch { }
                                                }
                                            }
                                        }
                                    }

                                    // If nothing new was found (edge case), fall back to removing by matching name/quality/variant
                                    if (toRemoveCount > 0)
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
                                                // Never remove the exact upgrade target item (safety)
                                                if (item == _upgradeTargetItem) continue;

                                                int remove = Math.Min(item.m_stack, toRemoveCount);
                                                item.m_stack -= remove;
                                                toRemoveCount -= remove;
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix (fallback): removed {remove} from stack {item.m_shared.m_name}");
                                                if (item.m_stack <= 0)
                                                {
                                                    try { player.GetInventory().RemoveItem(item); } catch { }
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

                // IMPORTANT: if this is an upgrade operation, do not run plugin-managed removal for success
                // because the game's upgrade path may be different (and we may not have captured GUI upgrade recipe earlier).
                if (IsUpgradeOperation(gui, selectedRecipe) || _isUpgradeDetected)
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] TrySpawnCraftEffect: upgrade success detected, skipping plugin resource removal so the game handles it.");
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
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: removing upgrade resources using {which}");

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
                        _snapshotRecipe = null;
                    }

                    // Return null so Postfix will not attempt to remove crafted/upgraded item.
                    return null;
                }

                // Normal failed craft on suppressed/multi-resource path -> remove resources (keep-one) and return Recipe
                RemoveRequiredResources(gui, player, selectedRecipe, false, false);

                // Show red message
                player.Message(MessageHud.MessageType.Center, "<color=red>Crafting failed!</color>");
                return selectedRecipe;
            }
        }

        // helper: detect upgrade operation robustly
        private static bool IsUpgradeOperation(InventoryGui gui, Recipe recipe)
        {
            if (recipe == null || gui == null) return false;

            try
            {
                var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                if (craftUpgradeField != null)
                {
                    object cv = craftUpgradeField.GetValue(gui);
                    if (cv is int v && v > 1)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // ignore
            }

            // Inventory-based detection
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
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            // Recipe consumes result -> treat as upgrade
            try
            {
                if (RecipeConsumesResult(recipe))
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] IsUpgradeOperation: recipe consumes its result -> upgrade detected.");
                    return true;
                }
            }
            catch { /* ignore */ }

            return false;
        }

        // New helper specifically for upgrade removal.
        // This method uses the upgrade recipe (passed as selectedRecipe) and will:
        // - Prefer the GUI upgrade recipe if available.
        // - Apply craft-upgrade multiplier logic (m_amountPerLevel & m_craftUpgrade).
        // - Only remove resources when crafted == false (i.e. on failure).
        // - Always skip removing any resource that equals the recipe result.
        // - Never remove the exact inventory item instance specified by upgradeTargetItem.
        public static void RemoveRequiredResourcesUpgrade(InventoryGui gui, Player player, Recipe selectedRecipe, ItemDrop.ItemData upgradeTargetItem, bool crafted)
        {
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
                    UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: using GUI upgrade recipe instead of passed recipe.");
                }
            }
            catch { /* ignore */ }

            // For upgrade-specific helper we only remove resources on failure; if crafted==true we let the game handle success.
            if (crafted) return;

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

            // Resolve the recipe result name (used when skipping removal of the result item)
            string resultName = null;
            try
            {
                resultName = recipeToUse.m_item?.m_itemData?.m_shared?.m_name;
            }
            catch { resultName = null; }

            // Get resources collection from the recipeToUse (important to use the upgrade recipe)
            var resourcesObj = GetMember(recipeToUse, "m_resources");
            var resources = resourcesObj as System.Collections.IEnumerable;
            if (resources == null)
            {
                return;
            }

            // Build list to iterate multiple times
            var resourceList = resources.Cast<object>().ToList();

            // Build compact list of (name, amount) for requirements applying craftUpgrade and amount-per-level logic
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
            }

            // If filtered list is empty (all requirements were the result-resource and we skip them), nothing to remove
            var validReqsFiltered = validReqs.Where(v => !string.Equals(v.name, resultName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (validReqsFiltered.Count == 0) return;

            // Helper: remove amount from player's inventory by scanning stacks with matching heuristics,
            // but never remove the exact upgradeTargetItem instance.
            int RemoveAmountFromInventorySkippingTarget(string resourceName, int amount)
            {
                if (string.IsNullOrEmpty(resourceName) || amount <= 0) return 0;

                int remaining = amount;
                var items = inventory.GetAllItems();

                // Attempt removal by predicate helper that respects the upgradeTargetItem
                void TryRemove(Func<ItemDrop.ItemData, bool> predicate)
                {
                    if (remaining <= 0) return;
                    for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                    {
                        var it = items[i];
                        if (it == null || it.m_shared == null) continue;
                        // Never remove the exact selected upgrade target instance
                        if (upgradeTargetItem != null && ReferenceEquals(it, upgradeTargetItem)) continue;
                        if (!predicate(it)) continue;
                        int toRemove = Math.Min(it.m_stack, remaining);
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

                // 3) token contains (inventory name contains resource token)
                TryRemove(it => it.m_shared.m_name != null && resourceName != null &&
                                 it.m_shared.m_name.IndexOf(resourceName, StringComparison.OrdinalIgnoreCase) >= 0);

                // 4) token contained in resource name (reverse)
                TryRemove(it => it.m_shared.m_name != null && resourceName != null &&
                                 resourceName.IndexOf(it.m_shared.m_name, StringComparison.OrdinalIgnoreCase) >= 0);

                // 5) fallback: try RemoveItem API (best-effort) - note this may remove the target if the API targets it by name,
                // but since game API may only remove by name, we already tried instance-safe removal first.
                if (remaining > 0)
                {
                    try
                    {
                        inventory.RemoveItem(resourceName, remaining);
                        remaining = 0;
                    }
                    catch
                    {
                        // swallow - best-effort
                    }
                }

                return amount - remaining; // removed count
            }

            // --- Failure behavior for upgrades: remove all required resources EXCEPT the recipe result resource (i.e. preserve upgraded item)
            if (validReqs.Count == 0) return;

            // Choose one resource to keep (keep-one behavior) but ignore result-resource entries when picking
            int keepIndex = UnityEngine.Random.Range(0, validReqsFiltered.Count);
            var keepTuple = validReqsFiltered[keepIndex];

            bool skippedKeep = false;
            foreach (var req in resourceList)
            {
                var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                if (shared == null) continue;

                var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                string resourceName = nameObj as string;
                if (string.IsNullOrEmpty(resourceName)) continue;

                // Always preserve the base/upgrading item resource (do not remove resources that are the recipe result)
                if (!string.IsNullOrEmpty(resultName) &&
                    string.Equals(resourceName, resultName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int baseAmount = ToInt(GetMember(req, "m_amount"));
                int perLevel = ToInt(GetMember(req, "m_amountPerLevel"));
                int amount = baseAmount * ((perLevel > 0 && craftUpgrade > 1) ? craftUpgrade : 1);
                if (amount <= 0) continue;

                if (!skippedKeep && resourceName == keepTuple.name && amount == keepTuple.amount)
                {
                    skippedKeep = true;
                    continue;
                }

                try
                {
                    RemoveAmountFromInventorySkippingTarget(resourceName, amount);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade removal failed: {ex}");
                }
            }
        }

        // RemoveRequiredResources now supports skipping removal of the resource that equals the recipe result (used for failed upgrades).
        public static void RemoveRequiredResources(InventoryGui gui, Player player, Recipe selectedRecipe, Boolean crafted, bool skipRemovingResultResource = false)
        {
            if (player == null || selectedRecipe == null) return;
            var inventory = player.GetInventory();
            if (inventory == null) return;

            // Preserve craft upgrade multiplier if present
            var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
            int craftUpgrade = 1;
            if (craftUpgradeField != null)
            {
                object value = craftUpgradeField.GetValue(gui);
                if (value is int q && q > 1)
                    craftUpgrade = q;
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
            }
            catch { resultName = null; }

            // Get resources collection
            var resourcesObj = GetMember(selectedRecipe, "m_resources");
            var resources = resourcesObj as System.Collections.IEnumerable;
            if (resources == null)
            {
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

                // 3) token contains (inventory name contains resource token)
                TryRemove(it => it.m_shared.m_name != null && resourceName != null &&
                                 it.m_shared.m_name.IndexOf(resourceName, StringComparison.OrdinalIgnoreCase) >= 0);

                // 4) token contained in resource name (reverse)
                TryRemove(it => it.m_shared.m_name != null && resourceName != null &&
                                 resourceName.IndexOf(it.m_shared.m_name, StringComparison.OrdinalIgnoreCase) >= 0);

                // 5) fallback: try RemoveItem API (best-effort)
                if (remaining > 0)
                {
                    try
                    {
                        inventory.RemoveItem(resourceName, remaining);
                        remaining = 0;
                    }
                    catch
                    {
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
            }

            // Build filtered list that excludes the recipe result resource if requested.
            List<(object req, string name, int amount)> validReqsFiltered = validReqs;
            if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName))
            {
                validReqsFiltered = validReqs.Where(v => !string.Equals(v.name, resultName, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // --- SINGLE-RESOURCE CASE (handle first) ---
            if (validReqs.Count == 1 && resourceList.Count <= 1)
            {
                var single = validReqs[0];
                // If skipping removal for result-resource and the only requirement is the result item, skip removing it
                if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) && string.Equals(single.name, resultName, StringComparison.OrdinalIgnoreCase))
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResources: single-resource recipe is upgrade-result — skipping resource removal of result item.");
                    return;
                }
                try
                {
                    RemoveAmountFromInventory(single.name, single.amount);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources single-resource removal failed: {ex}");
                }
                return;
            }

            // --- END SINGLE-RESOURCE HANDLING ---

            // If crafting failed: remove all required resources EXCEPT one random resource.
            if (!crafted)
            {
                if (validReqs.Count == 0) return;

                // If filtered list is empty (all requirements were the result-resource and we skip them), nothing to remove
                if (validReqsFiltered.Count == 0) return;

                int keepIndex = UnityEngine.Random.Range(0, validReqsFiltered.Count);
                var keepTuple = validReqsFiltered[keepIndex];

                bool skippedKeep = false;
                foreach (var req in resourceList)
                {
                    var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                    if (shared == null) continue;

                    var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                    string resourceName = nameObj as string;
                    if (string.IsNullOrEmpty(resourceName)) continue;

                    // If requested, never remove the resource equal to the recipe result
                    if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) &&
                        string.Equals(resourceName, resultName, StringComparison.OrdinalIgnoreCase))
                    {
                        // preserve the base/upgrading item resource
                        continue;
                    }

                    int amount = ToInt(GetMember(req, "m_amount")) * ((ToInt(GetMember(req, "m_amountPerLevel")) > 0 && craftUpgrade > 1) ? craftUpgrade : 1);
                    if (amount <= 0) continue;

                    if (!skippedKeep && resourceName == keepTuple.name && amount == keepTuple.amount)
                    {
                        skippedKeep = true;
                        continue;
                    }

                    try
                    {
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

                // If requested and this resource equals the result, skip removing it
                if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) &&
                    string.Equals(resourceName, resultName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int amount = ToInt(GetMember(req, "m_amount")) * ((ToInt(GetMember(req, "m_amountPerLevel")) > 0 && craftUpgrade > 1) ? craftUpgrade : 1);
                if (amount <= 0) continue;

                try
                {
                    RemoveAmountFromInventory(resourceName, amount);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources removal failed: {ex}");
                }
            }
        }
    }
}