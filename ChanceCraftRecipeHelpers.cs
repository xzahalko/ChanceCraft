using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ChanceCraft
{
    // Helper methods related to recipe/upgrade detection and candidate selection.
    // These implementations are adapted from the original ChanceCraft.cs logic
    // and are intentionally self-contained to minimize coupling with the main plugin.
    public static class ChanceCraftRecipeHelpers
    {
        // Find the best candidate recipe from ObjectDB for upgrading the given craftRecipe result.
        public static Recipe FindBestUpgradeRecipeCandidate(Recipe craftRecipe)
        {
            try
            {
                if (craftRecipe == null) return null;
                string resultName = craftRecipe.m_item?.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(resultName) || ObjectDB.instance == null) return null;

                Recipe best = null;
                int bestScore = int.MaxValue;

                var dbRecipesEnumerable = ObjectDB.instance.m_recipes as IEnumerable;
                if (dbRecipesEnumerable == null) return null;

                foreach (var rObj in dbRecipesEnumerable)
                {
                    var r = rObj as Recipe;
                    if (r == null) continue;
                    try
                    {
                        var rn = r.m_item?.m_itemData?.m_shared?.m_name;
                        var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var resourcesObj = resourcesField?.GetValue(r) as IEnumerable;
                        if (resourcesObj == null) continue;

                        int total = 0;
                        bool consumesResult = false;
                        var distinctNames = new List<string>();

                        foreach (var req in resourcesObj)
                        {
                            if (req == null) continue;
                            try
                            {
                                var t = req.GetType();
                                var amountObj = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                int amount = amountObj != null ? Convert.ToInt32(amountObj) : 0;
                                total += Math.Max(0, amount);

                                var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                var itemData = resItem?.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                                var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                var resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                if (!string.IsNullOrEmpty(resName))
                                {
                                    if (!distinctNames.Any(n => string.Equals(n, resName, StringComparison.OrdinalIgnoreCase)))
                                        distinctNames.Add(resName);
                                    if (string.Equals(resName, resultName, StringComparison.OrdinalIgnoreCase)) consumesResult = true;
                                }
                            }
                            catch { }
                        }

                        bool resultNameMatch = !string.IsNullOrEmpty(rn) && string.Equals(rn, resultName, StringComparison.OrdinalIgnoreCase);

                        int score = total;
                        if (consumesResult) score -= 2000;
                        if (resultNameMatch) score -= 1500;
                        score = score * 10 + distinctNames.Count;

                        bool containsWood = distinctNames.Any(n => n != null && n.IndexOf("wood", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!containsWood) score -= 100;

                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = r;
                        }
                    }
                    catch { }
                }

                return best;
            }
            catch (Exception ex)
            {
                try { UnityEngine.Debug.LogWarning($"FindBestUpgradeRecipeCandidate exception: {ex}"); } catch { }
                return null;
            }
        }

        // Conservative check whether the current gui+recipe context represents an upgrade operation.
        public static bool IsUpgradeOperation(object __instance, Recipe recipe)
        {
            var gui = ChanceCraftUIHelpers.ResolveInventoryGui(__instance);
            if (recipe == null || gui == null) return false;

            try
            {
                var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                if (craftUpgradeField != null)
                {
                    var cv = craftUpgradeField.GetValue(gui);
                    if (cv is int v && v > 1) return true;
                }
            }
            catch { }

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
                            if (it.m_shared.m_name == craftedName && it.m_quality < craftedQuality) return true;
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (RecipeConsumesResult(recipe)) return true;
            }
            catch { }

            return false;
        }

        public static bool ShouldTreatAsUpgrade(object __instance, Recipe selectedRecipe, bool isUpgradeCall)
        {
            try
            {
                if (isUpgradeCall) return true;
                if (ChanceCraftPlugin._isUpgradeDetected) return true;
                if (IsUpgradeOperation(__instance, selectedRecipe)) return true;

                bool guiHasUpgradeRecipe = false;
                try { guiHasUpgradeRecipe = ChanceCraftUIHelpers.GetUpgradeRecipeFromGui(__instance) != null; } catch { guiHasUpgradeRecipe = false; }

                ItemDrop.ItemData target = null;
                try { target = ChanceCraftPlugin._upgradeTargetItem ?? GetSelectedInventoryItem(__instance); } catch { target = ChanceCraftPlugin._upgradeTargetItem; }

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

                if (ChanceCraftPlugin._upgradeGuiRecipe != null && selectedRecipe != null)
                {
                    try
                    {
                        if (ChanceCraftPlugin.RecipeFingerprint(ChanceCraftPlugin._upgradeGuiRecipe) == ChanceCraftPlugin.RecipeFingerprint(selectedRecipe) && target != null) return true;
                    }
                    catch { }
                }

                return false;
            }
            catch { return false; }
        }

        // --- Local helpers used by the above functions ---

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
                    catch { }
                }
            }
            catch { }
            return false;
        }

        // Make this public so other helpers can call it with __instance
        public static ItemDrop.ItemData GetSelectedInventoryItem(object __instance)
        {
            var gui = ChanceCraftUIHelpers.ResolveInventoryGui(__instance);
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
    }
}