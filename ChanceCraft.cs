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

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        static class InventoryGuiDoCraftingPatch
        {
            [UsedImplicitly]
            static void Prefix(InventoryGui __instance)
            {
                // Mark that DoCrafting is executing so RemoveRequiredResources can avoid double-removal.
                IsDoCraft = true;
            }

            [UsedImplicitly]
            static void Postfix(InventoryGui __instance, Player player)
            {
                // Clear the flag so plugin logic can run after the game's DoCrafting has already removed ingredients.
                IsDoCraft = false;

                // Now run chance-crafting logic that may spawn effects and remove crafted items as required.
                Recipe recept = ChanceCraft.TrySpawnCraftEffect(__instance);

                if (player != null && recept != null)
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] Got crafted item, removing !");
                    ChanceCraft.RemoveCraftedItem(player, recept);
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

            // If we're inside InventoryGui.DoCrafting (patched Prefix sets IsDoCraft = true),
            // the game will already remove the required materials. Skip our removal to avoid double-removal.
            if (IsDoCraft)
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] Skipping RemoveRequiredResources because DoCrafting is in progress (avoids double removal).");
                return;
            }

            // Preserve existing logic to respect craft upgrade multiplier if present
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
                UnityEngine.Debug.LogWarning("[ChanceCraft] selectedRecipe.m_resources is null or not enumerable");
                return;
            }

            // Build a list so we can inspect count and iterate multiple times safely
            var resourceList = resources.Cast<object>().ToList();

            // If the recipe has exactly one required resource, remove it in all cases (always consume it).
            if (resourceList.Count == 1)
            {
                var req = resourceList[0];
                var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                string resourceName = nameObj as string;
                if (!string.IsNullOrEmpty(resourceName))
                {
                    int amount = ToInt(GetMember(req, "m_amount")) * ((ToInt(GetMember(req, "m_amountPerLevel")) > 0 && craftUpgrade > 1) ? craftUpgrade : 1);
                    if (amount > 0)
                    {
                        try
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] Recipe has single requirement - removing: {resourceName} x{amount}");
                            inventory.RemoveItem(resourceName, amount);
                        }
                        catch (Exception ex)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] failed to remove {resourceName} x{amount}: {ex}");
                        }
                    }
                }
                return;
            }

            // --- Only apply plugin custom removal logic for non-eligible item types.
            // If the recipe is eligible for chance-crafting, let the game's default DoCrafting remove the resources.
            var itemType = selectedRecipe.m_item?.m_itemData?.m_shared?.m_itemType;

            // ---------------------------------------------------------------

            // If crafting failed (crafted == false) remove all required resources EXCEPT one random resource.
            if (!crafted)
            {
                // Collect valid requirements together with identifying fields (name + amount)
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

                if (validReqs.Count == 0)
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] No valid requirements to remove on failed craft.");
                    return;
                }

                int keepIndex = UnityEngine.Random.Range(0, validReqs.Count);
                var keepTuple = validReqs[keepIndex];
                UnityEngine.Debug.LogWarning($"[ChanceCraft] Craft failed: keeping one random resource: {keepTuple.name} x{keepTuple.amount}");

                // Remove all other resources. Use value comparison and skip a single matching entry.
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

                    // If this entry matches the chosen keep entry (by name and amount) and we haven't skipped yet, skip it.
                    if (!skippedKeep && resourceName == keepTuple.name && amount == keepTuple.amount)
                    {
                        skippedKeep = true;
                        continue;
                    }

                    try
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] removing requirement material (failed craft): {resourceName} x{amount}");
                        inventory.RemoveItem(resourceName, amount);
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] failed to remove {resourceName} x{amount}: {ex}");
                    }
                }

                return;
            }

            // crafted == true: remove all requirement materials as before (only for non-eligible recipes reached here)
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
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] removing requirement material: {resourceName} x{amount}");
                    inventory.RemoveItem(resourceName, amount);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] failed to remove {resourceName} x{amount}: {ex}");
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
        public static Recipe TrySpawnCraftEffect(InventoryGui gui)
        {
            UnityEngine.Debug.LogWarning("[ChanceCraft] TrySpawnCraftEffect called!");

            Recipe selectedRecipe = null;

            var selectedRecipeField = typeof(InventoryGui).GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            UnityEngine.Debug.LogWarning($"[ChanceCraft] selectedIndexField = {selectedRecipeField}");
            if (selectedRecipeField != null)
            {
                object value = selectedRecipeField.GetValue(gui);
                UnityEngine.Debug.LogWarning($"[ChanceCraft] m_selectedRecipe value type: {value?.GetType()} value: {value}");
                if (value != null && value.GetType().Name == "RecipeDataPair")
                {
                    var recipeProp = value.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (recipeProp != null)
                    {
                        selectedRecipe = recipeProp.GetValue(value) as Recipe;
                    }
                }
                else
                {
                    selectedRecipe = value as Recipe;
                }
            }

            UnityEngine.Debug.LogWarning($"[chancecraft] berore cond selectedRecipe={selectedRecipe} m_localPlayer={Player.m_localPlayer}");
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
