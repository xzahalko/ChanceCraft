using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ChanceCraft
{
    public partial class ChanceCraft
    {
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
                LogDebugIf (VERBOSE_DEBUG, $"Extracted itemType = {(itemType.HasValue ? itemType.Value.ToString() : "<null>")}");
            }
            catch (Exception ex)
            {
                LogWarning("Failed to extract itemType via direct access: " + ex);
                itemType = null;
            }

            bool typeAllowed =
                itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.Bow ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                itemType == ItemDrop.ItemData.ItemType.Shield ||
                itemType == ItemDrop.ItemData.ItemType.Helmet ||
                itemType == ItemDrop.ItemData.ItemType.Chest ||
                itemType == ItemDrop.ItemData.ItemType.Legs ||
                itemType == ItemDrop.ItemData.ItemType.Ammo;

            LogDebugIf (VERBOSE_DEBUG, $"typeAllowed = {typeAllowed}");

            float totalModifiedPercentage = 0f;
            bool foundAnyConfiguredMatch = false;

            var plugin = BepInEx.Bootstrap.Chainloader.ManagerObject.GetComponent<ChanceCraft>();

            if (typeAllowed)
            {
                LogDebugIf (VERBOSE_DEBUG, "Item type is NOT whitelisted. Checking player inventory for configured prefabs to possibly increase chance.");

                if (plugin == null)
                {
                    LogWarning("ChanceCraft plugin instance not found (plugin == null). Can't access configured lines. Aborting inventory checks.");
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
                            System.Collections.IEnumerable invItems = null;
                            var getAll = inv.GetType().GetMethod("GetAllItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                            if (getAll != null)
                            {
                                try
                                {
                                    invItems = (System.Collections.IEnumerable)getAll.Invoke(inv, null);
                                    LogDebugIf (VERBOSE_DEBUG, "Obtained inventory items via GetAllItems().");
                                }
                                catch (Exception ex)
                                {
                                    LogWarning("GetAllItems() invocation failed: " + ex);
                                    invItems = null;
                                }
                            }

                            if (invItems == null)
                            {
                                var fi = inv.GetType().GetField("m_inventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                         ?? inv.GetType().GetField("m_items", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (fi != null)
                                {
                                    try
                                    {
                                        invItems = fi.GetValue(inv) as System.Collections.IEnumerable;
                                        LogDebugIf (VERBOSE_DEBUG, $"Obtained inventory items via field '{fi.Name}'.");
                                    }
                                    catch (Exception ex)
                                    {
                                        LogWarning("Accessing inventory field failed: " + ex);
                                        invItems = null;
                                    }
                                }
                                else
                                {
                                    LogDebugIf (VERBOSE_DEBUG, "No inventory field found (m_inventory / m_items).");
                                }
                            }

                            if (invItems == null)
                            {
                                LogWarning("Failed to enumerate inventory items (invItems == null).\n");
                            }
                            else
                            {
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
                                        LogDebugIf (VERBOSE_DEBUG, "extractPrefabName exception: " + ex);
                                    }
                                    return null;
                                };

                                Action<object> removeOne = (invItemObj) =>
                                {
                                    try
                                    {
                                        var itemTypeInv = invItemObj.GetType();

                                        var removeMethod = inv.GetType().GetMethod("RemoveItem", new Type[] { itemTypeInv, typeof(int) });
                                        if (removeMethod != null)
                                        {
                                            try
                                            {
                                                removeMethod.Invoke(inv, new object[] { invItemObj, 1 });
                                                LogDebugIf (VERBOSE_DEBUG, "Removed 1 item (RemoveItem(itemData, int) used).\n");
                                                return;
                                            }
                                            catch (Exception ex) { LogDebugIf (VERBOSE_DEBUG, "RemoveItem(itemData, int) failed: " + ex); }
                                        }

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
                                            catch (Exception ex) { LogDebugIf (VERBOSE_DEBUG, "Failed to read shared.m_name: " + ex); displayName = null; }
                                        }

                                        if (!string.IsNullOrWhiteSpace(displayName))
                                        {
                                            var removeByName = inv.GetType().GetMethod("RemoveItem", new Type[] { typeof(string), typeof(int) });
                                            if (removeByName != null)
                                            {
                                                try
                                                {
                                                    removeByName.Invoke(inv, new object[] { displayName, 1 });
                                                    LogDebugIf (VERBOSE_DEBUG, $"Removed 1 item by name ('{displayName}') via RemoveItem(string, int).\n");
                                                    return;
                                                }
                                                catch (Exception ex) { LogDebugIf (VERBOSE_DEBUG, "RemoveItem(string,int) failed: " + ex); }
                                            }
                                        }

                                        var removeMethodSimple = inv.GetType().GetMethod("RemoveItem", new Type[] { itemTypeInv });
                                        if (removeMethodSimple != null)
                                        {
                                            try
                                            {
                                                removeMethodSimple.Invoke(inv, new object[] { invItemObj });
                                                LogDebugIf (VERBOSE_DEBUG, "Removed 1 item (RemoveItem(itemData) used).\n");
                                                return;
                                            }
                                            catch (Exception ex) { LogDebugIf (VERBOSE_DEBUG, "RemoveItem(itemData) failed: " + ex); }
                                        }

                                        var stackField = itemTypeInv.GetField("m_stack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        if (stackField != null)
                                        {
                                            try
                                            {
                                                var stackVal = stackField.GetValue(invItemObj);
                                                if (stackVal is int sv && sv > 1)
                                                {
                                                    stackField.SetValue(invItemObj, sv - 1);
                                                    LogDebugIf (VERBOSE_DEBUG, "Decremented m_stack by 1 as a fallback removal.");
                                                    return;
                                                }
                                                else
                                                {
                                                    LogDebugIf (VERBOSE_DEBUG, "m_stack present but not >1; cannot decrement to remove one reliably.");
                                                }
                                            }
                                            catch (Exception ex) { LogWarning("m_stack decrement failed: " + ex); }
                                        }

                                        LogWarning("Could not remove item via any known method; item may remain in inventory.");
                                    }
                                    catch (Exception ex) { LogDebugIf (VERBOSE_DEBUG, "removeOne outer exception: " + ex); }
                                };

                                var configLines = plugin.GetConfiguredLines();
                                LogDebugIf(VERBOSE_DEBUG, $"Configured lines count: {(configLines == null ? configLines.ToString() : "<null>")}\n");

                                foreach (var line in configLines)
                                {
                                    if (line == null || !line.IsActive)
                                    {
                                        LogDebugIf (VERBOSE_DEBUG, "Skipping inactive or null config line.");
                                        continue;
                                    }

                                    LogDebugIf (VERBOSE_DEBUG, $"Checking config line: ItemPrefab='{line.ItemPrefab}', IncreaseCraftPercentage={line.IncreaseCraftPercentage}\n");

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
                                    LogDebugIf (VERBOSE_DEBUG, $"Checked {checkedCount} inventory entries for prefab '{line.ItemPrefab}'.\n");

                                    if (matchedItemObj != null)
                                    {
                                        totalModifiedPercentage += line.IncreaseCraftPercentage;
                                        foundAnyConfiguredMatch = true;
                                        LogDebugIf (VERBOSE_DEBUG, $"Matched inventory prefab '{line.ItemPrefab}'. Increase +{line.IncreaseCraftPercentage}. Attempting to remove one instance.\n");
                                        removeOne(matchedItemObj);
                                        LogDebugIf (VERBOSE_DEBUG, $"Attempted removal for '{line.ItemPrefab}'.\n");
                                    }
                                    else
                                    {
                                        LogDebugIf (VERBOSE_DEBUG, $"No inventory match found for prefab '{line.ItemPrefab}'.\n");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (typeAllowed || !foundAnyConfiguredMatch)
            {
                LogDebugIf (VERBOSE_DEBUG, "Item matcher criteria and founded percentage increase item in inventory.");
                return null;
            }

            try
            {
                if (plugin != null && totalModifiedPercentage > 0f)
                {
                    LogInfo($"ChanceCraft: total modified percentage from inventory consumed items = {totalModifiedPercentage * 100f}%\n");
                }
            }
            catch (Exception ex) { LogDebugIf (VERBOSE_DEBUG, "Final logging failed: " + ex); }

            if (foundAnyConfiguredMatch)
            {
                float percent = totalModifiedPercentage * 100f;
                string text = $"<color=yellow>Crafting percentage increased by {percent:0.##}%</color>";
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
                LogInfo($"TrySpawnCraftEffect-DBG: recipe={recipeKeyDbg} itemType={itemType} quality={qualityLevel} craftChance={craftChanceAdjusted:F3} upgradeChance={upgradeChanceAdjusted:F3} chosenChance={chosenChance:F3} rand={randVal:F3} suppressed={suppressedThisOperation} isUpgradeCall={isUpgradeCall}\n");
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
                            LogInfo($"TrySpawnCraftEffect-DBG SUCCESS UPGRADE: recipeToUse={recipeKey} targetHash={targetHash} preSnapshotCount={snapshotCount} preSnapshotData={snapshotDataCount}\n");
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
                        LogWarning($"TrySpawnCraftEffect success/upgrade removal exception: {ex}\n");
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
                                LogInfo($"TrySpawnCraftEffect-DBG suppressed-success treating as UPGRADE: recipe={ChanceCraftRecipeHelpers.RecipeFingerprint(recipeToUse)} target={targetHashLog}\n");

                                ChanceCraftResourceHelpers.RemoveRequiredResourcesUpgrade(gui, player, recipeToUse, targetItem, true);
                            }
                            else
                            {
                                ChanceCraftResourceHelpers.RemoveRequiredResources(gui, player, selectedRecipe, true, false);
                            }
                        }
                        catch (Exception ex) { LogWarning($"TrySpawnCraftEffect success removal exception: {ex}\n"); }

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
                            LogInfo($"TrySpawnCraftEffect-DBG FAILURE UPGRADE: recipe={recipeKey} preSnapshotData={preSnapshotEntries} upgradeTargetIndex={_upgradeTargetItemIndex}\n");
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
                            LogInfo($"TrySpawnCraftEffect-DBG BEFORE removal: targetHash={targetHash} wood={woodBefore} scraps={scrapBefore} hides={hideBefore} didRevertAny={false}\n");
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
                    try { Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=red>Crafting failed!</color>"); } catch { }

                    ClearCapturedUpgradeGui();
                    return selectedRecipe;
                }
            }
        }
    }
}