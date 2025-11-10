using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ChanceCraft
{
    // Consolidated UI helpers (refreshing UI and extracting recipes from wrappers)
    public static class ChanceCraftUIHelpers
    {
        #region Logging & small helpers (kept here so method calls don't require many namespace changes)

        private static void LogWarning(string msg)
        {
            if (ChanceCraftPlugin.loggingEnabled?.Value ?? false) UnityEngine.Debug.LogWarning($"[ChanceCraft] {msg}");
        }

        private static void LogInfo(string msg)
        {
            if (ChanceCraftPlugin.loggingEnabled?.Value ?? false) UnityEngine.Debug.Log($"[ChanceCraft] {msg}");
        }

        // Resolve a possibly-Harmony-injected instance into an InventoryGui reference.
        // Public so other helper files can call it when they adopt the __instance-based pattern.
        public static InventoryGui ResolveInventoryGui(object instance)
        {
            if (instance == null) return null;
            if (instance is InventoryGui g) return g;
            if (instance is Component comp)
            {
                // try to find an InventoryGui on the component or in its parents/children
                var ig = comp.GetComponent<InventoryGui>();
                if (ig != null) return ig;
                ig = comp.GetComponentInParent<InventoryGui>(true);
                if (ig != null) return ig;
                ig = comp.GetComponentInChildren<InventoryGui>(true);
                return ig;
            }
            return null;
        }

        // Try to extract Recipe embedded in wrapper objects using BFS (non-generic Queue fallback)
        public static bool TryExtractRecipeFromWrapper(object wrapper, Recipe excludeRecipe, out Recipe foundRecipe, out string foundPath, int maxDepth = 3)
        {
            foundRecipe = null;
            foundPath = null;
            if (wrapper == null) return false;

            try
            {
                var seen = new HashSet<int>();
                var q = new System.Collections.Queue();
                q.Enqueue(new WrapperNode(wrapper, "root", 0));

                while (q.Count > 0)
                {
                    var nodeObj = q.Dequeue();
                    if (!(nodeObj is WrapperNode node)) continue;
                    var obj = node.Obj;
                    var path = node.Path;
                    var depth = node.Depth;

                    if (obj == null) continue;
                    int id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
                    if (seen.Contains(id)) continue;
                    seen.Add(id);

                    if (obj is Recipe r)
                    {
                        if (!ReferenceEquals(r, excludeRecipe))
                        {
                            foundRecipe = r;
                            foundPath = path;
                            LogWarning($"TryExtractRecipeFromWrapper: found Recipe at path '{path}': {ChanceCraftPlugin.RecipeInfo(r)}");
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
                                    LogWarning($"TryExtractRecipeFromWrapper: found Recipe field '{f.Name}' at path '{foundPath}' => {ChanceCraftPlugin.RecipeInfo(foundRecipe)}");
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
                                        LogWarning($"TryExtractRecipeFromWrapper: found Recipe in enumerable '{f.Name}' at '{foundPath}' => {ChanceCraftPlugin.RecipeInfo(foundRecipe)}");
                                        return true;
                                    }
                                    q.Enqueue(new WrapperNode(elem, $"{path}.{f.Name}[{idx}]", depth + 1));
                                    idx++;
                                    if (idx > 50) break;
                                }
                                continue;
                            }

                            q.Enqueue(new WrapperNode(val, $"{path}.{f.Name}", depth + 1));
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
                                    LogWarning($"TryExtractRecipeFromWrapper: found Recipe property '{p.Name}' at path '{foundPath}' => {ChanceCraftPlugin.RecipeInfo(foundRecipe)}");
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
                                        LogWarning($"TryExtractRecipeFromWrapper: found Recipe in enumerable property '{p.Name}' at '{foundPath}' => {ChanceCraftPlugin.RecipeInfo(foundRecipe)}");
                                        return true;
                                    }
                                    q.Enqueue(new WrapperNode(elem, $"{path}.{p.Name}[{idx}]", depth + 1));
                                    idx++;
                                    if (idx > 50) break;
                                }
                                continue;
                            }

                            q.Enqueue(new WrapperNode(v, $"{path}.{p.Name}", depth + 1));
                        }
                        catch { /* ignore per-property issues */ }
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"TryExtractRecipeFromWrapper exception: {ex}");
            }
            return false;
        }

        private class WrapperNode
        {
            public object Obj;
            public string Path;
            public int Depth;
            public WrapperNode(object obj, string path, int depth) { Obj = obj; Path = path; Depth = depth; }
        }

        // Robust method to find upgrade recipe in InventoryGui (moved from main file)
        public static Recipe GetUpgradeRecipeFromGui(object __instance)
        {
            var gui = ResolveInventoryGui(__instance);
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
                LogWarning($"GetUpgradeRecipeFromGui exception: {ex}");
            }
            return null;
        }

        #region GUI refresh & requirement parsing (moved)

        public static void RefreshCraftingPanel(object __instance)
        {
            var gui = ResolveInventoryGui(__instance);
            if (gui == null) return;
            var t = gui.GetType();

            string[] methods = new[]
            {
                "UpdateCraftingPanel", "UpdateRecipeList", "UpdateAvailableRecipes", "UpdateCrafting",
                "UpdateAvailableCrafting", "UpdateRecipes", "UpdateSelectedItem", "Refresh",
                "RefreshList", "UpdateIcons", "Setup", "OnOpen", "OnShow"
            };

            foreach (var name in methods)
            {
                try
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m != null && m.GetParameters().Length == 0)
                    {
                        try { m.Invoke(gui, null); } catch { }
                    }
                }
                catch { }
            }

            try
            {
                var selField = t.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (selField != null)
                {
                    var cur = selField.GetValue(gui);
                    try { selField.SetValue(gui, null); } catch { }
                    try { selField.SetValue(gui, cur); } catch { }
                }
            }
            catch { }

            try
            {
                var inventoryField = t.GetField("m_playerInventory", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                 ?? t.GetField("m_inventory", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var invObj = inventoryField?.GetValue(gui);
                if (invObj != null)
                {
                    var invType = invObj.GetType();
                    foreach (var name in new[] { "Refresh", "UpdateIfNeeded", "Update", "OnChanged" })
                    {
                        try
                        {
                            var rm = invType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (rm != null && rm.GetParameters().Length == 0)
                            {
                                try { rm.Invoke(invObj, null); } catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static void RefreshInventoryGui(object __instance)
        {
            var gui = ResolveInventoryGui(__instance);
            if (gui == null) return;
            var t = gui.GetType();
            var candidates = new[]
            {
                "UpdateCraftingPanel","UpdateRecipeList","UpdateAvailableRecipes","UpdateCrafting",
                "Refresh","RefreshList","UpdateAvailableCrafting","UpdateRecipes","UpdateInventory",
                "UpdateSelectedItem","OnInventoryChanged","RefreshInventory","UpdateIcons","Setup","OnOpen","OnShow"
            };
            foreach (var name in candidates)
            {
                try
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (m != null && m.GetParameters().Length == 0) m.Invoke(gui, null);
                }
                catch { }
            }

            try
            {
                var inventoryField = t.GetField("m_playerInventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?? t.GetField("m_inventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var inventoryObj = inventoryField?.GetValue(gui);
                if (inventoryObj != null)
                {
                    var invType = inventoryObj.GetType();
                    foreach (var rm in new[] { "Refresh", "UpdateIfNeeded", "Update", "OnChanged" })
                    {
                        try
                        {
                            var rmMethod = invType.GetMethod(rm, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (rmMethod != null && rmMethod.GetParameters().Length == 0) rmMethod.Invoke(inventoryObj, null);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static bool TryGetRequirementsFromGui(object __instance, out List<(string name, int amount)> requirements)
        {
            requirements = null;
            var gui = ResolveInventoryGui(__instance);
            if (gui == null) return false;

            try
            {
                var tGui = typeof(InventoryGui);

                List<(string name, int amount)> ParseRequirementEnumerable(IEnumerable reqEnum)
                {
                    var outList = new List<(string name, int amount)>();
                    if (reqEnum == null) return outList;
                    int idx = 0;
                    foreach (var elem in reqEnum)
                    {
                        idx++;
                        if (elem == null) { if (idx > 200) break; else continue; }
                        try
                        {
                            var et = elem.GetType();

                            object resItem = null;
                            var fResItem = et.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var pResItem = et.GetProperty("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fResItem != null) resItem = fResItem.GetValue(elem);
                            else if (pResItem != null) resItem = pResItem.GetValue(elem);

                            string resName = null;
                            if (resItem != null)
                            {
                                var itemDataField = resItem.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var itemDataProp = resItem.GetType().GetProperty("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                object itemDataVal = itemDataField != null ? itemDataField.GetValue(resItem) : itemDataProp?.GetValue(resItem);
                                if (itemDataVal != null)
                                {
                                    var sharedField = itemDataVal.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    var sharedProp = itemDataVal.GetType().GetProperty("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    object sharedVal = sharedField != null ? sharedField.GetValue(itemDataVal) : sharedProp?.GetValue(itemDataVal);
                                    if (sharedVal != null)
                                    {
                                        var nameField = sharedVal.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        var nameProp = sharedVal.GetType().GetProperty("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                        resName = nameField != null ? nameField.GetValue(sharedVal) as string : nameProp?.GetValue(sharedVal) as string;
                                    }
                                }
                            }

                            int parsedAmount = 0;
                            var fAmount = et.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var pAmount = et.GetProperty("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            object amountObj = fAmount != null ? fAmount.GetValue(elem) : pAmount?.GetValue(elem);

                            var fAmountPerLevel = et.GetField("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var pAmountPerLevel = et.GetProperty("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            object amountPerLevelObj = fAmountPerLevel != null ? fAmountPerLevel.GetValue(elem) : pAmountPerLevel?.GetValue(elem);

                            if (amountObj != null)
                            {
                                try { parsedAmount = Convert.ToInt32(amountObj); } catch { parsedAmount = 0; }
                            }
                            if (parsedAmount <= 0 && amountPerLevelObj != null)
                            {
                                try { parsedAmount = Convert.ToInt32(amountPerLevelObj); } catch { parsedAmount = 0; }
                            }

                            if (!string.IsNullOrEmpty(resName) && parsedAmount > 0) outList.Add((resName, parsedAmount));
                        }
                        catch { }
                        if (idx > 200) break;
                    }
                    return outList;
                }

                try
                {
                    var reqListField = tGui.GetField("m_reqList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (reqListField != null)
                    {
                        var reqListObj = reqListField.GetValue(gui) as IEnumerable;
                        if (reqListObj != null)
                        {
                            var list = ParseRequirementEnumerable(reqListObj);
                            if (list.Count > 0) { requirements = list; return true; }
                        }
                    }
                }
                catch { }

                try
                {
                    var selField = tGui.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (selField != null)
                    {
                        var selVal = selField.GetValue(gui);
                        if (selVal != null)
                        {
                            var wrapperType = selVal.GetType();
                            var candNames = new[] { "m_resources", "m_requirements", "resources", "requirements", "m_recipeResources", "m_resourceList", "m_reqList" };
                            foreach (var name in candNames)
                            {
                                try
                                {
                                    var f = wrapperType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                    if (f != null)
                                    {
                                        var maybe = f.GetValue(selVal) as IEnumerable;
                                        if (maybe != null)
                                        {
                                            var parsed = ParseRequirementEnumerable(maybe);
                                            if (parsed.Count > 0) { requirements = parsed; return true; }
                                        }
                                    }
                                    var p = wrapperType.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                    if (p != null && p.CanRead)
                                    {
                                        var maybe2 = p.GetValue(selVal) as IEnumerable;
                                        if (maybe2 != null)
                                        {
                                            var parsed2 = ParseRequirementEnumerable(maybe2);
                                            if (parsed2.Count > 0) { requirements = parsed2; return true; }
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (TryExtractRecipeFromWrapper(selVal, null, out var innerRecipe, out var path))
                            {
                                var resourcesField2 = innerRecipe?.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var resObj = resourcesField2?.GetValue(innerRecipe) as IEnumerable;
                                var parsed = ParseRequirementEnumerable(resObj);
                                if (parsed.Count > 0) { requirements = parsed; return true; }
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    var fields = tGui.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var f in fields)
                    {
                        try
                        {
                            var val = f.GetValue(gui);
                            if (val is IEnumerable en && !(val is string))
                            {
                                var parsed = ParseRequirementEnumerable(en);
                                if (parsed.Count > 0) { requirements = parsed; return true; }
                            }
                        }
                        catch { }
                    }

                    var props = tGui.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var p in props)
                    {
                        try
                        {
                            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                            var val = p.GetValue(gui);
                            if (val is IEnumerable en2 && !(val is string))
                            {
                                var parsed = ParseRequirementEnumerable(en2);
                                if (parsed.Count > 0) { requirements = parsed; return true; }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                return false;
            }
            catch (Exception ex)
            {
                LogWarning($"TryGetRequirementsFromGui exception: {ex}");
                requirements = null;
                return false;
            }
        }

        public static void RefreshUpgradeTabInner(object __instance)
        {
            var gui = ResolveInventoryGui(__instance);
            if (gui == null) return;
            try
            {
                var t = gui.GetType();

                string[] igMethods = new[]
                {
                    "UpdateCraftingPanel", "UpdateRecipeList", "UpdateAvailableRecipes", "UpdateCrafting",
                    "UpdateAvailableCrafting", "UpdateRecipes", "UpdateSelectedItem", "Refresh",
                    "RefreshList", "RefreshRequirements", "OnOpen", "OnShow"
                };
                foreach (var name in igMethods)
                {
                    try
                    {
                        var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (m != null && m.GetParameters().Length == 0)
                        {
                            try { m.Invoke(gui, null); LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: invoked InventoryGui.{name}()"); } catch { }
                        }
                    }
                    catch { /* ignore */ }
                }

                FieldInfo selField = t.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                   ?? t.GetField("m_upgradeRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                   ?? t.GetField("m_selectedUpgradeRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                object wrapper = null;
                if (selField != null)
                {
                    try { wrapper = selField.GetValue(gui); } catch { wrapper = null; }
                }
                else
                {
                    var selProp = t.GetProperty("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?? t.GetProperty("SelectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (selProp != null && selProp.CanRead)
                    {
                        try { wrapper = selProp.GetValue(gui); } catch { wrapper = null; }
                    }
                }

                if (wrapper != null)
                {
                    var wt = wrapper.GetType();

                    string[] wrapperMethods = new[] { "Refresh", "Update", "UpdateIfNeeded", "RefreshRequirements", "RefreshList", "OnChanged", "Setup" };
                    foreach (var name in wrapperMethods)
                    {
                        try
                        {
                            var m = wt.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (m != null && m.GetParameters().Length == 0)
                            {
                                try { m.Invoke(wrapper, null); LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: invoked wrapper.{name}()"); } catch { }
                            }
                        }
                        catch { /* ignore per-method */ }
                    }

                    try
                    {
                        var rp = wt.GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (rp != null && rp.CanRead && rp.CanWrite)
                        {
                            try
                            {
                                var val = rp.GetValue(wrapper);
                                rp.SetValue(wrapper, null);
                                rp.SetValue(wrapper, val);
                                LogWarning("[ChanceCraft] RefreshUpgradeTabInner: toggled wrapper.Recipe to force UI rebind");
                            }
                            catch { /* best-effort */ }
                        }
                    }
                    catch { /* ignore */ }
                }

                try
                {
                    var reqField = t.GetField("m_reqList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                ?? t.GetField("m_requirements", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (reqField != null)
                    {
                        var reqObj = reqField.GetValue(gui);
                        if (reqObj != null)
                        {
                            var rt = reqObj.GetType();
                            foreach (var name in new[] { "Refresh", "Update", "UpdateIfNeeded", "OnChanged" })
                            {
                                try
                                {
                                    var m = rt.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (m != null && m.GetParameters().Length == 0)
                                    {
                                        try { m.Invoke(reqObj, null); LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: invoked reqObj.{name}()"); } catch { }
                                    }
                                }
                                catch { /* ignore per-method */ }
                            }
                        }
                    }
                }
                catch { /* ignore */ }

                try
                {
                    gui.StartCoroutine(DelayedRefreshCraftingPanel(__instance, 1));
                    LogWarning("[ChanceCraft] RefreshUpgradeTabInner: scheduled DelayedRefreshCraftingPanel(gui,1)");
                }
                catch (Exception ex)
                {
                    LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: could not start coroutine: {ex}");
                    try { RefreshCraftingPanel(__instance); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: unexpected exception: {ex}");
            }
        }

        public static void ForceSimulateTabSwitchRefresh(object __instance)
        {
            var gui = ResolveInventoryGui(__instance);
            if (gui == null) return;
            try
            {
                gui.StartCoroutine(ForceSimulateTabSwitchRefreshCoroutine(__instance));
            }
            catch (Exception ex)
            {
                LogWarning($"[ChanceCraft] ForceSimulateTabSwitchRefresh: failed to start coroutine: {ex}");
                try { RefreshUpgradeTabInner(__instance); } catch { }
            }
        }

        private static IEnumerator ForceSimulateTabSwitchRefreshCoroutine(object __instance)
        {
            var gui = ResolveInventoryGui(__instance);
            if (gui == null) yield break;

            object TryGetMember(string name)
            {
                try
                {
                    var t = gui.GetType();
                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null) return f.GetValue(gui);
                    var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (p != null && p.CanRead) return p.GetValue(gui);
                }
                catch { }
                return null;
            }

            string[] tabCandidates = new[] { "m_tabUpgrade", "m_tabCraft", "m_tabCrafting", "m_tab", "m_crafting", "m_tabPanel", "m_tabs" };
            GameObject upgradeTabGO = null;
            foreach (var nm in tabCandidates)
            {
                try
                {
                    var val = TryGetMember(nm);
                    if (val != null)
                    {
                        if (val is GameObject g) { upgradeTabGO = g; break; }
                        if (val is Component c) { upgradeTabGO = c.gameObject; break; }
                        if (val is System.Collections.IEnumerable en && !(val is string))
                        {
                            foreach (var e in en)
                            {
                                if (e is GameObject ge) { upgradeTabGO = ge; break; }
                                if (e is Component ce) { upgradeTabGO = ce.gameObject; break; }
                            }
                            if (upgradeTabGO != null) break;
                        }
                    }
                }
                catch { }
            }

            Button backButton = null;
            Toggle backToggle = null;

            try
            {
                var comp = gui as Component;
                if (upgradeTabGO != null)
                {
                    backButton = upgradeTabGO.GetComponentInChildren<Button>(true);
                    backToggle = upgradeTabGO.GetComponentInChildren<Toggle>(true);
                }
                else if (comp != null)
                {
                    var btns = comp.GetComponentsInChildren<Button>(true);
                    var tgls = comp.GetComponentsInChildren<Toggle>(true);

                    backButton = btns.FirstOrDefault(b => b.gameObject.name.IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0
                                                         || b.gameObject.name.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                                                         || b.gameObject.name.IndexOf("tab", StringComparison.OrdinalIgnoreCase) >= 0)
                              ?? btns.FirstOrDefault();
                    backToggle = tgls.FirstOrDefault(t => t.gameObject.name.IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0
                                                         || t.gameObject.name.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                                                         || t.gameObject.name.IndexOf("tab", StringComparison.OrdinalIgnoreCase) >= 0)
                              ?? tgls.FirstOrDefault();
                }
            }
            catch { }

            Button awayButton = null;
            Toggle awayToggle = null;
            try
            {
                var comp = gui as Component;
                if (comp != null)
                {
                    var btnsAll = comp.GetComponentsInChildren<Button>(true);
                    var tglsAll = comp.GetComponentsInChildren<Toggle>(true);

                    awayButton = btnsAll.FirstOrDefault(b => b != backButton && !b.gameObject.name.Equals("Close", StringComparison.OrdinalIgnoreCase));
                    awayToggle = tglsAll.FirstOrDefault(t => t != backToggle);
                }
            }
            catch { }

            if (backButton == null && backToggle == null)
            {
                try { RefreshUpgradeTabInner(__instance); } catch { }
                yield break;
            }

            try
            {
                if (awayButton != null)
                {
                    try { awayButton.onClick?.Invoke(); LogWarning("[ChanceCraft] ForceSimulateTabSwitch: invoked awayButton.onClick"); } catch { }
                }
                else if (awayToggle != null)
                {
                    try { awayToggle.isOn = !awayToggle.isOn; LogWarning("[ChanceCraft] ForceSimulateTabSwitch: toggled awayToggle"); } catch { }
                }
                else
                {
                    try
                    {
                        var comp = gui as Component;
                        var child = (comp?.transform?.childCount > 0) ? comp.transform.GetChild(0).gameObject : null;
                        if (child != null) { child.SetActive(false); }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"[ChanceCraft] ForceSimulateTabSwitch: away-click failed: {ex}");
            }

            yield return null;

            try
            {
                if (backButton != null)
                {
                    try { backButton.onClick?.Invoke(); LogWarning("[ChanceCraft] ForceSimulateTabSwitch: invoked backButton.onClick"); } catch { }
                }
                else if (backToggle != null)
                {
                    try { backToggle.isOn = true; LogWarning("[ChanceCraft] ForceSimulateTabSwitch: set backToggle.isOn = true"); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogWarning($"[ChanceCraft] ForceSimulateTabSwitch: back-click failed: {ex}");
            }

            yield return null;
            try { RefreshUpgradeTabInner(__instance); } catch { }
            try { RefreshInventoryGui(__instance); } catch { }
            try { RefreshCraftingPanel(__instance); } catch { }

            yield break;
        }

        public static IEnumerator DelayedRefreshCraftingPanel(object __instance, int delayFrames = 1)
        {
            var gui = ResolveInventoryGui(__instance);
            if (gui == null) yield break;
            for (int i = 0; i < Math.Max(1, delayFrames); i++) yield return null;
            try { RefreshCraftingPanel(__instance); } catch { }
        }
        #endregion
    }
}