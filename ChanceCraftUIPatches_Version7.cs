using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

/// <summary>
/// Safer CraftingPanelPatches: find a UI-like crafting panel type (prefer types containing "Panel" or "UI"
/// and instances that are under a Canvas), patch candidate refresh methods, and run the simulated close/open coroutine.
/// Call CraftingPanelPatches.InstallPatches() once during mod initialization.
/// </summary>
public static class CraftingPanelPatches
{
    private static Harmony _harmony;
    private static readonly string HarmonyId = "com.chancecraft.uiRefreshPatch";

    private static readonly string[] CandidateMethodNames = new[]
    {
        "SetSelectedRecipe","SetSelected","OnSelected","SelectRecipe","UpdateSelected",
        "Refresh","Rebind","UpdateRecipe","Populate","OnChanged","OnEnable","Show"
    };

    // A small list of type-name substrings we consider as non-UI station/prefab types to exclude
    private static readonly string[] ExcludeTypeSubstrings = new[] { "piece", "workbench", "station", "piece_", "piece" };

    public static void InstallPatches()
    {
        try
        {
            if (_harmony != null) return;
            _harmony = new Harmony(HarmonyId);

            // 1) Strict search: find types whose name contains "Panel" or "UI" AND contains "Craft" or "Recipe"
            // and which have an instance in scene that is under a Canvas.
            Type chosenType = null;

            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => {
                    try { return a.GetTypes(); } catch { return Type.EmptyTypes; }
                })
                .Where(t => t.IsClass && !t.IsAbstract)
                .ToArray();

            // Try prioritized search
            var candidateTypes = allTypes.Where(t =>
            {
                var n = t.Name.ToLowerInvariant();
                bool nameLooksLikeUi = n.Contains("panel") || n.Contains("ui") || n.Contains("window");
                bool nameIsCraftRelated = n.Contains("craft") || n.Contains("recipe") || n.Contains("workshop") || n.Contains("bench");
                bool excluded = ExcludeTypeSubstrings.Any(s => n.Contains(s));
                return nameLooksLikeUi && nameIsCraftRelated && !excluded;
            }).ToArray();

            foreach (var t in candidateTypes)
            {
                try
                {
                    var inst = UnityEngine.Object.FindObjectOfType(t) as Component;
                    if (inst == null) continue;
                    // require that the found instance is under a Canvas
                    var canvas = inst.GetComponentInParent<Canvas>(true);
                    if (canvas != null)
                    {
                        chosenType = t;
                        Debug.Log($"[ChanceCraft] CraftingPanelPatches: selected UI-like type by Canvas check: {t.FullName} (instance on {inst.gameObject.name})");
                        break;
                    }
                }
                catch { /* ignore reflection / find errors */ }
            }

            // 2) If none found, try looser search: types with "panel" or "ui" in name regardless; pick any that has a scene instance.
            if (chosenType == null)
            {
                var looseCandidates = allTypes.Where(t =>
                {
                    var n = t.Name.ToLowerInvariant();
                    bool nameLooksLikeUi = n.Contains("panel") || n.Contains("ui") || n.Contains("window");
                    bool excluded = ExcludeTypeSubstrings.Any(s => n.Contains(s));
                    return nameLooksLikeUi && !excluded;
                }).ToArray();

                foreach (var t in looseCandidates)
                {
                    try
                    {
                        var inst = UnityEngine.Object.FindObjectOfType(t) as Component;
                        if (inst != null)
                        {
                            chosenType = t;
                            Debug.Log($"[ChanceCraft] CraftingPanelPatches: selected UI-like type by loose check: {t.FullName} (instance on {inst.gameObject.name})");
                            break;
                        }
                    }
                    catch { }
                }
            }

            // 3) If still none found, as last resort search for any type with "craft" or "recipe" in name but avoid piece/workbench types.
            if (chosenType == null)
            {
                var lastCandidates = allTypes.Where(t =>
                {
                    var n = t.Name.ToLowerInvariant();
                    bool craftRelated = n.Contains("craft") || n.Contains("recipe") || n.Contains("bench") || n.Contains("workshop");
                    bool excluded = ExcludeTypeSubstrings.Any(s => n.Contains(s));
                    return craftRelated && !excluded;
                }).ToArray();

                foreach (var t in lastCandidates)
                {
                    try
                    {
                        var inst = UnityEngine.Object.FindObjectOfType(t) as Component;
                        if (inst != null)
                        {
                            chosenType = t;
                            Debug.Log($"[ChanceCraft] CraftingPanelPatches: selected type by fallback: {t.FullName} (instance on {inst.gameObject.name})");
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (chosenType == null)
            {
                // Diagnostic: log a short sample of candidate types we considered so it's easy to tell why none matched
                var sample = allTypes
                    .Where(t => (t.Name.IndexOf("craft", StringComparison.OrdinalIgnoreCase) >= 0 || t.Name.IndexOf("recipe", StringComparison.OrdinalIgnoreCase) >= 0
                              || t.Name.IndexOf("panel", StringComparison.OrdinalIgnoreCase) >= 0 || t.Name.IndexOf("ui", StringComparison.OrdinalIgnoreCase) >= 0)
                              && !ExcludeTypeSubstrings.Any(s => t.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0))
                    .Take(30)
                    .Select(t => t.FullName).ToArray();

                Debug.LogWarning("[ChanceCraft] CraftingPanelPatches: couldn't find a suitable crafting UI type. Sample candidates: " + string.Join(", ", sample));
                return;
            }

            // Patch candidate methods on chosenType
            var patched = new List<string>();
            foreach (var name in CandidateMethodNames)
            {
                var mi = AccessTools.Method(chosenType, name);
                if (mi == null)
                {
                    var methods = chosenType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                              .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                                              .ToArray();
                    mi = methods.FirstOrDefault();
                }

                if (mi != null)
                {
                    try
                    {
                        var postfix = new HarmonyMethod(typeof(CraftingPanelPatches).GetMethod(nameof(GenericPostfix), BindingFlags.Static | BindingFlags.NonPublic));
                        _harmony.Patch(mi, postfix: postfix);
                        patched.Add(mi.Name + mi.GetParametersSignature());
                        Debug.Log($"[ChanceCraft] Patched {chosenType.FullName}.{mi.Name} (params: {mi.GetParametersSignature()})");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ChanceCraft] Failed patching {chosenType.FullName}.{name}: {ex}");
                    }
                }
            }

            if (patched.Count == 0)
                Debug.LogWarning("[ChanceCraft] CraftingPanelPatches: no methods were patched on the selected type.");
            else
                Debug.Log("[ChanceCraft] CraftingPanelPatches: patched methods: " + string.Join(", ", patched));
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] InstallPatches exception: " + ex);
        }
    }

    // Generic postfix scheduled to run after the target method
    private static void GenericPostfix(object __instance)
    {
        try
        {
            if (__instance == null) return;
            var comp = __instance as Component;
            GameObject panelRoot = comp != null ? comp.gameObject : null;

            // If this is not a UI root, try to find nearest Canvas ancestor or a root with "Craft"/"Recipe" in name
            if (panelRoot != null)
            {
                var canvasAncestor = panelRoot.GetComponentInParent<Canvas>(true);
                if (canvasAncestor != null)
                {
                    panelRoot = canvasAncestor.gameObject;
                }
                else
                {
                    // walk up to see if any parent looks like a UI "Crafting" panel
                    var parent = panelRoot.transform;
                    for (int i = 0; i < 6 && parent != null; i++)
                    {
                        var n = parent.name.ToLowerInvariant();
                        if (n.Contains("craft") || n.Contains("recipe") || n.Contains("panel") || n.Contains("ui"))
                        {
                            panelRoot = parent.gameObject;
                            break;
                        }
                        parent = parent.parent;
                    }
                }
            }

            if (panelRoot == null)
            {
                var go = GameObject.Find("Crafting") ?? GameObject.Find("crafting");
                if (go != null) panelRoot = go;
            }

            if (panelRoot == null)
            {
                Debug.Log("[ChanceCraft] Crafting patch postfix: couldn't resolve panel root for simulated refresh.");
                return;
            }

            var refresher = UIRemoteRefresher.Instance;
            if (refresher == null)
            {
                Debug.LogWarning("[ChanceCraft] Crafting patch postfix: UIRemoteRefresher.Instance is null.");
                return;
            }

            refresher.StartCoroutine(SimulateCloseOpenThenRefresh(panelRoot));
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] GenericPostfix exception: " + ex);
        }
    }

    private static IEnumerator SimulateCloseOpenThenRefresh(GameObject panelRoot)
    {
        // wait one frame so the game's normal refresh completes
        yield return null;
        if (panelRoot == null) yield break;

        bool shouldToggle = false;
        try { shouldToggle = panelRoot.activeInHierarchy; }
        catch { shouldToggle = panelRoot != null; }

        if (shouldToggle)
        {
            try { panelRoot.SetActive(false); } catch (Exception ex) { Debug.LogWarning("[ChanceCraft] SimulatedCloseOpen SetActive(false) failed: " + ex); }
            yield return null;
            if (panelRoot == null) yield break;
            try { panelRoot.SetActive(true); } catch (Exception ex) { Debug.LogWarning("[ChanceCraft] SimulatedCloseOpen SetActive(true) failed: " + ex); }
            yield return null;
        }
        else
        {
            // give the UI a couple frames if we didn't toggle
            yield return null;
            yield return null;
        }

        try
        {
            UIRemoteRefresher.Instance?.RefreshNextFrame(panelRoot);
            Debug.Log("[ChanceCraft] SimulatedCloseOpen: requested final RefreshNextFrame for " + (panelRoot?.name ?? "<null>"));
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ChanceCraft] SimulateCloseOpenThenRefresh final refresh exception: " + ex);
        }

        yield break;
    }

    private static string GetParametersSignature(this MethodInfo mi)
    {
        try
        {
            var ps = mi.GetParameters();
            return "(" + string.Join(", ", ps.Select(p => p.ParameterType.Name)) + ")";
        }
        catch { return "()"; }
    }
}