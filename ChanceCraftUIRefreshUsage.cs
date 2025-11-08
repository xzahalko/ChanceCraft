using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public static class ChanceCraftUIRefreshUsage
{
    public static void RefreshCraftingUiAfterChange()
    {
        try
        {
            GameObject panel = GetCraftingPanelRoot();
            if (panel != null)
            {
                // schedule the refresher
                try
                {
                    UIRemoteRefresher.Instance?.RefreshNextFrame(panel);
                    Debug.Log("[ChanceCraft] RefreshCraftingUiAfterChange: scheduled refresher for panel: " + panel.name);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ChanceCraft] RefreshCraftingUiAfterChange: RefreshNextFrame threw: " + ex);
                }

                // Fallback: start a coroutine to toggle tabs (mimic user switching tabs)
                try
                {
                    if (UIRemoteRefresher.Instance != null)
                    {
                        UIRemoteRefresher.Instance.StartCoroutine(DelayedToggleTabs(panel));
                        Debug.Log("[ChanceCraft] RefreshCraftingUiAfterChange: scheduled DelayedToggleTabs fallback for panel: " + panel.name);
                    }
                    else
                    {
                        Debug.LogWarning("[ChanceCraft] RefreshCraftingUiAfterChange: UIRemoteRefresher.Instance is null — cannot start DelayedToggleTabs coroutine.");
                        // As a last resort, perform immediate force rebuilds (best-effort synchronous)
                        TryImmediateLayoutRebuild(panel);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ChanceCraft] RefreshCraftingUiAfterChange: couldn't start DelayedToggleTabs coroutine: " + ex);
                    TryImmediateLayoutRebuild(panel);
                }
            }
            else
            {
                Debug.LogWarning("[ChanceCraft] RefreshCraftingUiAfterChange: couldn't find crafting panel root");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] RefreshCraftingUiAfterChange exception: " + ex);
        }
    }

    // Coroutine that mimics the user switching tabs: click other tab, wait a frame, click original tab.
    // IMPORTANT: yields are placed only outside try/catch blocks that contain catch clauses.
    private static IEnumerator DelayedToggleTabs(GameObject panel)
    {
        if (panel == null) yield break;

        // wait one frame so scheduled refresher can run first
        yield return null;

        // FIND buttons (no yields here)
        Button craftButton = null;
        Button upgradeButton = null;
        try
        {
            var buttons = panel.GetComponentsInChildren<Button>(true);
            foreach (var btn in buttons)
            {
                if (btn == null) continue;
                var goName = btn.gameObject.name ?? "";
                var txt = GetButtonText(btn) ?? "";
                var combined = (goName + "|" + txt).ToLowerInvariant();
                if (craftButton == null && (combined.Contains("craft") || combined.Contains("crafting") || combined.Contains("craft_tab") || combined.Contains("crafttab")))
                {
                    craftButton = btn;
                }
                if (upgradeButton == null && (combined.Contains("upgrade") || combined.Contains("upgrade_tab") || combined.Contains("upgradetab")))
                {
                    upgradeButton = btn;
                }
                if (craftButton != null && upgradeButton != null) break;
            }

            if (craftButton == null || upgradeButton == null)
            {
                foreach (var btn in panel.GetComponentsInChildren<Button>(true))
                {
                    if (btn == null) continue;
                    var nameLower = (btn.gameObject.name ?? "").ToLowerInvariant();
                    if (craftButton == null && nameLower.Contains("craft")) craftButton = btn;
                    if (upgradeButton == null && nameLower.Contains("upgrade")) upgradeButton = btn;
                    if (craftButton != null && upgradeButton != null) break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] DelayedToggleTabs: exception while searching buttons: " + ex);
        }

        // If we found both buttons, click the "other" tab then back to the upgrade tab.
        if (craftButton != null && upgradeButton != null)
        {
            bool upgradeVisible = false;
            try
            {
                upgradeVisible = IsUpgradeTabVisible(panel);
            }
            catch { /* non-fatal, default to false */ }

            Button first = upgradeVisible ? craftButton : upgradeButton;
            Button second = upgradeVisible ? upgradeButton : craftButton;

            // invoke first click (no yields inside this try/catch)
            try
            {
                InvokeButtonClick(first);
                Debug.Log("[ChanceCraft] DelayedToggleTabs: invoked first tab click: " + first.gameObject.name);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ChanceCraft] DelayedToggleTabs: exception invoking first click: " + ex);
            }

            // wait one frame for UI to handle the change
            yield return null;

            try
            {
                InvokeButtonClick(second);
                Debug.Log("[ChanceCraft] DelayedToggleTabs: invoked second tab click: " + second.gameObject.name);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ChanceCraft] DelayedToggleTabs: exception invoking second click: " + ex);
            }

            // After switching back to the upgrade tab, try to restore focus to the previously-selected upgrade item.
            try
            {
                FocusPreviouslySelectedUpgradeItem(panel);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ChanceCraft] DelayedToggleTabs: FocusPreviouslySelectedUpgradeItem failed: " + ex);
            }

            yield break;
        }

        // Fallback: toggle only the recipe list child briefly.
        Transform recipeList = null;
        try
        {
            recipeList = FindChildByName(panel.transform, "RecipeList") ?? FindChildByName(panel.transform, "Recipe List") ?? FindChildByName(panel.transform, "RecipePanel");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] DelayedToggleTabs: exception searching recipeList: " + ex);
        }

        if (recipeList != null)
        {
            try
            {
                recipeList.gameObject.SetActive(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ChanceCraft] DelayedToggleTabs: exception deactivating recipeList: " + ex);
                yield break;
            }

            // yield outside of try/catch
            yield return null;

            try
            {
                recipeList.gameObject.SetActive(true);
                Debug.Log("[ChanceCraft] DelayedToggleTabs: toggled recipeList to force rebuild");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ChanceCraft] DelayedToggleTabs: exception reactivating recipeList: " + ex);
            }

            // Try to focus previously selected upgrade item if possible
            try
            {
                FocusPreviouslySelectedUpgradeItem(panel);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ChanceCraft] DelayedToggleTabs: FocusPreviouslySelectedUpgradeItem (fallback) failed: " + ex);
            }

            yield break;
        }

        // Last-resort synchronous layout rebuild (no yields involved)
        TryImmediateLayoutRebuild(panel);
        yield break;
    }

    // Try to focus the previously selected item in the InventoryGui upgrade tab.
    // This uses reflection to read the plugin's saved upgrade target (if present) and set InventoryGui's index/selection.
    private static void FocusPreviouslySelectedUpgradeItem(GameObject panel)
    {
        if (panel == null) return;

        try
        {
            // Find InventoryGui instance related to the panel
            InventoryGui igInstance = null;
            try { igInstance = panel.GetComponentInChildren<InventoryGui>(true); } catch { igInstance = null; }
            if (igInstance == null)
            {
                try { igInstance = UnityEngine.Object.FindObjectOfType<InventoryGui>(); } catch { igInstance = null; }
            }
            if (igInstance == null)
            {
                Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: InventoryGui instance not found.");
                return;
            }

            // Try to read plugin's stored upgrade target via reflection (ChanceCraftPlugin._upgradeTargetItem or similar)
            object upgradeTarget = null;
            int savedIndex = -1;

            // Search loaded assemblies for ChanceCraftPlugin type
            Type pluginType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetTypes().FirstOrDefault(x => x.Name == "ChanceCraftPlugin" || x.Name == "ChanceCraft");
                    if (t != null) { pluginType = t; break; }
                }
                catch { }
            }

            if (pluginType != null)
            {
                try
                {
                    // Try common field names
                    var fTarget = pluginType.GetField("_upgradeTargetItem", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                               ?? pluginType.GetField(" _upgradeTargetItem", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (fTarget != null)
                    {
                        upgradeTarget = fTarget.GetValue(null);
                        Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: read _upgradeTargetItem via reflection: " + (upgradeTarget != null ? upgradeTarget.ToString() : "<null>"));
                    }

                    var fIndex = pluginType.GetField("_upgradeTargetItemIndex", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                             ?? pluginType.GetField("_upgradeTargetIndex", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                             ?? pluginType.GetField("_upgradeItemIndex", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    if (fIndex != null)
                    {
                        try { savedIndex = Convert.ToInt32(fIndex.GetValue(null)); Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: read saved index " + savedIndex); } catch { savedIndex = -1; }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: plugin reflection read failed: " + ex);
                }
            }
            else
            {
                Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: plugin type ChanceCraftPlugin not found in loaded assemblies.");
            }

            Type igType = typeof(InventoryGui);

            // Try to set InventoryGui.m_upgradeItemIndex if available (prefer savedIndex)
            var fUpgradeItems = igType.GetField("m_upgradeItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fUpgradeIndex = igType.GetField("m_upgradeItemIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                ?? igType.GetField("m_upgradeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (fUpgradeItems != null && fUpgradeIndex != null)
            {
                try
                {
                    var listObj = fUpgradeItems.GetValue(igInstance) as System.Collections.IList;
                    if (listObj != null)
                    {
                        int idxToSet = -1;

                        // Prefer savedIndex when valid
                        if (savedIndex >= 0 && savedIndex < listObj.Count) idxToSet = savedIndex;

                        // Otherwise, try to find by reference to upgradeTarget (if present)
                        if (idxToSet < 0 && upgradeTarget != null)
                        {
                            for (int i = 0; i < listObj.Count; i++)
                            {
                                try
                                {
                                    var entry = listObj[i];
                                    if (entry == null) continue;
                                    if (ReferenceEquals(entry, upgradeTarget)) { idxToSet = i; break; }

                                    // fallback: compare by fields (m_shared.m_name + quality) if direct reference doesn't match
                                    var eShared = entry.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(entry);
                                    var tShared = upgradeTarget.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(upgradeTarget);
                                    var eName = eShared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(eShared) as string;
                                    var tName = tShared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(tShared) as string;
                                    if (!string.IsNullOrEmpty(eName) && !string.IsNullOrEmpty(tName) && string.Equals(eName, tName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        idxToSet = i;
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }

                        if (idxToSet >= 0)
                        {
                            try
                            {
                                fUpgradeIndex.SetValue(igInstance, idxToSet);
                                Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: set InventoryGui.m_upgradeItemIndex = " + idxToSet);
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: failed to set m_upgradeItemIndex: " + ex);
                            }
                        }
                        else
                        {
                            Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: could not resolve index in m_upgradeItems (no match).");
                        }

                        // Try to call common update methods so the GUI rebinds selection
                        TryInvokeInventoryGuiUpdateMethods(igInstance);

                        // Try to focus the corresponding UI child to restore visible selection/focus
                        if (idxToSet >= 0)
                        {
                            TrySelectUpgradeUiChild(panel, idxToSet, listObj.Count);
                        }

                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: exception operating on m_upgradeItems: " + ex);
                }
            }

            // If we couldn't set index, attempt to find individual UI element for the target and click it
            if (upgradeTarget != null)
            {
                try
                {
                    // Search for any child GameObject under panel that has a name matching the item name and click its selectable/button if found
                    var shared = upgradeTarget.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(upgradeTarget);
                    var tName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                    if (!string.IsNullOrEmpty(tName))
                    {
                        var lower = tName.ToLowerInvariant();
                        foreach (var tr in panel.GetComponentsInChildren<Transform>(true))
                        {
                            if (tr == null || string.IsNullOrEmpty(tr.name)) continue;
                            if (tr.name.ToLowerInvariant().Contains(lower) || (tr.gameObject.GetComponentInChildren<Text>()?.text?.ToLowerInvariant().Contains(lower) ?? false))
                            {
                                // attempt to click a Button on this transform
                                var b = tr.GetComponentInChildren<Button>(true);
                                if (b != null)
                                {
                                    try { InvokeButtonClick(b); Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: clicked candidate UI element: " + tr.name); return; } catch { }
                                }

                                var sel = tr.GetComponentInChildren<Selectable>(true);
                                if (sel != null)
                                {
                                    try { sel.Select(); EventSystem.current?.SetSelectedGameObject(sel.gameObject); Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: selected candidate UI element: " + tr.name); return; } catch { }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: fallback UI-element click attempt failed: " + ex);
                }
            }

            Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem: nothing else to try.");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] FocusPreviouslySelectedUpgradeItem top-level error: " + ex);
        }
    }

    // Try to select the UI child corresponding to the index in the upgrade-items UI list.
    // Searches for likely candidate containers and tries to select the child at idx.
    private static void TrySelectUpgradeUiChild(GameObject panel, int idx, int expectedCount)
    {
        if (panel == null) return;
        try
        {
            // Heuristics: find transforms with names matching upgrade + list/panel/entries
            var candidates = new List<Transform>();
            foreach (var t in panel.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                var n = t.name.ToLowerInvariant();
                if ((n.Contains("upgrade") && (n.Contains("list") || n.Contains("panel") || n.Contains("entries") || n.Contains("items")))
                    || n.Contains("upgrade_items") || n.Contains("upgradeitem") || n.Contains("upgradecontainer"))
                {
                    candidates.Add(t);
                }
            }

            // Also include transforms whose child count matches expectedCount (likely the list container)
            foreach (var t in panel.GetComponentsInChildren<Transform>(true))
            {
                try
                {
                    if (t.childCount == expectedCount && !candidates.Contains(t))
                        candidates.Add(t);
                }
                catch { }
            }

            // Try candidates in order
            foreach (var cand in candidates)
            {
                try
                {
                    if (cand == null) continue;
                    int childCount = cand.childCount;
                    if (childCount == 0) continue;
                    if (idx >= 0 && idx < childCount)
                    {
                        var child = cand.GetChild(idx);
                        if (child == null) continue;

                        // prefer Button/Selectable on the child
                        var b = child.GetComponentInChildren<Button>(true);
                        if (b != null)
                        {
                            try { b.Select(); EventSystem.current?.SetSelectedGameObject(b.gameObject); Debug.LogWarning("[ChanceCraft] TrySelectUpgradeUiChild: selected Button on child: " + child.name); return; } catch { }
                        }

                        var sel = child.GetComponentInChildren<Selectable>(true);
                        if (sel != null)
                        {
                            try { sel.Select(); EventSystem.current?.SetSelectedGameObject(sel.gameObject); Debug.LogWarning("[ChanceCraft] TrySelectUpgradeUiChild: selected Selectable on child: " + child.name); return; } catch { }
                        }

                        // fallback: set selected gameobject to the child itself
                        try { EventSystem.current?.SetSelectedGameObject(child.gameObject); Debug.LogWarning("[ChanceCraft] TrySelectUpgradeUiChild: set selected GameObject to child: " + child.name); return; } catch { }
                    }
                }
                catch { /* try next candidate */ }
            }

            // If none matched, attempt to find any selectable whose label matches index-th item name (best-effort)
            Debug.LogWarning("[ChanceCraft] TrySelectUpgradeUiChild: no matching UI container found to select child idx=" + idx);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] TrySelectUpgradeUiChild: exception: " + ex);
        }
    }

    // Try to invoke a few candidate InventoryGui instance methods to force rebind/selection visuals
    private static void TryInvokeInventoryGuiUpdateMethods(InventoryGui igInstance)
    {
        if (igInstance == null) return;
        Type igType = typeof(InventoryGui);
        var candidates = new[] {
            "UpdateCraftingPanel", "UpdateRecipeList", "UpdateAvailableRecipes", "UpdateCrafting",
            "Refresh", "RefreshList", "UpdateAvailableCrafting", "UpdateRecipes", "UpdateInventory",
            "UpdateSelectedItem", "OnInventoryChanged", "RefreshInventory", "UpdateIcons", "Setup", "OnOpen", "OnShow"
        };

        foreach (var name in candidates)
        {
            try
            {
                var m = igType.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null)
                {
                    try
                    {
                        m.Invoke(igInstance, null);
                        Debug.LogWarning("[ChanceCraft] TryInvokeInventoryGuiUpdateMethods: invoked " + name);
                        // prefer to stop after first successful invoke
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[ChanceCraft] TryInvokeInventoryGuiUpdateMethods: method " + name + " invoked but threw: " + ex);
                    }
                }
            }
            catch { /* keep trying other names */ }
        }
    }

    // Helper: invoke the button as if clicked (invokes onClick and tries ExecuteEvents)
    private static void InvokeButtonClick(Button btn)
    {
        if (btn == null) return;
        try
        {
            btn.onClick?.Invoke();
            ExecuteEvents.Execute(btn.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
        }
        catch { /* best-effort */ }
    }

    private static bool IsUpgradeTabVisible(GameObject panel)
    {
        try
        {
            var upgradeContent = FindChildByName(panel.transform, "Upgrade") ?? FindChildByName(panel.transform, "UpgradePanel");
            if (upgradeContent != null) return upgradeContent.gameObject.activeInHierarchy;

            var all = panel.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                var n = (t?.name ?? "").ToLowerInvariant();
                if (n.Contains("upgrade") && t.gameObject.activeInHierarchy) return true;
            }
        }
        catch { }
        return false;
    }

    // Robust GetButtonText that avoids a compile-time dependency on TextMeshPro.
    // Tries UnityEngine.UI.Text first, then attempts to find a TextMeshProUGUI component via reflection.
    private static string GetButtonText(Button btn)
    {
        try
        {
            var txt = btn.GetComponentInChildren<UnityEngine.UI.Text>();
            if (txt != null) return txt.text;

            // Try to get TextMeshProUGUI by reflection (avoids compile error if TMPro isn't referenced)
            Type tmproType = Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro")
                            ?? Type.GetType("TMPro.TextMeshProUGUI, Unity.TextMeshPro, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");

            if (tmproType != null)
            {
                var comp = btn.GetComponentInChildren(tmproType);
                if (comp != null)
                {
                    var textProp = tmproType.GetProperty("text");
                    if (textProp != null)
                    {
                        var v = textProp.GetValue(comp) as string;
                        return v;
                    }
                }
            }
        }
        catch { /* swallow - best-effort */ }
        return null;
    }

    private static Transform FindChildByName(Transform root, string name)
    {
        if (root == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var q = name.ToLowerInvariant();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                if ((t.name ?? "").ToLowerInvariant().Contains(q)) return t;
            }
        }
        catch { }
        return null;
    }

    // best-effort synchronous fallback if coroutine host is missing
    private static void TryImmediateLayoutRebuild(GameObject panel)
    {
        if (panel == null) return;
        try
        {
            Canvas.ForceUpdateCanvases();
            var rects = panel.GetComponentsInChildren<RectTransform>(true);
            foreach (var r in rects)
            {
                try { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(r); } catch { }
            }
            Debug.Log("[ChanceCraft] TryImmediateLayoutRebuild: forced layout rebuild for panel: " + panel.name);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] TryImmediateLayoutRebuild exception: " + ex);
        }
    }

    // existing utility: locate crafting panel root (preserved as-is)
    private static GameObject GetCraftingPanelRoot()
    {
        try
        {
            var found = GameObject.Find("Crafting");
            if (found != null) return found;

            found = GameObject.Find("/Crafting");
            if (found != null) return found;

            found = GameObject.Find("CraftingPanel");
            if (found != null) return found;

            return null;
        }
        catch { return null; }
    }
}