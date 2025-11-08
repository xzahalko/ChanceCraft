using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ChanceCraft
{
    public class ChanceCraftUIRefreshUsage
    {
        // Public entrypoint used elsewhere
        public static void RefreshCraftingUiAfterChange()
        {
            try
            {
                var igInstance = FindInventoryGuiInstance();
                if (igInstance == null)
                {
                    Debug.LogWarning("[ChanceCraft] RefreshCraftingUiAfterChange: no InventoryGui instance found.");
                    return;
                }

                // Preserve selection state before invoking refresh methods
                int? savedIndex = PreserveUpgradeSelection(igInstance);

                // Invoke safe methods (parameterless or those for which we can build valid args)
                TryInvokeInventoryGuiUpdateMethods(igInstance);

                // Attempt to restore previous selection after refresh.
                // Use delayed restore because InventoryGui may rebuild asynchronously.
                if (savedIndex.HasValue)
                {
                    // Retry up to 5 times, waiting 1 frame between attempts (tunable)
                    RestoreSelectionDelayed(igInstance, savedIndex.Value, framesToWait: 1, attempts: 5);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChanceCraft] RefreshCraftingUiAfterChange: unexpected exception: {ex}");
            }
        }

        // Try to show a failure message (red warning) in-game via several reflection fallbacks.
        public void ShowFailureMessage(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text)) return;

                var messageHudType = FindTypeByName("MessageHud");
                if (messageHudType != null)
                {
                    var instanceField = messageHudType.GetField("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    if (instanceField != null)
                    {
                        var instance = instanceField.GetValue(null);
                        if (instance != null)
                        {
                            var methodCandidates = new[] { "ShowMessage", "AddMessage", "Show", "ShowText" };
                            foreach (var mname in methodCandidates)
                            {
                                var method = messageHudType.GetMethod(mname, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                    null, new Type[] { typeof(string) }, null);
                                if (method != null)
                                {
                                    method.Invoke(instance, new object[] { text });
                                    return;
                                }

                                var enumType = messageHudType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                                    .FirstOrDefault(t => t.Name.IndexOf("Message", StringComparison.OrdinalIgnoreCase) >= 0);
                                if (enumType != null)
                                {
                                    var method2 = messageHudType.GetMethod(mname, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (method2 != null)
                                    {
                                        var enumValues = Enum.GetValues(enumType);
                                        object enumVal = enumValues.Length > 0 ? enumValues.GetValue(0) : null;
                                        if (enumVal != null)
                                        {
                                            try
                                            {
                                                method2.Invoke(instance, new object[] { enumVal, text });
                                                return;
                                            }
                                            catch { /* ignore and continue */ }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                var ig = FindInventoryGuiInstance();
                if (ig != null)
                {
                    var t = ig.GetType();
                    var nameCandidates = new[] { "ShowError", "ShowMessage", "ShowFail", "ShowFailure", "DisplayMessage" };
                    foreach (var name in nameCandidates)
                    {
                        var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                            null, new Type[] { typeof(string) }, null);
                        if (m != null)
                        {
                            m.Invoke(ig, new object[] { text });
                            return;
                        }
                    }
                }

                var monos = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var m in monos)
                {
                    var mt = m.GetType();
                    var methods = mt.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(mi =>
                        {
                            if (mi.GetParameters().Length != 1) return false;
                            var p = mi.GetParameters()[0];
                            return p.ParameterType == typeof(string) &&
                                   (mi.Name.IndexOf("Show", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    mi.Name.IndexOf("Message", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    mi.Name.IndexOf("Display", StringComparison.OrdinalIgnoreCase) >= 0);
                        });

                    foreach (var method in methods)
                    {
                        try
                        {
                            method.Invoke(m, new object[] { text });
                            return;
                        }
                        catch { /* try next */ }
                    }
                }

                Debug.LogWarning($"[ChanceCraft] ShowFailureMessage fallback: {text}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChanceCraft] ShowFailureMessage: {ex}");
            }
        }

        // --- Preserve & restore selection ---

        private static int? PreserveUpgradeSelection(object igInstance)
        {
            try
            {
                var t = igInstance.GetType();

                string[] indexFieldNames = { "savedUpgradeIndex", "savedIndex", "m_savedIndex", "m_selectedUpgradeIndex", "selectedUpgradeIndex" };
                foreach (var fname in indexFieldNames)
                {
                    var f = t.GetField(fname, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null && f.FieldType == typeof(int))
                    {
                        var val = (int)f.GetValue(igInstance);
                        if (val >= 0) return val;
                    }

                    var p = t.GetProperty(fname, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (p != null && p.PropertyType == typeof(int))
                    {
                        var val = (int)p.GetValue(igInstance, null);
                        if (val >= 0) return val;
                    }
                }

                var targetField = t.GetField("_upgradeTargetItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                  ?? t.GetField("upgradeTargetItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var itemsField = t.GetField("m_upgradeItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                 ?? t.GetField("upgradeItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (targetField != null && itemsField != null)
                {
                    var target = targetField.GetValue(igInstance);
                    var arr = itemsField.GetValue(igInstance) as Array;
                    if (target != null && arr != null && arr.Length > 0)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            if (ReferenceEquals(arr.GetValue(i), target) || Equals(arr.GetValue(i), target))
                                return i;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChanceCraft] PreserveUpgradeSelection: exception while reading selection: {ex}");
            }

            return null;
        }

        private static void RestoreUpgradeSelection(object igInstance, int index)
        {
            try
            {
                var t = igInstance.GetType();

                string[] indexFieldNames = { "savedUpgradeIndex", "savedIndex", "m_savedIndex", "m_selectedUpgradeIndex", "selectedUpgradeIndex" };
                foreach (var fname in indexFieldNames)
                {
                    var f = t.GetField(fname, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null && f.FieldType == typeof(int))
                    {
                        f.SetValue(igInstance, index);
                        Debug.Log($"[ChanceCraft] RestoreUpgradeSelection: restored index via field {fname}={index}");
                        return;
                    }

                    var p = t.GetProperty(fname, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (p != null && p.PropertyType == typeof(int) && p.CanWrite)
                    {
                        p.SetValue(igInstance, index, null);
                        Debug.Log($"[ChanceCraft] RestoreUpgradeSelection: restored index via property {fname}={index}");
                        return;
                    }
                }

                string[] selectMethodNames = { "SelectUpgradeIndex", "SelectUpgradeItem", "SetSelectedUpgrade", "SetSelectedIndex", "FocusUpgradeItem", "FocusPreviouslySelectedUpgradeItem" };
                foreach (var name in selectMethodNames)
                {
                    var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new Type[] { typeof(int) }, null);
                    if (m != null)
                    {
                        try
                        {
                            m.Invoke(igInstance, new object[] { index });
                            Debug.Log($"[ChanceCraft] RestoreUpgradeSelection: restored index via method {name}({index})");
                            return;
                        }
                        catch (TargetInvocationException tie)
                        {
                            Debug.LogWarning($"[ChanceCraft] RestoreUpgradeSelection: method {name} threw: {tie.InnerException ?? tie}");
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[ChanceCraft] RestoreUpgradeSelection: method {name} invocation exception: {ex}");
                        }
                    }
                }

                var itemsField = t.GetField("m_upgradeItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                 ?? t.GetField("upgradeItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var targetField2 = t.GetField("_upgradeTargetItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                  ?? t.GetField("upgradeTargetItem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (itemsField != null && targetField2 != null)
                {
                    var arr = itemsField.GetValue(igInstance) as Array;
                    if (arr != null && index >= 0 && index < arr.Length)
                    {
                        targetField2.SetValue(igInstance, arr.GetValue(index));
                        Debug.Log($"[ChanceCraft] RestoreUpgradeSelection: set _upgradeTargetItem from m_upgradeItems[{index}]");
                        var refresh = t.GetMethod("RefreshUpgradeItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                      ?? t.GetMethod("RebuildUpgradeList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (refresh != null)
                        {
                            try { refresh.Invoke(igInstance, null); }
                            catch { }
                        }
                        return;
                    }
                }

                Debug.LogWarning("[ChanceCraft] RestoreUpgradeSelection: could not restore selection (no known fields/methods found)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChanceCraft] RestoreUpgradeSelection: exception while restoring selection: {ex}");
            }
        }

        // Delayed restore: schedule a restore after N frames using a short-lived MonoBehaviour
        private static void RestoreSelectionDelayed(object igInstance, int index, int framesToWait = 1, int attempts = 5)
        {
            try
            {
                // Create a GameObject to host the restorer
                var go = new GameObject("ChanceCraft_SelectionRestorer");
                UnityEngine.Object.DontDestroyOnLoad(go);
                var restorer = go.AddComponent<SelectionRestorer>();
                restorer.Init(igInstance, index, framesToWait, attempts);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChanceCraft] RestoreSelectionDelayed: exception scheduling delayed restore: {ex}");
            }
        }

        private class SelectionRestorer : MonoBehaviour
        {
            private object _igInstance;
            private int _index;
            private int _framesBetweenAttempts;
            private int _attemptsLeft;

            public void Init(object igInstance, int index, int framesBetweenAttempts, int attempts)
            {
                _igInstance = igInstance;
                _index = index;
                _framesBetweenAttempts = Math.Max(1, framesBetweenAttempts);
                _attemptsLeft = Math.Max(1, attempts);
                StartCoroutine(Run());
            }

            private IEnumerator Run()
            {
                try
                {
                    while (_attemptsLeft > 0)
                    {
                        for (int i = 0; i < _framesBetweenAttempts; i++)
                            yield return new WaitForEndOfFrame();

                        try
                        {
                            if (_igInstance != null)
                            {
                                RestoreUpgradeSelection(_igInstance, _index);
                                Debug.Log($"[ChanceCraft] SelectionRestorer: attempted restore, attemptsLeft={_attemptsLeft} index={_index}");

                                // verify whether the selection stuck
                                var preserved = PreserveUpgradeSelection(_igInstance);
                                if (preserved.HasValue && preserved.Value == _index)
                                {
                                    Debug.Log($"[ChanceCraft] SelectionRestorer: restore succeeded index={_index}");
                                    yield break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[ChanceCraft] SelectionRestorer: restoration attempt threw: {ex}");
                        }

                        _attemptsLeft--;
                    }

                    Debug.LogWarning($"[ChanceCraft] SelectionRestorer: exhausted attempts to restore selection index={_index}");
                }
                finally
                {
                    // cleanup
                    Destroy(gameObject);
                }
            }
        }

        // --- Invocation helpers that try to provide safe args for known methods ---
        private static void TryInvokeInventoryGuiUpdateMethods(object igInstance)
        {
            if (igInstance == null) return;
            var type = igInstance.GetType();

            string[] methodNames = new[] { "UpdateCraftingPanel", "UpdateRecipeList", "UpdateInventory" };

            foreach (var name in methodNames)
            {
                try
                {
                    var candidates = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                         .Where(m => m.Name == name)
                                         .OrderBy(m => m.GetParameters().Length)
                                         .ToArray();

                    if (candidates.Length == 0)
                    {
                        Debug.Log($"[ChanceCraft] TryInvokeInventoryGuiUpdateMethods: method {name} not found on {type.FullName}");
                        continue;
                    }

                    MethodInfo chosen = null;
                    object[] args = null;

                    // prefer a truly parameterless overload
                    chosen = candidates.FirstOrDefault(m => m.GetParameters().Length == 0);
                    if (chosen != null)
                    {
                        args = null;
                    }
                    else
                    {
                        // Attempt to build appropriate non-null args for known methods.
                        foreach (var m in candidates)
                        {
                            var built = TryBuildArgsForMethod(m, igInstance);
                            if (built != null)
                            {
                                chosen = m;
                                args = built;
                                break;
                            }
                        }
                    }

                    if (chosen == null)
                    {
                        Debug.Log($"[ChanceCraft] TryInvokeInventoryGuiUpdateMethods: skipping {name} because no safe overload found");
                        continue;
                    }

                    Debug.Log($"[ChanceCraft] TryInvokeInventoryGuiUpdateMethods: invoking {name} overload with {(args == null ? 0 : args.Length)} arg(s)");
                    chosen.Invoke(igInstance, args);
                }
                catch (TargetInvocationException tie)
                {
                    Debug.LogWarning($"[ChanceCraft] TryInvokeInventoryGuiUpdateMethods: method {name} invoked but threw: {tie.InnerException ?? tie}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ChanceCraft] TryInvokeInventoryGuiUpdateMethods: method {name} invoked but threw: {ex}");
                }
            }
        }

        // Try to build non-null arguments for specific InventoryGui methods. Return null to skip overload.
        private static object[] TryBuildArgsForMethod(MethodInfo method, object igInstance)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0) return null;

            var paramTypes = parameters.Select(p => p.ParameterType).ToArray();

            if (string.Equals(method.Name, "UpdateRecipeList", StringComparison.OrdinalIgnoreCase)
                && paramTypes.Length == 1)
            {
                var recipesObj = TryFindRecipesCollectionOnInventoryGui(igInstance);
                if (recipesObj != null && paramTypes[0].IsInstanceOfType(recipesObj))
                {
                    return new object[] { recipesObj };
                }

                if (recipesObj != null && typeof(IEnumerable).IsAssignableFrom(paramTypes[0]))
                {
                    return new object[] { recipesObj };
                }

                Debug.Log($"[ChanceCraft] TryBuildArgsForMethod: cannot supply recipes for {method.Name}; skipping overload");
                return null;
            }

            if (string.Equals(method.Name, "UpdateInventory", StringComparison.OrdinalIgnoreCase)
                && paramTypes.Length == 1)
            {
                var playerObj = TryFindPlayerInstance();
                if (playerObj != null && paramTypes[0].IsInstanceOfType(playerObj))
                {
                    return new object[] { playerObj };
                }

                Debug.Log($"[ChanceCraft] TryBuildArgsForMethod: cannot supply Player for {method.Name}; skipping overload");
                return null;
            }

            // Generic attempt: try to supply instances for reference types and defaults for value types.
            var args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                var pt = p.ParameterType;

                if (!pt.IsValueType)
                {
                    var inst = TryFindInstanceOfTypeInScene(pt);
                    if (inst == null)
                    {
                        // If param is optional, use default null; otherwise skip
                        if (p.IsOptional)
                        {
                            args[i] = null;
                            continue;
                        }
                        Debug.Log($"[ChanceCraft] TryBuildArgsForMethod: cannot provide non-null instance of {pt.FullName} for method {method.Name}");
                        return null;
                    }
                    args[i] = inst;
                }
                else
                {
                    // For value types (enums/structs/primitives), try to create a default instance.
                    try
                    {
                        var defaultVal = Activator.CreateInstance(pt);
                        args[i] = defaultVal;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        Debug.Log($"[ChanceCraft] TryBuildArgsForMethod: failed to create default for {pt.FullName}: {ex}. Skipping overload.");
                        return null;
                    }
                }
            }

            return args;
        }

        // Try to locate a Player instance
        private static object TryFindPlayerInstance()
        {
            try
            {
                var playerType = FindTypeByName("Player");
                if (playerType != null)
                {
                    var f = playerType.GetField("m_localPlayer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (f != null)
                    {
                        var val = f.GetValue(null);
                        if (val != null) return val;
                    }

                    var p = playerType.GetProperty("localPlayer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? playerType.GetProperty("m_localPlayer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (p != null)
                    {
                        var val = p.GetValue(null, null);
                        if (val != null) return val;
                    }

                    var monos = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                    foreach (var m in monos)
                    {
                        if (m.GetType() == playerType || string.Equals(m.GetType().Name, "Player", StringComparison.OrdinalIgnoreCase))
                            return m;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChanceCraft] TryFindPlayerInstance: exception: {ex}");
            }
            return null;
        }

        private static object TryFindRecipesCollectionOnInventoryGui(object igInstance)
        {
            try
            {
                var t = igInstance.GetType();
                string[] names = { "m_recipes", "recipes", "m_recipeList", "m_recipeEntries", "m_allRecipes", "RecipeList" };
                foreach (var name in names)
                {
                    var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f != null)
                    {
                        var val = f.GetValue(igInstance);
                        if (val != null) return val;
                    }

                    var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (p != null)
                    {
                        var val = p.GetValue(igInstance, null);
                        if (val != null) return val;
                    }
                }

                var fields = t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var f in fields)
                {
                    var ft = f.FieldType;
                    if (typeof(IEnumerable).IsAssignableFrom(ft) && ft.FullName != null && ft.FullName.IndexOf("Recipe", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var val = f.GetValue(igInstance);
                        if (val != null) return val;
                    }
                }

                var props = t.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var p in props)
                {
                    var pt = p.PropertyType;
                    if (typeof(IEnumerable).IsAssignableFrom(pt) && pt.FullName != null && pt.FullName.IndexOf("Recipe", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var val = p.GetValue(igInstance, null);
                        if (val != null) return val;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChanceCraft] TryFindRecipesCollectionOnInventoryGui: exception: {ex}");
            }

            return null;
        }

        private static object TryFindInstanceOfTypeInScene(Type targetType)
        {
            try
            {
                var monos = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (var m in monos)
                {
                    var mt = m.GetType();
                    if (mt == targetType || string.Equals(mt.FullName, targetType.FullName, StringComparison.OrdinalIgnoreCase) || string.Equals(mt.Name, targetType.Name, StringComparison.OrdinalIgnoreCase))
                        return m;
                }
            }
            catch { }
            return null;
        }

        private static object[] BuildDefaultArgs(ParameterInfo[] parameters)
        {
            if (parameters == null || parameters.Length == 0) return null;
            var args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                try
                {
                    if (p.HasDefaultValue) { args[i] = p.DefaultValue; continue; }
                }
                catch { }
                var pt = p.ParameterType;
                if (!pt.IsValueType || Nullable.GetUnderlyingType(pt) != null) { args[i] = null; continue; }
                try { args[i] = Activator.CreateInstance(pt); }
                catch { args[i] = null; }
            }
            return args;
        }

        // Helpers to find InventoryGui type/instance
        private static UnityEngine.Object FindInventoryGuiInstance()
        {
            var inventoryType = GetInventoryGuiType();
            if (inventoryType != null)
            {
                var all = UnityEngine.Object.FindObjectsOfType(inventoryType);
                if (all != null && all.Length > 0)
                    return all[0];
            }

            var monos = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var m in monos)
            {
                var t = m.GetType();
                if (string.Equals(t.Name, "InventoryGui", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.FullName, "InventoryGui", StringComparison.OrdinalIgnoreCase))
                {
                    return m;
                }
            }

            return null;
        }

        private static Type GetInventoryGuiType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType("InventoryGui") ?? asm.GetType("InventoryGui", false, true);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static Type FindTypeByName(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name, false, true);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}