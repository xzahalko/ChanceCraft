using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Enhanced UI refresher that aggressively forces per-entry rebinds for the recipe list.
/// Use UIRemoteRefresher.Instance.RefreshNextFrame(panelRoot) from ChanceCraft.DoCrafting after you
/// toggle m_selectedRecipe / call RefreshCraftingPanel(...).
/// 
/// This file is a drop-in replacement for the previous UIRemoteRefresher and keeps yields outside
/// try/catch blocks to avoid CS1626. The new logic attempts to clear/restore per-entry bound fields
/// to force each entry to rebind its visuals (fixes the "stale quality" effect).
/// </summary>
public class UIRemoteRefresher : MonoBehaviour
{
    private static UIRemoteRefresher _instance;
    public static UIRemoteRefresher Instance
    {
        get
        {
            if (_instance != null) return _instance;
            _instance = FindObjectOfType<UIRemoteRefresher>();
            if (_instance != null) return _instance;

            var go = new GameObject("ChanceCraft_UIRefresher");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<UIRemoteRefresher>();
            return _instance;
        }
    }

    public void RefreshNextFrame(GameObject panelRoot)
    {
        if (panelRoot == null)
        {
            Debug.LogWarning("[ChanceCraft] UIRemoteRefresher.RefreshNextFrame called with null panelRoot");
            return;
        }
        StartCoroutine(DelayedForceRefresh(panelRoot));
    }

    private IEnumerator DelayedForceRefresh(GameObject panelRoot)
    {
        // Let underlying data changes settle
        yield return null;

        if (panelRoot == null)
        {
            Debug.LogWarning("[ChanceCraft] DelayedForceRefresh: panelRoot destroyed before refresh");
            yield break;
        }

        // Basic rebuild + TMP/graphics attempts (no yields in the try)
        Transform recipeListTransform = null;
        bool invokedPanelRefresh = false;
        try
        {
            Debug.Log($"[ChanceCraft] DelayedForceRefresh: attempting refresh for panel '{panelRoot.name}'");

            // Layout / graphics / TMP
            var rt = panelRoot.GetComponent<RectTransform>();
            if (rt != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

            var layoutRoots = panelRoot.GetComponentsInChildren<RectTransform>(true)
                .Where(t => t.GetComponent<VerticalLayoutGroup>() != null
                         || t.GetComponent<HorizontalLayoutGroup>() != null
                         || t.GetComponent<GridLayoutGroup>() != null
                         || t.GetComponent<ContentSizeFitter>() != null)
                .ToArray();
            foreach (var l in layoutRoots) LayoutRebuilder.ForceRebuildLayoutImmediate(l);

            var graphics = panelRoot.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null) continue;
                try { g.SetVerticesDirty(); g.SetMaterialDirty(); }
                catch (Exception ex) { Debug.LogWarning($"[ChanceCraft] Graphic dirty failed for {g.name}: {ex}"); }
            }

            var tmpType = Type.GetType("TMPro.TMP_Text, Unity.TextMeshPro") ?? Type.GetType("TMPro.TMP_Text, TMPro");
            if (tmpType != null)
            {
                var tmpObjects = panelRoot.GetComponentsInChildren(tmpType, true);
                var forceMethod = tmpType.GetMethod("ForceMeshUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (forceMethod != null)
                {
                    var paramInfo = forceMethod.GetParameters();
                    object[] args = null;
                    if (paramInfo != null && paramInfo.Length > 0)
                    {
                        args = new object[paramInfo.Length];
                        for (int p = 0; p < paramInfo.Length; p++)
                        {
                            var pt = paramInfo[p].ParameterType;
                            if (pt == typeof(bool)) args[p] = false;
                            else if (pt.IsValueType) args[p] = Activator.CreateInstance(pt);
                            else args[p] = null;
                        }
                    }
                    for (int i = 0; i < tmpObjects.Length; i++)
                    {
                        var obj = tmpObjects[i];
                        if (obj == null) continue;
                        try { forceMethod.Invoke(obj, args); }
                        catch (TargetParameterCountException)
                        {
                            try { forceMethod.Invoke(obj, new object[] { false }); }
                            catch (Exception ex2) { Debug.LogWarning($"[ChanceCraft] TMP fallback invoke failed: {ex2}"); }
                        }
                        catch (Exception ex) { Debug.LogWarning($"[ChanceCraft] TMP invoke failed: {ex}"); }
                    }
                }
            }

            Canvas.ForceUpdateCanvases();

            // Try generic CraftingPanel refreshes via reflection
            invokedPanelRefresh = TryInvokeCraftingPanelRefresh(panelRoot);

            // Locate probable RecipeList transform for per-entry rebind attempts
            recipeListTransform = panelRoot.transform.Find("RecipeList") ??
                                  panelRoot.transform.Find("recipeList") ??
                                  panelRoot.transform.Find("RecipeListContent") ??
                                  panelRoot.GetComponentsInChildren<Transform>(true)
                                           .FirstOrDefault(t => t.name.IndexOf("recipelist", StringComparison.OrdinalIgnoreCase) >= 0
                                                             || t.name.IndexOf("recipes", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] DelayedForceRefresh main try failed: " + ex);
            // fallback: attempt to locate RecipeList even if above failed
            recipeListTransform = recipeListTransform ?? panelRoot.GetComponentsInChildren<Transform>(true)
                                           .FirstOrDefault(t => t.name.IndexOf("recipelist", StringComparison.OrdinalIgnoreCase) >= 0
                                                             || t.name.IndexOf("recipes", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // If reflection refresh didn't happen, try toggling recipe list or performing per-entry rebind
        if (!invokedPanelRefresh)
        {
            if (recipeListTransform != null)
            {
                // First attempt: quick toggle to force rebuild (yields allowed here)
                Debug.Log($"[ChanceCraft] DelayedForceRefresh: toggling RecipeList '{recipeListTransform.name}' to force rebuild");
                recipeListTransform.gameObject.SetActive(false);
                yield return null;
                if (recipeListTransform != null) recipeListTransform.gameObject.SetActive(true);
                yield return null;
            }
            else
            {
                // If RecipeList not found, toggle entire panel
                Debug.Log("[ChanceCraft] DelayedForceRefresh: RecipeList not found; toggling whole panel");
                panelRoot.SetActive(false);
                yield return null;
                if (panelRoot != null) panelRoot.SetActive(true);
                yield return null;
            }
        }

        // Now attempt a per-entry rebind pass: collect candidate entry components and clear/restore their bound data.
        // We prepare in a no-yield phase (scan & clear), then yield, then restore entries one-by-one (with yields between) to force rebind.
        List<EntryBinding> entries = new List<EntryBinding>();

        try
        {
            Transform listRoot = recipeListTransform ?? panelRoot.transform;
            var children = listRoot.GetComponentsInChildren<Transform>(true).Where(t => t.parent == listRoot || t.parent == listRoot || t.name.IndexOf("Recipe", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            // Better: take direct children under RecipeList (if found) otherwise search for plausible recipe entry objects
            Transform[] entryTransforms;
            if (recipeListTransform != null)
            {
                entryTransforms = recipeListTransform.Cast<Transform>().ToArray();
                // Actually GetComponentsInChildren yields many; instead grab direct children:
                entryTransforms = recipeListTransform.GetComponentsInChildren<Transform>(true)
                                  .Where(t => t.parent == recipeListTransform).ToArray();
            }
            else
            {
                // fallback: find children that look like 'Recipe' items under panelRoot
                entryTransforms = panelRoot.GetComponentsInChildren<Transform>(true)
                    .Where(t => t.name.IndexOf("recipe", StringComparison.OrdinalIgnoreCase) >= 0 && t.GetComponents<Component>().Length > 1)
                    .ToArray();
            }

            if (entryTransforms == null || entryTransforms.Length == 0)
            {
                // try broader heuristic: children under a 'Content' or 'List' child
                var content = panelRoot.GetComponentsInChildren<Transform>(true)
                                 .FirstOrDefault(t => t.name.IndexOf("content", StringComparison.OrdinalIgnoreCase) >= 0
                                                   || t.name.IndexOf("list", StringComparison.OrdinalIgnoreCase) >= 0);
                if (content != null) entryTransforms = content.GetComponentsInChildren<Transform>(true)
                                            .Where(t => t != content && t.GetComponents<Component>().Length > 1).ToArray();
            }

            Debug.Log($"[ChanceCraft] DelayedForceRefresh: found {entryTransforms?.Length ?? 0} candidate entry transforms for rebind");

            if (entryTransforms != null)
            {
                foreach (var et in entryTransforms)
                {
                    if (et == null) continue;
                    // For each entry transform, look for components which likely hold the bound item/recipe
                    var comps = et.GetComponents<Component>();
                    if (comps == null || comps.Length == 0) continue;

                    EntryBinding binding = new EntryBinding { transform = et };

                    // Candidate field/property names that commonly hold bound data
                    string[] candidateNames = new[] { "m_item", "m_itemData", "m_recipe", "m_recipeItem", "item", "data", "m_shared", "m_itemDrop" };

                    foreach (var comp in comps)
                    {
                        if (comp == null) continue;
                        var ct = comp.GetType();

                        // Look for fields
                        foreach (var name in candidateNames)
                        {
                            var f = ct.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (f != null)
                            {
                                try
                                {
                                    var orig = f.GetValue(comp);
                                    binding.bindings.Add(new MemberBinding { component = comp, field = f, originalValue = orig });
                                    break; // stop at first match for this component
                                }
                                catch { /* ignore */ }
                            }
                        }

                        // Look for properties if no field saved
                        if (!binding.bindings.Any(b => b.component == comp))
                        {
                            foreach (var name in candidateNames)
                            {
                                var p = ct.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (p != null && p.CanRead && (p.CanWrite || p.GetSetMethod(true) != null))
                                {
                                    try
                                    {
                                        var orig = p.GetValue(comp, null);
                                        binding.bindings.Add(new MemberBinding { component = comp, property = p, originalValue = orig });
                                        break;
                                    }
                                    catch { /* ignore */ }
                                }
                            }
                        }
                    }

                    // Only include entries with at least one candidate binding
                    if (binding.bindings.Count > 0)
                    {
                        entries.Add(binding);
                        // Clear the bindings now (no yields)
                        foreach (var mb in binding.bindings)
                        {
                            try
                            {
                                if (mb.field != null) mb.field.SetValue(mb.component, null);
                                else if (mb.property != null) mb.property.SetValue(mb.component, null, null);
                            }
                            catch (Exception ex) { Debug.LogWarning($"[ChanceCraft] Failed clearing binding on {mb.component.GetType().FullName}: {ex}"); }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ChanceCraft] Exception preparing per-entry rebinds: {ex}");
        }

        // If we cleared some entries, yield one frame for the UI to pick up cleared state
        if (entries.Count > 0)
        {
            Debug.Log($"[ChanceCraft] DelayedForceRefresh: cleared {entries.Count} entry bindings; restoring entries one-by-one");
            yield return null;

            // Restore one-by-one, yield between restores to force per-entry rebind
            foreach (var entry in entries)
            {
                if (entry == null) continue;
                foreach (var mb in entry.bindings)
                {
                    try
                    {
                        if (mb.field != null) mb.field.SetValue(mb.component, mb.originalValue);
                        else if (mb.property != null) mb.property.SetValue(mb.component, mb.originalValue, null);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ChanceCraft] Failed restoring binding on {mb.component.GetType().FullName}: {ex}");
                    }
                }

                // try to invoke an update/refresh method on the entry's components to force visual update
                try
                {
                    var comps = entry.transform.GetComponents<Component>();
                    foreach (var comp in comps)
                    {
                        if (comp == null) continue;
                        TryInvokeZeroArgRefreshMethods(comp);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ChanceCraft] Exception invoking refresh methods on entry {entry.transform.name}: {ex}");
                }

                // Give UI a frame to react to this single entry restoration
                yield return null;
            }

            // Final canvas update
            try { Canvas.ForceUpdateCanvases(); }
            catch (Exception ex) { Debug.LogWarning($"[ChanceCraft] Canvas.ForceUpdateCanvases failed after entries restore: {ex}"); }
        }
        else
        {
            // nothing to restore, ensure canvases updated
            try { Canvas.ForceUpdateCanvases(); }
            catch (Exception ex) { Debug.LogWarning($"[ChanceCraft] Canvas.ForceUpdateCanvases failed: {ex}"); }
        }

        Debug.Log($"[ChanceCraft] UIRemoteRefresher: finished enhanced refresh for panel: {panelRoot?.name} (entries restored={entries.Count}, invokedPanelRefresh={invokedPanelRefresh})");
        yield break;
    }

    // Helper: try common zero-arg refresh/update method names on a component
    private void TryInvokeZeroArgRefreshMethods(Component comp)
    {
        if (comp == null) return;
        var type = comp.GetType();
        string[] methodNames = new[] { "Refresh", "Update", "Rebuild", "Rebind", "Populate", "OnSelected", "OnChanged", "SetSelected", "UpdateSelected", "UpdateRecipe", "UpdateList", "SetItem" };
        var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var name in methodNames)
        {
            var m = methods.FirstOrDefault(mi => mi.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 && mi.GetParameters().Length == 0);
            if (m != null)
            {
                try
                {
                    m.Invoke(comp, null);
                    Debug.Log($"[ChanceCraft] Invoked {type.FullName}.{m.Name}()");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ChanceCraft] Failed invoking {type.FullName}.{m.Name}: {ex}");
                }
            }
        }
    }

    // Try to find a crafting-like component and call refresh/update methods (used earlier)
    private bool TryInvokeCraftingPanelRefresh(GameObject panelRoot)
    {
        bool anyInvoked = false;
        string[] candidateTypes = new[] { "CraftingPanel", "CookingPanel", "RecipePanel", "CraftingUI", "UI_Crafting", "Crafting" };

        Type foundType = null;
        Component foundComp = null;

        foreach (var name in candidateTypes)
        {
            var t = Type.GetType(name + ", Assembly-CSharp") ?? Type.GetType(name);
            if (t != null)
            {
                var comp = panelRoot.GetComponent(t) ?? UnityEngine.Object.FindObjectOfType(t) as Component;
                if (comp != null)
                {
                    foundType = t;
                    foundComp = comp;
                    Debug.Log($"[ChanceCraft] Found crafting-like component '{t.FullName}' on '{comp.gameObject.name}'");
                    break;
                }
            }
        }

        if (foundComp == null)
        {
            foreach (var name in candidateTypes)
            {
                var t = Type.GetType(name + ", Assembly-CSharp") ?? Type.GetType(name);
                if (t == null) continue;
                var comp = UnityEngine.Object.FindObjectOfType(t) as Component;
                if (comp != null)
                {
                    foundType = t;
                    foundComp = comp;
                    Debug.Log($"[ChanceCraft] Found crafting-like component globally: '{t.FullName}' on '{comp.gameObject.name}'");
                    break;
                }
            }
        }

        if (foundComp == null || foundType == null) return false;

        string[] methodNamePatterns = new[]
        {
            "Refresh","Update","Rebuild","Rebind","Populate","OnSelected","OnChanged","SetSelected","UpdateSelected","UpdateRecipe","UpdateList"
        };

        var methods = foundType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var pattern in methodNamePatterns)
        {
            var matches = methods.Where(m => m.Name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0 && m.GetParameters().Length == 0).ToArray();
            foreach (var m in matches)
            {
                try
                {
                    m.Invoke(foundComp, null);
                    Debug.Log($"[ChanceCraft] Invoked {foundType.FullName}.{m.Name}()");
                    anyInvoked = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ChanceCraft] Failed invoking {foundType.FullName}.{m.Name}: {ex}");
                }
            }
        }

        return anyInvoked;
    }

    // Helper classes for storing per-entry bindings
    private class EntryBinding
    {
        public Transform transform;
        public List<MemberBinding> bindings = new List<MemberBinding>();
    }

    private class MemberBinding
    {
        public Component component;
        public FieldInfo field;
        public PropertyInfo property;
        public object originalValue;
    }
}