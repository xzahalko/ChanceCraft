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
        // FIX: Correct the syntax for ConfigEntry<float> declaration and remove stray '}'
        private static ConfigEntry<float> myBossEithkyrStrength;

        public const string pluginID = "deep.ChanceCraft";
        public const string pluginName = "Chance Craft";
        public const string pluginVersion = "1.1.0";

        private readonly Harmony harmony = new Harmony(pluginID);

        //        internal static readonly ConfigSync configSync = new ConfigSync(pluginID) { DisplayName = pluginName, CurrentVersion = pluginVersion, MinimumRequiredVersion = pluginVersion };

        public static ChanceCraft instance;

        public static ConfigEntry<bool> modEnabled;
        public static ConfigEntry<bool> configLocked;
        public static ConfigEntry<bool> loggingEnabled;

        private static ConfigEntry<bool> _loggingEnabled;

        private Harmony _harmony;
        private static bool IsDoCraft;

        [UsedImplicitly]
        void Awake()
        {
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), "ChanceCraft");

            instance = this;

            // Remove this line for better randomness, or move it to a static constructor or only run once per game session.
            // UnityEngine.Random.InitState((int)DateTime.Now.Ticks ^ Environment.TickCount ^ Guid.NewGuid().GetHashCode());

            _loggingEnabled = Config.Bind("Logging", "Logging Enabled", true, "Enable logging");
            // In Awake(), replace the old successChance config with the following:
            weaponSuccessChance = Config.Bind("General", "WeaponSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft weapons (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));
            armorSuccessChance = Config.Bind("General", "ArmorSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft armors (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));
            arrowSuccessChance = Config.Bind("General", "ArrowSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft arrows (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));
//            myBossEithkyrStrength = Config.Bind("General", "Eithkyr stength", 0.6f, new ConfigDescription("Strength of Eithkyr (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));
            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting weapons success chance set to {weaponSuccessChance.Value * 100}%.");
            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting armors success chance set to {armorSuccessChance.Value * 100}%.");
            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting arrow success chance set to {arrowSuccessChance.Value * 100}%.");

            UnityEngine.Debug.LogWarning("[ChanceCraft] Awake called!");

//            myBossEithkyrStrength.SettingChanged += (sender, args) => {
//                UpdateBossStrength();
//            };

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
                    character.m_health = myBossEithkyrStrength.Value*1000;
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

        public static void RemoveCraftedItem(Player player, Recipe recipe)
        {
            if (player == null || recipe == null || recipe.m_item == null) return;
            var inventory = player.GetInventory();
            UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveCraftedItem called!");

            if (inventory == null) return;

            // Use crafted count from recipe (e.g. arrows: recipe.m_amount)
            int craftedCount = recipe.m_amount > 0 ? recipe.m_amount : 1;
            var craftedName = recipe.m_item.m_itemData.m_shared.m_name;
            var craftedQuality = recipe.m_item.m_itemData.m_quality;
            var craftedVariant = recipe.m_item.m_itemData.m_variant;

            var items = inventory.GetAllItems();
            for (int i = items.Count - 1; i >= 0 && craftedCount > 0; i--)
            {
                var item = items[i];
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
                        inventory.RemoveItem(item);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveCraftedItem removed item (stack now 0): {item.m_shared.m_name}");
                    }
                }
            }

            // Fallback: try removing by name and stack if any left
            if (craftedCount > 0)
            {
                inventory.RemoveItem(craftedName, craftedCount);
                UnityEngine.Debug.LogWarning($"[ChanceCraft] Remove crafted item by name and stack: attempted {craftedCount}");
            }
        }

        // --- Modified parts of ChanceCraft.cs: Harmony patch and RemoveRequiredResources ---

        // Harmony patch for InventoryGui.DoCrafting: suppress default resource removal for eligible recipes
        // --- InventoryGui.DoCrafting patch (replace existing class) ---
        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        static class InventoryGuiDoCraftingPatch
        {
            private static readonly Dictionary<Recipe, object> _savedResources = new Dictionary<Recipe, object>();

            // Per-call state (reset at Prefix)
            private static bool _suppressedThisCall = false;
            private static Recipe _savedRecipeForCall = null;

            [UsedImplicitly]
            static void Prefix(InventoryGui __instance)
            {
                // reset per-call state
                _suppressedThisCall = false;
                _savedRecipeForCall = null;

                try
                {
                    // get the selected recipe object (handle RecipeDataPair wrapper)
                    var selectedRecipeField = typeof(InventoryGui).GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (selectedRecipeField == null) return;

                    object value = selectedRecipeField.GetValue(__instance);
                    Recipe selectedRecipe = null;
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

                    if (selectedRecipe == null) return;

                    // Save exact recipe instance for the call (so Postfix uses same object)
                    _savedRecipeForCall = selectedRecipe;

                    // Detect upgrade early (do NOT touch m_resources if this is an upgrade)
                    var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (craftUpgradeField != null)
                    {
                        try
                        {
                            object cv = craftUpgradeField.GetValue(__instance);
                            if (cv is int v && v > 1)
                            {
                                // upgrade — let game handle it, do not suppress
                                return;
                            }
                        }
                        catch { /* ignore */ }
                    }

                    // Build positive requirements list (skip zero amounts). If only one valid requirement -> don't suppress.
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

                    // Only suppress when recipe has multiple positive requirements (plugin's keep-one-on-fail behavior)
                    if (validReqs.Count <= 1)
                    {
                        // don't suppress single-resource recipes (lets the game remove them and avoids upgrade interference)
                        return;
                    }

                    // Eligible item types filter (optional; preserve previous behavior)
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

                    // Save original resources so we can restore in Postfix
                    lock (_savedResources)
                    {
                        if (!_savedResources.ContainsKey(selectedRecipe))
                            _savedResources[selectedRecipe] = resourcesObj;
                    }

                    // Replace m_resources with an empty collection (so the game's DoCrafting does not remove materials)
                    Type fieldType = resourcesField.FieldType;
                    object empty = null;
                    if (fieldType.IsArray)
                        empty = Array.CreateInstance(fieldType.GetElementType(), 0);
                    else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
                        empty = Activator.CreateInstance(fieldType);

                    if (empty != null)
                    {
                        resourcesField.SetValue(selectedRecipe, empty);
                        _suppressedThisCall = true;
                        // mark IsDoCraft only if we suppressed (so other code knows)
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
                    // Restore the original m_resources only for the exact saved recipe we suppressed.
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

                    // If we didn't suppress for this call, do nothing (the game handled resource removal, or it was an upgrade).
                    if (!_suppressedThisCall)
                    {
                        // clear per-call state and exit
                        _suppressedThisCall = false;
                        _savedRecipeForCall = null;
                        IsDoCraft = false;
                        return;
                    }

                    // We suppressed resources in Prefix (for this recipe). Run ChanceCraft logic for the exact recipe.
                    var recipeForLogic = _savedRecipeForCall;
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;

                    // Call TrySpawnCraftEffect and RemoveCraftedItem using the recipe instance we controlled.
                    Recipe recept = ChanceCraft.TrySpawnCraftEffect(__instance, recipeForLogic);
                    if (player != null && recept != null)
                    {
                        ChanceCraft.RemoveCraftedItem(player, recept);
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix exception: {ex}");
                    // reset defensive
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;
                }
            }
        }

        public static List<ItemDrop.ItemData> GetCraftedItemsFromInventory(Player player, Recipe recipe)
        {
            var craftedItems = new List<ItemDrop.ItemData>();
            if (player == null || recipe == null || recipe.m_item == null) return craftedItems;

            var craftedName = recipe.m_item.m_itemData.m_shared.m_name;
            var inventory = player.GetInventory();
            if (inventory == null) return craftedItems;

            foreach (var item in inventory.GetAllItems())
            {
                if (item.m_shared.m_name == craftedName)
                {
                    craftedItems.Add(item);
                }
            }
            return craftedItems;
        }

        public static void RemoveRequiredResources(InventoryGui gui, Player player, Recipe selectedRecipe, Boolean crafted)
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
                        // swallow - we'll report below if nothing removed
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

            // --- SINGLE-RESOURCE CASE (handle first) ---
            if (validReqs.Count == 1 && resourceList.Count <= 1)
            {
                var single = validReqs[0];
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
            // If resourceList contains multiple entries but only one valid (positive) requirement,
            // treat it as a single valid requirement on either success or failure.
            if (!crafted && validReqs.Count == 1)
            {
                var only = validReqs[0];
                try
                {
                    RemoveAmountFromInventory(only.name, only.amount);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources single valid requirement removal failed: {ex}");
                }
                return;
            }
            // --- END SINGLE-RESOURCE HANDLING ---

            // If crafting failed: remove all required resources EXCEPT one random resource.
            if (!crafted)
            {
                if (validReqs.Count == 0) return;

                int keepIndex = UnityEngine.Random.Range(0, validReqs.Count);
                var keepTuple = validReqs[keepIndex];

                bool skippedKeep = false;
                foreach (var req in resourceList)
                {
                    var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                    if (shared == null) continue;

                    var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                    string resourceName = nameObj as string;
                    if (string.IsNullOrEmpty(resourceName)) continue;

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

        /*
                public static bool IsChanceCraftRepairable(ItemDrop.ItemData item)
                {
                    if (item == null) return false;
        //            if (item.m_customData != null && item.m_customData.TryGetValue("ChanceCraft_NoRepair", out var val) && val == "1")
        //                return true;
                    // Fallback to original logic if needed, or return true if you want all other items to be repairable
                    return false;
                }
        */

        /*
                [HarmonyPatch(typeof(InventoryGui), "UpdateRepair")]
                public static class InventoryGui_UpdateRepair_Patch
                {
                    static void Postfix(InventoryGui __instance)
                    {
                        var player = Player.m_localPlayer;
                        if (player == null) return;
                        var inventory = player.GetInventory();
                        if (inventory == null) return;

                        foreach (var item in inventory.GetAllItems())
                        {
                            if (ChanceCraft.IsChanceCraftRepairable(item) )
                            {
                                item.m_shared.m_canBeReparied = false;
                            }
                        }
                    }
                }
        */
        // Replace signature and the initial selection logic with this:
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

            // --- Only apply to weapons, armors, and arrows ---
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
            // -------------------------------------------------

            UnityEngine.Debug.LogWarning("[chancecraft] berore rand");
            float rand = UnityEngine.Random.value;
            var player = Player.m_localPlayer;

            UnityEngine.Debug.LogWarning($"[chancecraft] successChance.Value = {rand}");
            // Determine the correct success chance based on item type
            float chance = 0.6f; // default fallback
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
                UnityEngine.Debug.LogWarning("[chancecraft] povedlo se");
                // Success: spawn crafting effect at player position using Create(pos, rot)
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
                            createMethod.Invoke(m_craftItemEffects, new object[] { Player.m_localPlayer.transform.position, Quaternion.identity });
                        }
                    }
                }

                // Add null checks and ensure you are modifying the correct item instance.
                // Also, make sure the inventory is refreshed after modification if needed.

                var craftedName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                var craftedQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                var craftedVariant = selectedRecipe.m_item?.m_itemData?.m_variant ?? 0;
                int craftedCount = selectedRecipe.m_amount > 0 ? selectedRecipe.m_amount : 1;

                var items = player.GetInventory()?.GetAllItems();
                if (items != null && craftedName != null)
                {
                    for (int i = items.Count - 1; i >= 0 && craftedCount > 0; i--)
                    {
                        var item = items[i];
                        if (item != null &&
                            item.m_shared.m_name == craftedName &&
                            item.m_quality == craftedQuality &&
                            item.m_variant == craftedVariant)
                        {
                            int toModify = Math.Min(item.m_stack, craftedCount);
//                            item.m_durability = item.GetMaxDurability() * UnityEngine.Random.value;
//                            item.m_customData["ChanceCraft_NoRepair"] = "1";
//                            UnityEngine.Debug.LogWarning($"[ChanceCraft] Set durability of {item.m_shared.m_name} to {item.m_durability}, not repairable");
                            craftedCount -= toModify;
                        }
                    }
                    // Optionally, force inventory update:
//                    player.GetInventory().Changed();
                }

                RemoveRequiredResources(gui, player, selectedRecipe, true);

                UnityEngine.Debug.LogWarning("[chancecraft] removed materials ok ...");

                return null; // Crafting succeeds
            }
            else
            {
                // Remove all resources
                UnityEngine.Debug.LogWarning("[chancecraft] failed");

                RemoveRequiredResources(gui, player, selectedRecipe, false);

                // Show red message
                player.Message(MessageHud.MessageType.Center, "<color=red>Crafting failed!</color>");
                return selectedRecipe;
            }
        }

        //[HarmonyPatch(typeof(InventoryGui), "Craft")]
        //[HarmonyPostfix]
        //private static void Craft_Postfix(InventoryGui __instance)
        //{
        //    UnityEngine.Debug.LogWarning("[ChanceCraft] Craft_Postfix called!");

        //    var selectedRecipeField = typeof(InventoryGui).GetField("m_selectedRecipe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        //    var selectedRecipe = selectedRecipeField?.GetValue(__instance) as Recipe;

        //    if (selectedRecipe == null || Player.m_localPlayer == null)
        //        return;

        //    // Only apply to weapons and armor (including modded)
        //    var item = selectedRecipe.m_item;
        //    if (item == null || !(item.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield ||
        //                          item.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Helmet ||
        //                          item.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Chest ||
        //                          item.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Legs))
        //        return;

        //    float rand = UnityEngine.Random.value;
        //    UnityEngine.Debug.LogWarning($"[chancecraft] successChance.Value = {successChance?.Value}");
        //    if (rand <= successChance.Value)
        //    {
        //        UnityEngine.Debug.LogWarning("[chancecraft] povedlo se");
        //        // Success: spawn crafting effect at player position
        //        var craftingStationField = typeof(InventoryGui).GetField("currentCraftingStation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        //        var craftingStation = craftingStationField?.GetValue(__instance);
        //        if (craftingStation != null)
        //        {
        //            var m_craftItemEffectsField = craftingStation.GetType().GetField("m_craftItemEffects", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        //            var m_craftItemEffects = m_craftItemEffectsField?.GetValue(craftingStation);
        //            if (m_craftItemEffects != null)
        //            {
        //                var createMethod = m_craftItemEffects.GetType().GetMethod("Create", new Type[] { typeof(Vector3), typeof(Quaternion), typeof(Transform), typeof(float), typeof(int) });
        //                if (createMethod != null)
        //                {
        //                    createMethod.Invoke(m_craftItemEffects, new object[] { Player.m_localPlayer.transform.position, Quaternion.identity, null, 1f, -1 });
        //                }
        //            }
        //        }
        //        return; // Crafting succeeds
        //    }
        //    else
        //    {
        //        // Remove all resources
        //        UnityEngine.Debug.LogWarning("[chancecraft] failed");
        //        var player = Player.m_localPlayer;
        //        foreach (var req in selectedRecipe.m_resources)
        //        {
        //            var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        //            int craftUpgrade = 1;
        //            if (craftUpgradeField != null)
        //            {
        //                object value = craftUpgradeField.GetValue(__instance);
        //                if (value is int q && q > 1)
        //                    craftUpgrade = q;
        //            }
        //            int amount = req.m_amount * ((req.m_amountPerLevel > 0 && craftUpgrade > 1) ? craftUpgrade : 1);
        //            player.GetInventory().RemoveItem(req.m_resItem.m_itemData.m_shared.m_name, amount);
        //        }

        //        // Show red message
        //        player.Message(MessageHud.MessageType.Center, "<color=red>Crafting failed!</color>");
        //        return;
        //    }
        //}
    }
}
