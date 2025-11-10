using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ChanceCraft
{
    // Resource removal helpers extracted from ChanceCraft.cs
    public static class ChanceCraftResourceHelpers
    {
        // These helpers intentionally use ChanceCraftPlugin's exposed internal state (locks/flags) where needed.
        // They replicate the logic from ChanceCraft.cs but are separated for clarity.

        // Remove amount from an inventory while trying to avoid the upgrade target item reference
        public static int RemoveAmountFromInventorySkippingTarget(Inventory inventory, ItemDrop.ItemData upgradeTargetItem, string resourceName, int amount)
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
                ChanceCraft.LogInfo($"RemoveAmountFromInventorySkippingTarget: requested={amount} removed={removedTotal} resource={resourceName} targetHash={targetHash}");
            }
            catch { }

            return amount - remaining;
        }

        // Simple local remove helper used by some code paths (operates on Player.m_localPlayer)
        public static void RemoveAmountFromInventoryLocal(string resName, int amount)
        {
            try
            {
                if (string.IsNullOrEmpty(resName) || amount <= 0) return;
                var player = Player.m_localPlayer;
                if (player == null) return;
                var inv = player.GetInventory();
                if (inv == null) return;

                int toRemove = amount;

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

                ChanceCraft.LogInfo($"RemoveAmountFromInventoryLocal: requested={amount} name={resName} removed={amount - toRemove}");
            }
            catch (Exception ex)
            {
                ChanceCraft.LogWarning($"RemoveAmountFromInventoryLocal exception: {ex}");
            }
        }

        // Public RemoveRequiredResources ported from ChanceCraft.cs and refactored to call local helpers.
        public static void RemoveRequiredResources(InventoryGui gui, Player player, Recipe selectedRecipe, bool crafted, bool skipRemovingResultResource = false)
        {
            if (player == null || selectedRecipe == null) return;
            var inventory = player.GetInventory();
            if (inventory == null) return;

            string removalKey = null;
            bool removalKeyAdded = false;
            try
            {
                try
                {
                    var recipeKeyNow = ChanceCraftRecipeHelpers.RecipeFingerprint(selectedRecipe);
                    var target = ChanceCraftRecipeHelpers.GetSelectedInventoryItem(gui);
                    var targetHash = target != null ? RuntimeHelpers.GetHashCode(target).ToString("X") : "null";
                    removalKey = $"{recipeKeyNow}|t:{targetHash}|crafted:{crafted}";
                }
                catch { removalKey = null; }

                if (!string.IsNullOrEmpty(removalKey))
                {
                    lock (ChanceCraft._recentRemovalKeysLock)
                    {
                        if (ChanceCraft._recentRemovalKeys.Contains(removalKey))
                        {
                            ChanceCraft.LogInfo($"RemoveRequiredResources: skipping duplicate removal for {removalKey}");
                            return;
                        }
                        ChanceCraft._recentRemovalKeys.Add(removalKey);
                        removalKeyAdded = true;
                    }

                    try
                    {
                        var st = new System.Diagnostics.StackTrace(1, false);
                        var frame = st.GetFrame(0);
                        var caller = frame != null ? $"{frame.GetMethod()?.DeclaringType?.Name}.{frame.GetMethod()?.Name}" : "unknown";
                        ChanceCraft.LogInfo($"RemoveRequiredResources CALLER: removalKey={removalKey} caller={caller}");
                    }
                    catch { }
                }

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

                int RemoveAmountFromInventoryLocalInt(string resourceName, int amount)
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
                            if (!predicate(it)) continue;
                            int toRemove = Math.Min(it.m_stack, remaining);
                            it.m_stack -= toRemove;
                            remaining -= toRemove;
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

                        bool isUpgradeNow = ChanceCraft._isUpgradeDetected || ChanceCraftRecipeHelpers.IsUpgradeOperation(gui, selectedRecipe);
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
                            continue;
                        }

                        try
                        {
                            int removed = RemoveAmountFromInventoryLocalInt(req.name, req.amount);
                        }
                        catch (Exception ex)
                        {
                            ChanceCraft.LogWarning($"RemoveRequiredResources removal exception: {ex}");
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
                        int removed = RemoveAmountFromInventoryLocalInt(req.name, req.amount);
                    }
                    catch (Exception ex)
                    {
                        ChanceCraft.LogWarning($"RemoveRequiredResources removal exception: {ex}");
                    }
                }
            }
            finally
            {
                try
                {
                    if (removalKeyAdded && !string.IsNullOrEmpty(removalKey))
                    {
                        lock (ChanceCraft._recentRemovalKeysLock)
                        {
                            ChanceCraft._recentRemovalKeys.Remove(removalKey);
                        }
                    }
                }
                catch { }
            }
        }

        // RemoveRequiredResourcesUpgrade: uses per-level semantics and tries to avoid removing the upgrade target instance
        public static void RemoveRequiredResourcesUpgrade(InventoryGui gui, Player player, Recipe selectedRecipe, ItemDrop.ItemData upgradeTarget, bool crafted)
        {
            if (player == null || selectedRecipe == null) return;
            var inventory = player.GetInventory();
            if (inventory == null) return;

            try
            {
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

                        int effectivePerLevel = perLevel > 0 ? perLevel : baseAmount;
                        if (effectivePerLevel > 0) validReqs.Add((resName, effectivePerLevel));
                    }
                    catch { }
                }

                if (validReqs.Count == 0) return;

                string resultName = null;
                try { resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name; } catch { resultName = null; }

                int RemoveAmountSkippingTargetLocal(string resourceName, int amount)
                {
                    try
                    {
                        var method = typeof(ChanceCraftResourceHelpers).GetMethod("RemoveAmountFromInventorySkippingTarget", BindingFlags.Static | BindingFlags.Public);
                        if (method != null)
                        {
                            object removed = method.Invoke(null, new object[] { inventory, upgradeTarget, resourceName, amount });
                            if (removed is int ri) return ri;
                        }
                    }
                    catch { }

                    int remaining = amount;
                    var items = inventory.GetAllItems();
                    if (items == null) return 0;

                    for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                    {
                        var it = items[i];
                        if (it == null || it.m_shared == null) continue;
                        if (!string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (upgradeTarget != null && (ReferenceEquals(it, upgradeTarget) || RuntimeHelpers.GetHashCode(it) == RuntimeHelpers.GetHashCode(upgradeTarget))) continue;
                        int toRemove = Math.Min(it.m_stack, remaining);
                        it.m_stack -= toRemove;
                        remaining -= toRemove;
                        if (it.m_stack <= 0)
                        {
                            try { inventory.RemoveItem(it); } catch { }
                        }
                    }

                    if (remaining > 0)
                    {
                        for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                        {
                            var it = items[i];
                            if (it == null || it.m_shared == null) continue;
                            if (!string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase)) continue;
                            int toRemove = Math.Min(it.m_stack, remaining);
                            it.m_stack -= toRemove;
                            remaining -= toRemove;
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
                    var candidates = validReqs.Where(r => !(string.Equals(r.name, resultName, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (candidates.Count == 0)
                    {
                        candidates = validReqs;
                    }

                    int idx = UnityEngine.Random.Range(0, candidates.Count);
                    var chosen = candidates[idx];
                    int amountToRemove = chosen.perLevel * Math.Max(1, craftUpgrade);

                    int removed = RemoveAmountSkippingTargetLocal(chosen.name, amountToRemove);
                    return;
                }
                else
                {
                    foreach (var req in validReqs)
                    {
                        if (!string.IsNullOrEmpty(resultName) && string.Equals(req.name, resultName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        int amountToRemove = req.perLevel * Math.Max(1, craftUpgrade);

                        int removed = 0;
                        try
                        {
                            removed = RemoveAmountSkippingTargetLocal(req.name, amountToRemove);
                        }
                        catch (Exception ex) { ChanceCraft.LogWarning($"RemoveRequiredResourcesUpgrade removal exception: {ex}"); }
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                ChanceCraft.LogWarning($"RemoveRequiredResourcesUpgrade exception: {ex}");
            }
        }
    }
}
