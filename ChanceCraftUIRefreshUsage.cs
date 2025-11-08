using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Small helper to locate the crafting UI root and schedule the UIRemoteRefresher to run next frame.
/// Place this file in the same assembly as ChanceCraft.cs (same namespace or global) so the symbol is visible.
/// </summary>
public static class ChanceCraftUIRefreshUsage
{
    /// <summary>
    /// Try to find the crafting UI root GameObject and schedule a UI refresh on the next frame.
    /// Safe to call from postfix patches / TrySpawnCraftEffect code paths.
    /// </summary>
    public static void RefreshCraftingUiAfterChange()
    {
        try
        {
            GameObject panel = GetCraftingPanelRoot();
            if (panel != null)
            {
                // schedule the refresher
                UIRemoteRefresher.Instance.RefreshNextFrame(panel);
                Debug.Log("[ChanceCraft] RefreshCraftingUiAfterChange: scheduled refresher for panel: " + panel.name);
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

    /// <summary>
    /// Heuristics to find the Crafting UI root GameObject.
    /// Returns null if not found.
    /// </summary>
    private static GameObject GetCraftingPanelRoot()
    {
        // 1) Try to find a CraftingPanel component (Assembly-CSharp)
        try
        {
            var craftingType = Type.GetType("CraftingPanel, Assembly-CSharp") ?? Type.GetType("CraftingPanel");
            if (craftingType != null)
            {
                var comp = UnityEngine.Object.FindObjectOfType(craftingType) as Component;
                if (comp != null && comp.gameObject != null)
                {
                    Debug.Log("[ChanceCraft] Found crafting panel via CraftingPanel type: " + comp.gameObject.name);
                    return comp.gameObject;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] GetCraftingPanelRoot: Type.GetType('CraftingPanel') failed: " + ex);
        }

        // 2) Common GameObject names
        string[] names = new[] { "Crafting", "crafting", "CraftingPanel", "craftingpanel", "CraftingMenu", "RecipePanel", "piece_workbench(Clone)" };
        foreach (var name in names)
        {
            var go = GameObject.Find(name);
            if (go != null)
            {
                Debug.Log("[ChanceCraft] Found crafting panel via GameObject.Find: " + name);
                return go;
            }
        }

        // 3) Look for a Canvas whose name contains craft/recipe and return the canvas root
        var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>(true);
        foreach (var c in canvases)
        {
            if (c == null || string.IsNullOrEmpty(c.name)) continue;
            var n = c.name.ToLowerInvariant();
            if (n.Contains("craft") || n.Contains("recipe") || n.Contains("workbench") || n.Contains("bench"))
            {
                Debug.Log("[ChanceCraft] Found crafting panel via Canvas name: " + c.name);
                return c.gameObject;
            }
        }

        // 4) Heuristic: search children for "recipe" or "recipelist"
        var allRoots = UnityEngine.Object.FindObjectsOfType<RectTransform>(true);
        foreach (var rt in allRoots)
        {
            if (rt == null) continue;
            var lower = rt.name.ToLowerInvariant();
            if (lower.Contains("recipe") || lower.Contains("recipelist") || lower.Contains("craft"))
            {
                Debug.Log("[ChanceCraft] Found crafting panel candidate via RectTransform name: " + rt.name);
                return rt.gameObject;
            }
        }

        // not found
        return null;
    }
}