using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ChanceCraft
{
    // Consolidated resource removal and quality/variant unpack helpers
    public static class ChanceCraftResourceHelpers
    {
        #region Logging & small helpers

        private static void LogWarning(string msg)
        {
            if (ChanceCraftPlugin.loggingEnabled?.Value ?? false) UnityEngine.Debug.LogWarning($"[ChanceCraft] {msg}");
        }

        private static void LogInfo(string msg)
        {
            if (ChanceCraftPlugin.loggingEnabled?.Value ?? false) UnityEngine.Debug.Log($"[ChanceCraft] {msg}");
        }

        // Helper: unpack tuple-like object to (quality, variant)
        public static bool TryUnpackQualityVariant(object tupleValue, out int quality, out int variant)
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

            // 5) Last-resort: parse integers from ToString()
            try
            {
                string s = tupleValue.ToString();
                var numbers = new List<int>();
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

        // RemoveRequiredResources: general removal for non-upgrade crafting
        // NOTE: changed to accept Harmony instance (object __instance) and resolve InventoryGui internally.
        public static void RemoveRequiredResources(object __instance, Player player, Recipe selectedRecipe, bool crafted, bool skipRemovingResultResource = false)
        {
            var gui = ChanceCraftUIHelpers.ResolveInventoryGui(__instance);
            if (player == null || selectedRecipe == null) return;
            var inventory = player.GetInventory();
            if (inventory == null) return;

            string removalKey = null;
            bool removalKeyAdded = false;
            try
            {
                try
                {
                    var recipeKeyNow = selectedRecipe != null ? ChanceCraftPlugin.RecipeFingerprint(selectedRecipe) : "null";
                    var target = ChanceCraftPlugin._upgradeTargetItem ?? ChanceCraftRecipeHelpers.GetSelectedInventoryItem(__instance);
                    var targetHash = target != null ? RuntimeHelpers.GetHashCode(target).ToString("X") : "null";
                    removalKey = $"{recipeKeyNow}|t:{targetHash}|crafted:{crafted}";
                }
                catch { removalKey = null; }

                if (!string.IsNullOrEmpty(removalKey))
                {
                    lock (ChanceCraftPlugin._recentRemovalKeysLock)
                    {
                        if (ChanceCraftPlugin._recentRemovalKeys.Contains(removalKey))
                        {
                            LogInfo($"RemoveRequiredResources: skipping duplicate removal for {removalKey}");
                            return;
                        }
                        ChanceCraftPlugin._recentRemovalKeys.Add(removalKey);
                        removalKeyAdded = true;
                    }
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

                int RemoveAmountFromInventoryLocal(string resourceName, int amount)
                {
                    if (string.IsNullOrEmpty(resourceName) || amount <= 0) return 0;

                    int remaining = amount;
                    var items = inventory.GetAllItems();

                    if (ChanceCraftPlugin.VERBOSE_DEBUG)
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
                            if (ChanceCraftPlugin.VERBOSE_DEBUG)
                            {
                                try
                                {
                                    LogInfo($"RemoveAmountFromInventoryLocal-REMOVE: removed={toRemove} from stackHash={RuntimeHelpers.GetHashCode(it):X} name={it.m_shared.m_name} q={it.m_quality} s_before={before} s_after={it.m_stack}");
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
                    if (ChanceCraftPlugin.VERBOSE_DEBUG)
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

                        bool isUpgradeNow = ChanceCraftPlugin._isUpgradeDetected || ChanceCraftRecipeHelpers.IsUpgradeOperation(__instance, selectedRecipe);
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
                            int removed = RemoveAmountFromInventoryLocal(req.name, req.amount);
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
                        int removed = RemoveAmountFromInventoryLocal(req.name, req.amount);
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"RemoveRequiredResources removal exception: {ex}");
                    }
                }
            }
            finally
            {
                try
                {
                    if (removalKeyAdded && !string.IsNullOrEmpty(removalKey))
                    {
                        lock (ChanceCraftPlugin._recentRemovalKeysLock)
                        {
                            ChanceCraftPlugin._recentRemovalKeys.Remove(removalKey);
                        }
                    }
                }
                catch { }
            }
        }

        // RemoveRequiredResourcesUpgrade: prefer GUI-captured normalized requirements; fallback to recipe/ObjectDB resources
        public static void RemoveRequiredResourcesUpgrade(object __instance, Player player, Recipe selectedRecipe, ItemDrop.ItemData upgradeTarget, bool crafted)
        {
            var gui = ChanceCraftUIHelpers.ResolveInventoryGui(__instance);
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

                        int effectivePerLevel = perLevel > 0 ? perLevel : baseAmount;
                        if (effectivePerLevel > 0) validReqs.Add((resName, effectivePerLevel));
                    }
                    catch { }
                }

                if (validReqs.Count == 0) return;

                string resultName = null;
                try { resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name; } catch { resultName = null; }

                int RemoveAmountSkippingTarget(string resourceName, int amount)
                {
                    // Use central helper that removes from inventory avoiding upgradeTarget where possible
                    try
                    {
                        int removed = RemoveAmountFromInventorySkippingTarget(inventory, upgradeTarget, resourceName, amount);
                        return removed;
                    }
                    catch { }

                    int remaining = amount;
                    var items = inventory.GetAllItems();
                    if (items == null) return 0;

                    for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                    {
                        var it = items[i];
                        if (it == null || it.m_shared == null) continue;
                        if (upgradeTarget != null && (ReferenceEquals(it, upgradeTarget) || RuntimeHelpers.GetHashCode(it) == RuntimeHelpers.GetHashCode(upgradeTarget))) continue;
                        if (!string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase)) continue;
                        int toRemove = Math.Min(it.m_stack, remaining);
                        int before = it.m_stack;
                        it.m_stack -= toRemove;
                        remaining -= toRemove;
                        try { LogInfo($"RemoveAmountFromInventorySkippingTarget-REMOVE: removed={toRemove} from stackHash={RuntimeHelpers.GetHashCode(it):X} name={it.m_shared.m_name} q={it.m_quality} s_before={before} s_after={it.m_stack}"); } catch { }
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
                            int before = it.m_stack;
                            it.m_stack -= toRemove;
                            remaining -= toRemove;
                            try { LogInfo($"RemoveAmountFromInventorySkippingTarget-REMOVE-FALLBACK: removed={toRemove} from stackHash={RuntimeHelpers.GetHashCode(it):X} name={it.m_shared.m_name} q={it.m_quality} s_before={before} s_after={it.m_stack}"); } catch { }
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

                    int removed = RemoveAmountSkippingTarget(chosen.name, amountToRemove);
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
                        try
                        {
                            int removed = RemoveAmountSkippingTarget(req.name, amountToRemove);
                        }
                        catch (Exception ex) { LogWarning($"RemoveRequiredResourcesUpgrade removal exception: {ex}"); }
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                LogWarning($"RemoveRequiredResourcesUpgrade exception: {ex}");
            }
        }

        // Slightly more general helper used by RemoveRequiredResourcesUpgrade
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
                LogInfo($"RemoveAmountFromInventorySkippingTarget: requested={amount} removed={removedTotal} resource={resourceName} targetHash={targetHash}");
            }
            catch { }

            return amount - remaining;
        }

        // Force revert after removal: tries to restore item quality in player's inventory conservatively
        public static void ForceRevertAfterRemoval(object __instance, Recipe selectedRecipe, ItemDrop.ItemData upgradeTarget = null)
        {
            var gui = ChanceCraftUIHelpers.ResolveInventoryGui(__instance);
            try
            {
                if (selectedRecipe == null) return;
                string resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(resultName)) return;
                int finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;

                int expectedPreQuality = Math.Max(0, finalQuality - 1);

                try
                {
                    if (ChanceCraftPlugin._preCraftSnapshotData != null && ChanceCraftPlugin._preCraftSnapshotData.Count > 0)
                    {
                        var preQs = ChanceCraftPlugin._preCraftSnapshotData.Values
                            .Select(v => { if (TryUnpackQualityVariant(v, out int a, out int b)) return a; return 0; })
                            .ToList();
                        if (preQs.Count > 0) expectedPreQuality = Math.Max(0, preQs.Max());
                    }
                }
                catch { }

                var inv = Player.m_localPlayer?.GetInventory();
                var all = inv?.GetAllItems();
                if (all == null) return;

                // 1) try to revert explicit upgradeTarget
                if (upgradeTarget != null)
                {
                    try
                    {
                        var found = all.FirstOrDefault(it => it != null && (ReferenceEquals(it, upgradeTarget) || RuntimeHelpers.GetHashCode(it) == RuntimeHelpers.GetHashCode(upgradeTarget)));
                        if (found != null)
                        {
                            int prevQ = expectedPreQuality;
                            int prevV = found.m_variant;
                            try
                            {
                                if (ChanceCraftPlugin._preCraftSnapshotData != null && ChanceCraftPlugin._preCraftSnapshotData.TryGetValue(upgradeTarget, out var tupleVal) && TryUnpackQualityVariant(tupleVal, out int pq, out int pv))
                                {
                                    prevQ = pq;
                                    prevV = pv;
                                }
                                else
                                {
                                    int h = RuntimeHelpers.GetHashCode(found);
                                    if (ChanceCraftPlugin._preCraftSnapshotHashQuality != null && ChanceCraftPlugin._preCraftSnapshotHashQuality.TryGetValue(h, out int prevHashQ))
                                    {
                                        prevQ = prevHashQ;
                                        var kv = ChanceCraftPlugin._preCraftSnapshotData?.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == h);
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
                    catch { }
                }

                // 2) revert by pre-snapshot hash
                try
                {
                    if (ChanceCraftPlugin._preCraftSnapshotHashQuality != null && ChanceCraftPlugin._preCraftSnapshotHashQuality.Count > 0)
                    {
                        foreach (var it in all)
                        {
                            if (it == null || it.m_shared == null) continue;
                            int h = RuntimeHelpers.GetHashCode(it);
                            if (ChanceCraftPlugin._preCraftSnapshotHashQuality.TryGetValue(h, out int prevQ))
                            {
                                if (it.m_quality > prevQ)
                                {
                                    LogInfo($"ForceRevertAfterRemoval: reverting by-hash item itemHash={h:X} name={it.m_shared.m_name} oldQ={it.m_quality} -> {prevQ}");
                                    it.m_quality = prevQ;
                                    var kv = ChanceCraftPlugin._preCraftSnapshotData?.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == h);
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

                // 3) last-resort: revert first found resultName item with quality > expectedPreQuality
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
                            var kv = ChanceCraftPlugin._preCraftSnapshotData?.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == RuntimeHelpers.GetHashCode(it));
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
    }
}
#endregion
