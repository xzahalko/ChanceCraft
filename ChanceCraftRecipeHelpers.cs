using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace ChanceCraft
{
    // Recipe / item / wrapper related helpers extracted from ChanceCraft.cs
    public static class ChanceCraftRecipeHelpers
    {
        private class WrapperQueueNode
        {
            public object Obj;
            public string Path;
            public int Depth;
            public WrapperQueueNode(object obj, string path, int depth) { Obj = obj; Path = path; Depth = depth; }
        }

        public static string ItemInfo(ItemDrop.ItemData it)
        {
            if (it == null) return "<null>";
            try
            {
                return $"[{it.GetHashCode():X8}] name='{it.m_shared?.m_name}' q={it.m_quality} v={it.m_variant} stack={it.m_stack}";
            }
            catch
            {
                return "<bad item>";
            }
        }

        public static string RecipeInfo(Recipe r)
        {
            if (r == null) return "<null>";
            try
            {
                var name = r.m_item?.m_itemData?.m_shared?.m_name ?? "<no-result>";
                int amount = r.m_amount;
                var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (resourcesField == null) return $"Recipe(result='{name}', amount={amount})";
                var res = resourcesField.GetValue(r) as IEnumerable;
                if (res == null) return $"Recipe(result='{name}', amount={amount})";
                var parts = new List<string>();
                foreach (var req in res)
                {
                    try
                    {
                        var t = req.GetType();
                        var amtObj = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                        int amt = amtObj != null ? Convert.ToInt32(amtObj) : 0;
                        string resName = null;
                        var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                        if (resItem != null)
                        {
                            var itemData = resItem.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                            var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                            resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                        }
                        parts.Add($"{resName ?? "<unknown>"}:{amt}");
                    }
                    catch { parts.Add("<res-err>"); }
                }
                return $"Recipe(result='{name}', amount={amount}, resources=[{string.Join(", ", parts)}])";
            }
            catch { return "<bad recipe>"; }
        }

        public static string RecipeFingerprint(Recipe r)
        {
            if (r == null || r.m_item == null) return "<null>";
            try
            {
                var name = r.m_item.m_itemData?.m_shared?.m_name ?? "<no-result>";
                var parts = new List<string>();
                var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var resObj = resourcesField?.GetValue(r) as IEnumerable;
                if (resObj != null)
                {
                    foreach (var req in resObj)
                    {
                        try
                        {
                            var t = req.GetType();
                            var amountObj = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                            int reqAmt = amountObj != null ? Convert.ToInt32(amountObj) : 0;
                            var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                            string rname = null;
                            if (resItem != null)
                            {
                                var itemData = resItem.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                                var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                rname = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                            }
                            parts.Add($"{rname ?? "<unknown>"}:{reqAmt}");
                        }
                        catch { parts.Add("<res-err>"); }
                    }
                }
                return $"{name}|{r.m_amount}|{string.Join(",", parts)}";
            }
            catch { return "<fingerprint-err>"; }
        }

        public static bool RecipeConsumesResult(Recipe recipe)
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
                    catch { /* ignore malformed */ }
                }
            }
            catch { }
            return false;
        }

        public static ItemDrop.ItemData GetSelectedInventoryItem(InventoryGui gui)
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

        public static bool TryExtractRecipeFromWrapper(object wrapper, Recipe excludeRecipe, out Recipe foundRecipe, out string foundPath, int maxDepth = 3)
        {
            foundRecipe = null;
            foundPath = null;
            if (wrapper == null) return false;

            try
            {
                var seen = new HashSet<int>();
                var q = new System.Collections.Queue();
                q.Enqueue(new WrapperQueueNode(wrapper, "root", 0));

                while (q.Count > 0)
                {
                    var nodeObj = q.Dequeue();
                    if (!(nodeObj is WrapperQueueNode node)) continue;
                    var obj = node.Obj;
                    var path = node.Path;
                    var depth = node.Depth;

                    if (obj == null) continue;
                    int id = RuntimeHelpers.GetHashCode(obj);
                    if (seen.Contains(id)) continue;
                    seen.Add(id);

                    if (obj is Recipe r)
                    {
                        if (!ReferenceEquals(r, excludeRecipe))
                        {
                            foundRecipe = r;
                            foundPath = path;
                            ChanceCraft.LogWarning($"TryExtractRecipeFromWrapper: found Recipe at path '{path}': {RecipeInfo(r)}");
                            return true;
                        }
                    }

                    if (depth >= maxDepth) continue;

                    Type t = obj.GetType();

                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        try
                        {
                            var val = f.GetValue(obj);
                            if (val == null) continue;

                            if (typeof(Recipe).IsAssignableFrom(f.FieldType))
                            {
                                var maybe = val as Recipe;
                                if (maybe != null && !ReferenceEquals(maybe, excludeRecipe))
                                {
                                    foundRecipe = maybe;
                                    foundPath = $"{path}.{f.Name}";
                                    ChanceCraft.LogWarning($"TryExtractRecipeFromWrapper: found Recipe field '{f.Name}' at path '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                    return true;
                                }
                            }

                            if (val is IEnumerable ie && !(val is string))
                            {
                                int idx = 0;
                                foreach (var elem in ie)
                                {
                                    if (elem == null) { idx++; continue; }
                                    if (elem is Recipe rr && !ReferenceEquals(rr, excludeRecipe))
                                    {
                                        foundRecipe = rr;
                                        foundPath = $"{path}.{f.Name}[{idx}]";
                                        ChanceCraft.LogWarning($"TryExtractRecipeFromWrapper: found Recipe in enumerable '{f.Name}' at '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                        return true;
                                    }
                                    q.Enqueue(new WrapperQueueNode(elem, $"{path}.{f.Name}[{idx}]", depth + 1));
                                    idx++;
                                    if (idx > 50) break;
                                }
                                continue;
                            }

                            q.Enqueue(new WrapperQueueNode(val, $"{path}.{f.Name}", depth + 1));
                        }
                        catch { /* ignore per-field issues */ }
                    }

                    foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        try
                        {
                            if (p.GetIndexParameters().Length > 0) continue;
                            if (!p.CanRead) continue;
                            var v = p.GetValue(obj);
                            if (v == null) continue;

                            if (typeof(Recipe).IsAssignableFrom(p.PropertyType))
                            {
                                var maybe = v as Recipe;
                                if (maybe != null && !ReferenceEquals(maybe, excludeRecipe))
                                {
                                    foundRecipe = maybe;
                                    foundPath = $"{path}.{p.Name}";
                                    ChanceCraft.LogWarning($"TryExtractRecipeFromWrapper: found Recipe property '{p.Name}' at path '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                    return true;
                                }
                            }

                            if (v is IEnumerable ie2 && !(v is string))
                            {
                                int idx = 0;
                                foreach (var elem in ie2)
                                {
                                    if (elem == null) { idx++; continue; }
                                    if (elem is Recipe rr && !ReferenceEquals(rr, excludeRecipe))
                                    {
                                        foundRecipe = rr;
                                        foundPath = $"{path}.{p.Name}[{idx}]";
                                        ChanceCraft.LogWarning($"TryExtractRecipeFromWrapper: found Recipe in enumerable property '{p.Name}' at '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                        return true;
                                    }
                                    q.Enqueue(new WrapperQueueNode(elem, $"{path}.{p.Name}[{idx}]", depth + 1));
                                    idx++;
                                    if (idx > 50) break;
                                }
                                continue;
                            }

                            q.Enqueue(new WrapperQueueNode(v, $"{path}.{p.Name}", depth + 1));
                        }
                        catch { /* ignore per-property issues */ }
                    }
                }
            }
            catch (Exception ex)
            {
                ChanceCraft.LogWarning($"TryExtractRecipeFromWrapper exception: {ex}");
            }
            return false;
        }

        public static Recipe GetUpgradeRecipeFromGui(InventoryGui gui)
        {
            if (gui == null) return null;
            try
            {
                var t = typeof(InventoryGui);
                var names = new[] {
                    "m_upgradeRecipe", "m_selectedUpgradeRecipe", "m_selectedRecipe",
                    "m_currentRecipe", "m_selectedUpgrade", "m_selectedUpgradeRecipeData",
                    "m_selectedUpgradeRecipePair", "m_currentUpgradeRecipe", "m_selectedRecipeData", "m_selected",
                    "m_targetRecipe", "m_previewRecipe", "m_craftRecipe"
                };

                foreach (var name in names)
                {
                    try
                    {
                        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (f != null)
                        {
                            var val = f.GetValue(gui);
                            if (val is Recipe r) return r;
                            if (val != null)
                            {
                                if (TryExtractRecipeFromWrapper(val, null, out var inner, out var path)) return inner;
                                var prop = val.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (prop != null && prop.CanRead) return prop.GetValue(val) as Recipe;
                            }
                        }

                        var propInfo = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (propInfo != null)
                        {
                            var val2 = propInfo.GetValue(gui);
                            if (val2 is Recipe r3) return r3;
                            if (val2 != null)
                            {
                                if (TryExtractRecipeFromWrapper(val2, null, out var inner3, out var path3)) return inner3;
                                var p2 = val2.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (p2 != null && p2.CanRead) return p2.GetValue(val2) as Recipe;
                            }
                        }
                    }
                    catch { /* ignore candidate errors */ }
                }

                var selectedRecipeField = t.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (selectedRecipeField != null)
                {
                    var wrapper = selectedRecipeField.GetValue(gui);
                    if (wrapper != null)
                    {
                        if (TryExtractRecipeFromWrapper(wrapper, null, out var inner, out var path)) return inner;
                        var rp = wrapper.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (rp != null && rp.CanRead) return rp.GetValue(wrapper) as Recipe;
                    }
                }

                // fallback scan
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        var v = f.GetValue(gui);
                        if (v is Recipe rr) return rr;
                        if (v != null && TryExtractRecipeFromWrapper(v, null, out var inner2, out var p2)) return inner2;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                ChanceCraft.LogWarning($"GetUpgradeRecipeFromGui exception: {ex}");
            }
            return null;
        }

        public static bool TryUnpackQualityVariant(object tupleValue, out int quality, out int variant)
        {
            quality = 0;
            variant = 0;
            if (tupleValue == null) return false;

            var t = tupleValue.GetType();

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
                        var rn = r.m_item.m_itemData?.m_shared?.m_name;
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
                ChanceCraft.LogWarning($"FindBestUpgradeRecipeCandidate exception: {ex}");
                return null;
            }
        }

        public static bool IsUpgradeOperation(InventoryGui gui, Recipe recipe)
        {
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
    }
}