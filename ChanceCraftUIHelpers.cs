using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace ChanceCraft
{
    // UI refresh / requirement parsing / revert helpers extracted from ChanceCraft.cs
    public static class ChanceCraftUIHelpers
    {
        // add this inside the ChanceCraftUIHelpers class
        public static Recipe GetUpgradeRecipeFromGui(InventoryGui gui)
        {
            if (gui == null) return null;

            try
            {
                // Common pattern: CraftingGui stores the currently selected recipe/item
                // Replace the field/property names below with the actual ones from CraftingGui in your project.
                // Example placeholders:
                // - gui.m_currentRecipe
                // - gui.m_selectedRecipe
                // - gui.GetSelectedRecipe() (method)

                // TODO: replace the following lookup with the real CraftingGui member
                // Example (replace with the actual member):
                // return gui.m_selectedRecipe;

                // Fallback attempt: try to reflect common field names (non-performant, but robust for quick fix)
                var type = gui.GetType();
                var field = type.GetField("m_selectedRecipe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                            ?? type.GetField("m_currentRecipe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (field != null)
                {
                    return field.GetValue(gui) as Recipe;
                }

                // If there is a property or method, try them
                var prop = type.GetProperty("SelectedRecipe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop != null)
                {
                    return prop.GetValue(gui) as Recipe;
                }

                var method = type.GetMethod("GetSelectedRecipe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (method != null)
                {
                    return method.Invoke(gui, null) as Recipe;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogException(ex);
            }

            return null;
        }

        public static void RefreshCraftingPanel(InventoryGui gui)
        {
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

        public static void DumpInventoryGuiStructure(InventoryGui gui)
        {
            try
            {
                if (gui == null) { ChanceCraft.LogInfo("DumpInventoryGuiStructure: gui is null"); return; }
                var t = gui.GetType();
                ChanceCraft.LogInfo("DumpInventoryGuiStructure: InventoryGui type = " + t.FullName);
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        object val = null;
                        try { val = f.GetValue(gui); } catch { val = "<unreadable>"; }
                        string valType = val == null ? "null" : (val.GetType().FullName + (val is System.Collections.IEnumerable ? " (IEnumerable)" : ""));
                        ChanceCraft.LogInfo($"Field: {f.Name} : {f.FieldType.FullName} => {valType}");
                    }
                    catch { }
                }
                foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        object val = null;
                        if (p.GetIndexParameters().Length == 0 && p.CanRead)
                        {
                            try { val = p.GetValue(gui); } catch { val = "<unreadable>"; }
                        }
                        string valType = val == null ? "null" : (val.GetType().FullName + (val is System.Collections.IEnumerable ? " (IEnumerable)" : ""));
                        ChanceCraft.LogInfo($"Property: {p.Name} : {p.PropertyType.FullName} => {valType}");
                    }
                    catch { }
                }

                try
                {
                    var selField = t.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (selField != null)
                    {
                        var selVal = selField.GetValue(gui);
                        if (selVal != null) ChanceCraft.LogInfo("m_selectedRecipe value type = " + selVal.GetType().FullName);
                        else ChanceCraft.LogInfo("m_selectedRecipe is null");
                    }
                }
                catch { }

                try
                {
                    var rootGo = (gui as Component)?.gameObject;
                    if (rootGo == null) rootGo = typeof(InventoryGui).GetField("m_root", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(gui) as GameObject;
                    if (rootGo == null) ChanceCraft.LogInfo("DumpInventoryGuiStructure: gui.gameObject unknown");
                    else
                    {
                        ChanceCraft.LogInfo("DumpInventoryGuiStructure: dumping child hierarchy (depth 3) for " + rootGo.name);
                        void DumpChildren(UnityEngine.Transform tr, int depth)
                        {
                            if (tr == null || depth <= 0) return;
                            for (int i = 0; i < tr.childCount; i++)
                            {
                                var c = tr.GetChild(i);
                                string info = $"GO: {new string(' ', (3 - depth) * 2)}{c.name}";
                                var btn = c.GetComponent<UnityEngine.UI.Button>();
                                if (btn != null) info += " [Button]";
                                var tog = c.GetComponent<UnityEngine.UI.Toggle>();
                                if (tog != null) info += " [Toggle]";
                                var txt = c.GetComponent<UnityEngine.UI.Text>();
                                if (txt != null) info += $" [Text='{txt.text}']";
                                ChanceCraft.LogInfo(info);
                                DumpChildren(c, depth - 1);
                            }
                        }
                        DumpChildren(rootGo.transform, 3);
                    }
                }
                catch (Exception ex) { ChanceCraft.LogWarning("DumpInventoryGuiStructure: child dump failed: " + ex); }
            }
            catch (Exception ex)
            {
                ChanceCraft.LogWarning("DumpInventoryGuiStructure failed: " + ex);
            }
        }

        public static void ForceSimulateTabSwitchRefresh(InventoryGui gui)
        {
            if (gui == null) return;
            try
            {
                gui.StartCoroutine(ForceSimulateTabSwitchRefreshCoroutine(gui));
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] ForceSimulateTabSwitchRefresh: failed to start coroutine: {ex}");
                try { RefreshUpgradeTabInner(gui); } catch { }
            }
        }

        public static void RefreshUpgradeTabInner(InventoryGui gui)
        {
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
                            try { m.Invoke(gui, null); UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: invoked InventoryGui.{name}()"); } catch { }
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
                                try { m.Invoke(wrapper, null); UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: invoked wrapper.{name}()"); } catch { }
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
                                UnityEngine.Debug.LogWarning("[ChanceCraft] RefreshUpgradeTabInner: toggled wrapper.Recipe to force UI rebind");
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
                                        try { m.Invoke(reqObj, null); UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: invoked reqObj.{name}()"); } catch { }
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
                    gui.StartCoroutine(DelayedRefreshCraftingPanel(gui, 1));
                    UnityEngine.Debug.LogWarning("[ChanceCraft] RefreshUpgradeTabInner: scheduled DelayedRefreshCraftingPanel(gui,1)");
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: could not start coroutine: {ex}");
                    try { RefreshCraftingPanel(gui); } catch { }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshUpgradeTabInner: unexpected exception: {ex}");
            }
        }

        public static IEnumerator ForceSimulateTabSwitchRefreshCoroutine(InventoryGui gui)
        {
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
                try { RefreshUpgradeTabInner(gui); } catch { }
                yield break;
            }

            try
            {
                if (awayButton != null)
                {
                    try { awayButton.onClick?.Invoke(); UnityEngine.Debug.LogWarning("[ChanceCraft] ForceSimulateTabSwitch: invoked awayButton.onClick"); } catch { }
                }
                else if (awayToggle != null)
                {
                    try { awayToggle.isOn = !awayToggle.isOn; UnityEngine.Debug.LogWarning("[ChanceCraft] ForceSimulateTabSwitch: toggled awayToggle"); } catch { }
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
                UnityEngine.Debug.LogWarning($"[ChanceCraft] ForceSimulateTabSwitch: away-click failed: {ex}");
            }

            yield return null;

            try
            {
                if (backButton != null)
                {
                    try { backButton.onClick?.Invoke(); UnityEngine.Debug.LogWarning("[ChanceCraft] ForceSimulateTabSwitch: invoked backButton.onClick"); } catch { }
                }
                else if (backToggle != null)
                {
                    try { backToggle.isOn = true; UnityEngine.Debug.LogWarning("[ChanceCraft] ForceSimulateTabSwitch: set backToggle.isOn = true"); } catch { }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] ForceSimulateTabSwitch: back-click failed: {ex}");
            }

            yield return null;
            try { RefreshUpgradeTabInner(gui); } catch { }
            try { RefreshInventoryGui(gui); } catch { }
            try { RefreshCraftingPanel(gui); } catch { }

            yield break;
        }

        public static IEnumerator DelayedRefreshCraftingPanel(InventoryGui gui, int delayFrames = 1)
        {
            if (gui == null) yield break;
            for (int i = 0; i < Math.Max(1, delayFrames); i++) yield return null;
            try { RefreshCraftingPanel(gui); } catch { }
        }

        public static void RefreshInventoryGui(InventoryGui gui)
        {
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

        // TryGetRequirementsFromGui ported (parsing UI-provided requirement lists)
        public static bool TryGetRequirementsFromGui(InventoryGui gui, out List<(string name, int amount)> requirements)
        {
            requirements = null;
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

                            if (ChanceCraftRecipeHelpers.TryExtractRecipeFromWrapper(selVal, null, out var innerRecipe, out var path))
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
                ChanceCraft.LogWarning($"TryGetRequirementsFromGui exception: {ex}");
                requirements = null;
                return false;
            }
        }

        public static void ForceRevertAfterRemoval(InventoryGui gui, Recipe selectedRecipe, ItemDrop.ItemData upgradeTarget = null)
        {
            try
            {
                if (selectedRecipe == null) return;
                string resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(resultName)) return;
                int finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;

                int expectedPreQuality = Math.Max(0, finalQuality - 1);

                try
                {
                    if (ChanceCraft._preCraftSnapshotData != null && ChanceCraft._preCraftSnapshotData.Count > 0)
                    {
                        var preQs = ChanceCraft._preCraftSnapshotData.Values
                            .Select(v => { if (ChanceCraftRecipeHelpers.TryUnpackQualityVariant(v, out int a, out int b)) return a; return 0; })
                            .ToList();
                        if (preQs.Count > 0) expectedPreQuality = Math.Max(0, preQs.Max());
                    }
                }
                catch { }

                var inv = Player.m_localPlayer?.GetInventory();
                var all = inv?.GetAllItems();
                if (all == null) return;

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
                                if (ChanceCraft._preCraftSnapshotData != null && ChanceCraft._preCraftSnapshotData.TryGetValue(upgradeTarget, out var tupleVal) && ChanceCraftRecipeHelpers.TryUnpackQualityVariant(tupleVal, out int pq, out int pv))
                                {
                                    prevQ = pq;
                                    prevV = pv;
                                }
                                else
                                {
                                    int h = RuntimeHelpers.GetHashCode(found);
                                    if (ChanceCraft._preCraftSnapshotHashQuality != null && ChanceCraft._preCraftSnapshotHashQuality.TryGetValue(h, out int prevHashQ))
                                    {
                                        prevQ = prevHashQ;
                                        var kv = ChanceCraft._preCraftSnapshotData?.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == h);
                                        if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && ChanceCraftRecipeHelpers.TryUnpackQualityVariant(kv.Value, out int pq2, out int pv2))
                                        {
                                            prevV = pv2;
                                        }
                                    }
                                }
                            }
                            catch { }

                            if (found.m_quality > prevQ)
                            {
                                ChanceCraft.LogInfo($"ForceRevertAfterRemoval: reverting target item itemHash={RuntimeHelpers.GetHashCode(found):X} name={found.m_shared?.m_name} oldQ={found.m_quality} -> {prevQ}");
                                found.m_quality = prevQ;
                                try { found.m_variant = prevV; } catch { }
                            }
                            return;
                        }
                    }
                    catch { /* fail to target revert -> continue to fallback */ }
                }

                try
                {
                    if (ChanceCraft._preCraftSnapshotHashQuality != null && ChanceCraft._preCraftSnapshotHashQuality.Count > 0)
                    {
                        foreach (var it in all)
                        {
                            if (it == null || it.m_shared == null) continue;
                            int h = RuntimeHelpers.GetHashCode(it);
                            if (ChanceCraft._preCraftSnapshotHashQuality.TryGetValue(h, out int prevQ))
                            {
                                if (it.m_quality > prevQ)
                                {
                                    ChanceCraft.LogInfo($"ForceRevertAfterRemoval: reverting by-hash item itemHash={h:X} name={it.m_shared.m_name} oldQ={it.m_quality} -> {prevQ}");
                                    it.m_quality = prevQ;
                                    var kv = ChanceCraft._preCraftSnapshotData?.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == h);
                                    if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && ChanceCraftRecipeHelpers.TryUnpackQualityVariant(kv.Value, out int pqf, out int pvf))
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

                try
                {
                    foreach (var it in all)
                    {
                        if (it == null || it.m_shared == null) continue;
                        if (!string.Equals(it.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (it.m_quality > expectedPreQuality)
                        {
                            ChanceCraft.LogInfo($"ForceRevertAfterRemoval: last-resort revert itemHash={RuntimeHelpers.GetHashCode(it):X} name={it.m_shared.m_name} oldQ={it.m_quality} -> {expectedPreQuality}");
                            it.m_quality = expectedPreQuality;
                            var kv = ChanceCraft._preCraftSnapshotData?.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == RuntimeHelpers.GetHashCode(it));
                            if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int, int)>)) && ChanceCraftRecipeHelpers.TryUnpackQualityVariant(kv.Value, out int pq3, out int pv3))
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