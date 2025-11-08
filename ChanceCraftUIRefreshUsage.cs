using System;
using System.Collections;
using System.Collections.Generic;
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

            yield break;
        }

        // Last-resort synchronous layout rebuild (no yields involved)
        TryImmediateLayoutRebuild(panel);
        yield break;
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