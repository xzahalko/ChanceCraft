// Updated ChanceCraft.cs
// Fixes & changes:
// - Do NOT modify crafting behavior.
// - Separate crafting and upgrading flows more explicitly.
// - Upgrading now goes through the same RNG chance calculation as crafting (weaponSuccessChance, armorSuccessChance, arrowSuccessChance).
//   Upgrades can now fail based on those chances (previously some upgrade flows were effectively always successful).
// - Implemented an explicit isUpgradeCall parameter to TrySpawnCraftEffect so callers can clearly request upgrade-handling behavior.
// - Calls to TrySpawnCraftEffect inside InventoryGui.DoCrafting.Postfix were updated to indicate upgrade vs. craft contexts.
// - Preserve prior revert-on-failure behavior for upgrades: if the game already applied an in-place upgrade we attempt to revert quality/variant, then remove upgrade resources.
// - Fixed numerous truncated/logging/string/placeholder errors and removed stray "..." sequences that caused compile errors.
// Note: Replace your existing ChanceCraft.cs with this file and rebuild.

using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Policy;
using System.Threading;
using UnityEngine;
using static HitData;
using static Room;
using static UnityEngine.RectTransform;
using static UnityEngine.Scripting.GarbageCollector;
using UnityEngine.UI;

namespace ChanceCraft
{
    [BepInPlugin(pluginID, pluginName, pluginVersion)]
    public class ChanceCraftPlugin : BaseUnityPlugin
    {
        private static ConfigEntry<float> weaponSuccessChance;
        private static ConfigEntry<float> armorSuccessChance;
        private static ConfigEntry<float> arrowSuccessChance;

        public const string pluginID = "deep.ChanceCraft";
        public const string pluginName = "Chance Craft";
        public const string pluginVersion = "1.1.2";

        private Harmony _harmony;
        private static bool IsDoCraft;

        // Snapshot & upgrade detection state (single-call snapshot)
        private static HashSet<ItemDrop.ItemData> _preCraftSnapshot = null;
        private static Recipe _snapshotRecipe = null;

        // Store pre-craft qualities so we can reliably detect in-place upgrades
        private static Dictionary<ItemDrop.ItemData, (int quality, int variant)> _preCraftSnapshotData = null;

        // Track suppressed logical recipes across GUI reopenings by a fingerprint
        private static HashSet<string> _suppressedRecipeKeys = new HashSet<string>();
        private static readonly object _suppressedRecipeKeysLock = new object();

        private static ItemDrop.ItemData _upgradeTargetItem = null;   // exact inventory item being upgraded (if detected)
        private static Recipe _upgradeRecipe = null;
        private static Recipe _upgradeGuiRecipe = null;
        private static bool _isUpgradeDetected = false;
        private static int _upgradeTargetItemIndex = -1;
        private static Dictionary<int, int> _preCraftSnapshotHashQuality = null;

        // store GUI-provided requirement list (if any) for this crafting/upgrade call
        private static List<(string name, int amount)> _upgradeGuiRequirements = null;

        private static ConfigEntry<bool> _loggingEnabled;

        [UsedImplicitly]
        void Awake()
        {
            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), "ChanceCraft");

            _loggingEnabled = Config.Bind("Logging", "Logging Enabled", true, "Enable logging");
            weaponSuccessChance = Config.Bind("General", "WeaponSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft weapons (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));
            armorSuccessChance = Config.Bind("General", "ArmorSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft armors (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));
            arrowSuccessChance = Config.Bind("General", "ArrowSuccessChance", 0.6f, new ConfigDescription("Chance to successfully craft arrows (0.0 - 1.0)", new AcceptableValueRange<float>(0f, 1f)));

            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting weapons success chance set to {weaponSuccessChance.Value * 100}%.");
            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting armors success chance set to {armorSuccessChance.Value * 100}%.");
            Logger.LogInfo($"ChanceCraft plugin loaded. Crafting arrow success chance set to {arrowSuccessChance.Value * 100}%.");

            UnityEngine.Debug.LogWarning("[ChanceCraft] Awake called!");
            Game.isModded = true;
        }

        [UsedImplicitly]
        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private static string ItemInfo(ItemDrop.ItemData it)
        {
            if (it == null) return "<null>";
            try
            {
                return $"[{it.GetHashCode():X8}] name='{it.m_shared?.m_name}' q={it.m_quality} v={it.m_variant} stack={it.m_stack}";
            }
            catch { return "<bad item>"; }
        }

        private static string RecipeInfo(Recipe r)
        {
            if (r == null) return "<null>";
            try
            {
                var name = r.m_item?.m_itemData?.m_shared?.m_name ?? "<no-result>";
                var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                string resList = "";
                if (resourcesField != null)
                {
                    var resObj = resourcesField.GetValue(r) as System.Collections.IEnumerable;
                    if (resObj != null)
                    {
                        var parts = new List<string>();
                        foreach (var req in resObj)
                        {
                            try
                            {
                                var t = req.GetType();
                                var amount = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req) ?? "?";
                                var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                string resName = null;
                                try
                                {
                                    var itemData = resItem?.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                                    var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                    resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                }
                                catch { }
                                parts.Add($"{resName ?? "<unknown>"}:{amount}");
                            }
                            catch { parts.Add("<res-err>"); }
                        }
                        resList = string.Join(", ", parts);
                    }
                }
                return $"Recipe(result='{name}', amount={r.m_amount}, resources=[{resList}])";
            }
            catch
            {
                return "<bad recipe>";
            }
        }

        private static string RecipeFingerprint(Recipe r)
        {
            if (r == null || r.m_item == null) return "<null>";
            try
            {
                var name = r.m_item.m_itemData?.m_shared?.m_name ?? "<no-result>";
                var amount = r.m_amount;
                var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var resObj = resourcesField?.GetValue(r) as System.Collections.IEnumerable;
                var parts = new List<string>();
                if (resObj != null)
                {
                    foreach (var req in resObj)
                    {
                        try
                        {
                            var t = req.GetType();
                            var amountObj = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                            int reqAmount = amountObj != null ? Convert.ToInt32(amountObj) : 0;
                            var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                            var itemData = resItem?.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                            var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                            var rname = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                            parts.Add($"{rname ?? "<unknown>"}:{reqAmount}");
                        }
                        catch { parts.Add("<res-err>"); }
                    }
                }
                return $"{name}|{amount}|{string.Join(",", parts)}";
            }
            catch { return "<fingerprint-err>"; }
        }

        private static bool RecipeConsumesResult(Recipe recipe)
        {
            if (recipe == null || recipe.m_item == null) return false;
            try
            {
                var craftedName = recipe.m_item.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(craftedName)) return false;

                var resourcesField = recipe.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (resourcesField == null) return false;
                var resourcesObj = resourcesField.GetValue(recipe) as System.Collections.IEnumerable;
                if (resourcesObj == null) return false;

                foreach (var req in resourcesObj)
                {
                    if (req == null) continue;
                    try
                    {
                        var t = req.GetType();
                        var resItemField = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var resItem = resItemField?.GetValue(req);
                        var itemDataField = resItem?.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var itemData = itemDataField?.GetValue(resItem);
                        var sharedField = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var shared = sharedField?.GetValue(itemData);
                        var nameField = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var resName = nameField?.GetValue(shared) as string;
                        if (!string.IsNullOrEmpty(resName) && string.Equals(resName, craftedName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                        // ignore malformed resource entry
                    }
                }
            }
            catch
            {
                // ignore
            }
            return false;
        }

        private static bool IsUpgradeOperation(InventoryGui gui, Recipe recipe)
        {
            if (recipe == null || gui == null) return false;

            try
            {
                var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                if (craftUpgradeField != null)
                {
                    try
                    {
                        object cv = craftUpgradeField.GetValue(gui);
                        if (cv is int v && v > 1)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation: m_craftUpgrade={v} -> upgrade detected");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation craftUpgrade check exception: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation: craftUpgrade reflection outer exception: {ex}");
            }

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
                            if (it.m_shared.m_name == craftedName && it.m_quality < craftedQuality)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation: found lower-quality inventory item {ItemInfo(it)} -> upgrade detected");
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation inventory check exception: {ex}");
            }

            try
            {
                if (RecipeConsumesResult(recipe))
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] IsUpgradeOperation: recipe consumes its result -> upgrade detected.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] IsUpgradeOperation RecipeConsumesResult exception: {ex}");
            }

            return false;
        }

        private static ItemDrop.ItemData GetSelectedInventoryItem(InventoryGui gui)
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

            var candidates = new[] { "m_selectedItem", "m_selected", "m_selectedItemData", "m_currentItem", "m_selectedInventoryItem", "m_selectedSlot", "m_selectedIndex", "m_selectedSlot" };
            foreach (var c in candidates)
            {
                try
                {
                    var val = TryGet(c);
                    if (val is ItemDrop.ItemData idata)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] GetSelectedInventoryItem: got ItemData from field '{c}': {ItemInfo(idata)}");
                        return idata;
                    }
                    if (val != null)
                    {
                        var valType = val.GetType();
                        var innerField = valType.GetField("m_item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (innerField != null)
                        {
                            var inner = innerField.GetValue(val);
                            if (inner is ItemDrop.ItemData ii)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] GetSelectedInventoryItem: got inner ItemData from '{c}.m_item': {ItemInfo(ii)}");
                                return ii;
                            }
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
                        if (idx >= 0 && idx < all.Count)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] GetSelectedInventoryItem: inferred selected by slot idx {idx}: {ItemInfo(all[idx])}");
                            return all[idx];
                        }
                    }
                }
            }
            catch { /* ignore */ }

            return null;
        }

        // Try to find a Recipe object embedded in wrappers
        private static bool TryExtractRecipeFromWrapper(object wrapper, Recipe excludeRecipe, out Recipe foundRecipe, out string foundPath, int maxDepth = 3)
        {
            foundRecipe = null;
            foundPath = null;
            if (wrapper == null) return false;

            try
            {
                var seen = new HashSet<int>();
                var q = new List<Tuple<object, string, int>>();
                q.Add(Tuple.Create(wrapper, "root", 0));
                int qi = 0;

                while (qi < q.Count)
                {
                    var node = q[qi++];
                    var obj = node.Item1;
                    var path = node.Item2;
                    var depth = node.Item3;

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
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] TryExtractRecipeFromWrapper: found Recipe at path '{path}': {RecipeInfo(r)} (excluded? False)");
                            return true;
                        }
                        else
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] TryExtractRecipeFromWrapper: found Recipe at path '{path}' but it matches excluded recipe -> skipping");
                        }
                    }

                    if (depth >= maxDepth) continue;

                    Type t = obj.GetType();
                    string typeName = t.Name ?? "";
                    bool likelyWrapper = typeName.IndexOf("Recipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         typeName.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         typeName.IndexOf("RecipeData", StringComparison.OrdinalIgnoreCase) >= 0;

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
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TryExtractRecipeFromWrapper: found Recipe field '{f.Name}' at path '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                    return true;
                                }
                            }

                            if (f.Name.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                q.Add(Tuple.Create(val, $"{path}.{f.Name}", depth + 1));
                                continue;
                            }

                            if (val is IEnumerable ie && !(val is string))
                            {
                                int idx = 0;
                                foreach (var elem in ie)
                                {
                                    if (elem == null) continue;
                                    if (elem is Recipe rr && !ReferenceEquals(rr, excludeRecipe))
                                    {
                                        foundRecipe = rr;
                                        foundPath = $"{path}.{f.Name}[{idx}]";
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TryExtractRecipeFromWrapper: found Recipe in enumerable '{f.Name}' at '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                        return true;
                                    }
                                    if (elem != null && idx < 5)
                                        q.Add(Tuple.Create(elem, $"{path}.{f.Name}[{idx}]", depth + 1));
                                    idx++;
                                    if (idx > 10) break;
                                }
                                continue;
                            }

                            if (likelyWrapper || depth < maxDepth - 1)
                                q.Add(Tuple.Create(val, $"{path}.{f.Name}", depth + 1));
                        }
                        catch { /* ignore */ }
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
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TryExtractRecipeFromWrapper: found Recipe property '{p.Name}' at path '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                    return true;
                                }
                            }

                            if (p.Name.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                q.Add(Tuple.Create(v, $"{path}.{p.Name}", depth + 1));
                                continue;
                            }

                            if (v is IEnumerable ie2 && !(v is string))
                            {
                                int idx = 0;
                                foreach (var elem in ie2)
                                {
                                    if (elem == null) continue;
                                    if (elem is Recipe rr && !ReferenceEquals(rr, excludeRecipe))
                                    {
                                        foundRecipe = rr;
                                        foundPath = $"{path}.{p.Name}[{idx}]";
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TryExtractRecipeFromWrapper: found Recipe in enumerable property '{p.Name}' at '{foundPath}' => {RecipeInfo(foundRecipe)}");
                                        return true;
                                    }
                                    if (elem != null && idx < 5)
                                        q.Add(Tuple.Create(elem, $"{path}.{p.Name}[{idx}]", depth + 1));
                                    idx++;
                                    if (idx > 10) break;
                                }
                                continue;
                            }

                            if (likelyWrapper || depth < maxDepth - 1)
                                q.Add(Tuple.Create(v, $"{path}.{p.Name}", depth + 1));
                        }
                        catch { /* ignore */ }
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] TryExtractRecipeFromWrapper exception: {ex}");
            }
            return false;
        }

        private static Recipe GetUpgradeRecipeFromGui(InventoryGui gui)
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
                            if (val is Recipe r)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: field '{name}' is Recipe -> {RecipeInfo(r)}");
                                return r;
                            }
                            if (val != null)
                            {
                                Recipe inner;
                                string path;
                                if (TryExtractRecipeFromWrapper(val, null, out inner, out path))
                                {
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: extracted inner Recipe from field '{name}' at path '{path}' -> {RecipeInfo(inner)}");
                                    return inner;
                                }
                                var prop = val.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (prop != null && prop.CanRead)
                                {
                                    var r2 = prop.GetValue(val) as Recipe;
                                    if (r2 != null)
                                    {
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: field '{name}'.Recipe -> {RecipeInfo(r2)}");
                                        return r2;
                                    }
                                }
                            }
                        }

                        var propInfo = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (propInfo != null)
                        {
                            var val2 = propInfo.GetValue(gui);
                            if (val2 is Recipe r3)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: property '{name}' is Recipe -> {RecipeInfo(r3)}");
                                return r3;
                            }
                            if (val2 != null)
                            {
                                Recipe inner3;
                                string path3;
                                if (TryExtractRecipeFromWrapper(val2, null, out inner3, out path3))
                                {
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: extracted inner Recipe from prop '{name}' at path '{path3}' -> {RecipeInfo(inner3)}");
                                    return inner3;
                                }
                                var p2 = val2.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (p2 != null && p2.CanRead)
                                {
                                    var r4 = p2.GetValue(val2) as Recipe;
                                    if (r4 != null) return r4;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: candidate '{name}' check threw: {ex}"); }
                }

                try
                {
                    var selectedRecipeField = t.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (selectedRecipeField != null)
                    {
                        var wrapper = selectedRecipeField.GetValue(gui);
                        if (wrapper != null)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: inspecting m_selectedRecipe wrapper type={wrapper.GetType().FullName}");
                            Recipe inner;
                            string path;
                            Recipe wrapperSelected = null;
                            try
                            {
                                var rp = wrapper.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (rp != null) wrapperSelected = rp.GetValue(wrapper) as Recipe;
                            }
                            catch { /* ignore */ }

                            if (TryExtractRecipeFromWrapper(wrapper, wrapperSelected, out inner, out path))
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: extracted Recipe from m_selectedRecipe wrapper at '{path}' -> {RecipeInfo(inner)}");
                                return inner;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: m_selectedRecipe-wrapper inspection threw: {ex}");
                }

                try
                {
                    var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    foreach (var f in fields)
                    {
                        try
                        {
                            var v = f.GetValue(gui);
                            if (v == null) continue;
                            if (v is Recipe rr)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: fallback found Recipe in field '{f.Name}' -> {RecipeInfo(rr)}");
                                return rr;
                            }
                            Recipe inner;
                            string path;
                            if (TryExtractRecipeFromWrapper(v, null, out inner, out path))
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui: fallback extracted Recipe from field '{f.Name}' at '{path}' -> {RecipeInfo(inner)}");
                                return inner;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                UnityEngine.Debug.LogWarning("[ChanceCraft] GetUpgradeRecipeFromGui: no GUI-wrapped upgrade recipe found (returned null).");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] GetUpgradeRecipeFromGui exception: {ex}");
            }
            return null;
        }

        // RemoveAmountFromInventorySkippingTarget: unchanged behavior
        private static int RemoveAmountFromInventorySkippingTarget(Inventory inventory, ItemDrop.ItemData upgradeTargetItem, string resourceName, int amount)
        {
            if (inventory == null || string.IsNullOrEmpty(resourceName) || amount <= 0) return 0;

            int remaining = amount;
            var items = inventory.GetAllItems();

            try
            {
                // 1) exact token match
                for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    var it = items[i];
                    if (it == null || it.m_shared == null) continue;
                    if (upgradeTargetItem != null && ReferenceEquals(it, upgradeTargetItem)) continue;
                    if (it.m_shared.m_name != resourceName) continue;
                    int toRemove = Math.Min(it.m_stack, remaining);
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: removing {toRemove} from {ItemInfo(it)} matching exact '{resourceName}'");
                    it.m_stack -= toRemove;
                    remaining -= toRemove;
                    if (it.m_stack <= 0)
                    {
                        try { inventory.RemoveItem(it); } catch { }
                    }
                }

                // 2) case-insensitive exact
                for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    var it = items[i];
                    if (it == null || it.m_shared == null) continue;
                    if (upgradeTargetItem != null && ReferenceEquals(it, upgradeTargetItem)) continue;
                    if (!string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase)) continue;
                    int toRemove = Math.Min(it.m_stack, remaining);
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: removing {toRemove} from {ItemInfo(it)} matching case-insensitive '{resourceName}'");
                    it.m_stack -= toRemove;
                    remaining -= toRemove;
                    if (it.m_stack <= 0) { try { inventory.RemoveItem(it); } catch { } }
                }

                // 3) contains
                for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    var it = items[i];
                    if (it == null || it.m_shared == null) continue;
                    if (upgradeTargetItem != null && ReferenceEquals(it, upgradeTargetItem)) continue;
                    var name = it.m_shared.m_name;
                    if (name == null || resourceName == null) continue;
                    if (name.IndexOf(resourceName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int toRemove = Math.Min(it.m_stack, remaining);
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: removing {toRemove} from {ItemInfo(it)} containing '{resourceName}'");
                    it.m_stack -= toRemove;
                    remaining -= toRemove;
                    if (it.m_stack <= 0) { try { inventory.RemoveItem(it); } catch { } }
                }

                // 4) reverse contains
                for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                {
                    var it = items[i];
                    if (it == null || it.m_shared == null) continue;
                    if (upgradeTargetItem != null && ReferenceEquals(it, upgradeTargetItem)) continue;
                    var name = it.m_shared.m_name;
                    if (name == null || resourceName == null) continue;
                    if (resourceName.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    int toRemove = Math.Min(it.m_stack, remaining);
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: removing {toRemove} from {ItemInfo(it)} reverse-containing '{resourceName}'");
                    it.m_stack -= toRemove;
                    remaining -= toRemove;
                    if (it.m_stack <= 0) { try { inventory.RemoveItem(it); } catch { } }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: exception during attempts: {ex}");
            }

            if (remaining > 0)
            {
                try
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: fallback RemoveItem by name '{resourceName}' remaining={remaining}");
                    inventory.RemoveItem(resourceName, remaining);
                    remaining = 0;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventorySkippingTarget: RemoveItem exception: {ex}");
                }
            }

            return amount - remaining;
        }

        // Find best candidate upgrade recipe in ObjectDB (unchanged)
        private static Recipe FindBestUpgradeRecipeCandidate(Recipe craftRecipe)
        {
            try
            {
                if (craftRecipe == null) return null;
                string resultName = craftRecipe.m_item?.m_itemData?.m_shared?.m_name;
                if (string.IsNullOrEmpty(resultName)) return null;
                if (ObjectDB.instance == null)
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] FindBestUpgradeRecipeCandidate: ObjectDB.instance is null");
                    return null;
                }

                Recipe best = null;
                int bestScore = int.MaxValue;

                foreach (var r in ObjectDB.instance.m_recipes)
                {
                    try
                    {
                        if (r == null || r.m_item == null) continue;
                        var rn = r.m_item.m_itemData?.m_shared?.m_name;

                        var resourcesField = r.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var resourcesObj = resourcesField?.GetValue(r) as System.Collections.IEnumerable;
                        if (resourcesObj == null) continue;

                        int total = 0;
                        bool consumesResult = false;
                        var distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var req in resourcesObj)
                        {
                            if (req == null) continue;
                            try
                            {
                                var t = req.GetType();
                                var amountObj = t.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                int amount = 0;
                                try { amount = amountObj != null ? Convert.ToInt32(amountObj) : 0; } catch { amount = 0; }
                                total += Math.Max(0, amount);

                                var resItem = t.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                var itemData = resItem?.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(resItem);
                                var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                var resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                if (!string.IsNullOrEmpty(resName))
                                {
                                    distinctNames.Add(resName);
                                    if (string.Equals(resName, resultName, StringComparison.OrdinalIgnoreCase))
                                        consumesResult = true;
                                }
                            }
                            catch { }
                        }

                        bool resultNameMatch = !string.IsNullOrEmpty(rn) && string.Equals(rn, resultName, StringComparison.OrdinalIgnoreCase);

                        int score = total;
                        if (consumesResult) score -= 2000;
                        if (resultNameMatch) score -= 1500;

                        score = score * 10 + distinctNames.Count;

                        bool containsWood = false;
                        foreach (var n in distinctNames)
                        {
                            if (n != null && n.IndexOf("wood", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                containsWood = true;
                                break;
                            }
                        }
                        if (!containsWood) score -= 100;

                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = r;
                        }
                    }
                    catch { }
                }

                if (best != null)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] FindBestUpgradeRecipeCandidate: selected candidate {RecipeInfo(best)} score={bestScore}");
                }
                else
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] FindBestUpgradeRecipeCandidate: no candidate found for result '{craftRecipe.m_item?.m_itemData?.m_shared?.m_name}'");
                }

                return best;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] FindBestUpgradeRecipeCandidate exception: {ex}");
                return null;
            }
        }

        // InventoryGui.DoCrafting patch and helpers
        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        static class InventoryGuiDoCraftingPatch
        {
            private static readonly Dictionary<Recipe, object> _savedResources = new Dictionary<Recipe, object>();

            // Per-call state
            private static bool _suppressedThisCall = false;
            private static Recipe _savedRecipeForCall = null;

            [UsedImplicitly]
            static void Prefix(InventoryGui __instance)
            {
                _suppressedThisCall = false;
                _savedRecipeForCall = null;

                // reset upgrade/snapshot per call
                _isUpgradeDetected = false;
                _upgradeTargetItem = null;
                _upgradeRecipe = null;
                _upgradeGuiRecipe = null;
                _preCraftSnapshot = null;
                _preCraftSnapshotData = null;
                _snapshotRecipe = null;
                _upgradeGuiRequirements = null;

                DumpInventoryState("Prefix-after-snapshot", Player.m_localPlayer);

                try
                {
                    var selectedRecipeField = typeof(InventoryGui).GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (selectedRecipeField == null) return;
                    object value = selectedRecipeField.GetValue(__instance);
                    Recipe selectedRecipe = null;
                    if (value != null && value.GetType().Name == "RecipeDataPair")
                    {
                        var recipeProp = value.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (recipeProp != null) selectedRecipe = recipeProp.GetValue(value) as Recipe;
                    }
                    else
                    {
                        selectedRecipe = value as Recipe;
                    }
                    if (selectedRecipe == null) return;

                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: selectedRecipe = {RecipeInfo(selectedRecipe)}");

                    // Early discovery of GUI-wrapped recipe & DB candidate
                    try
                    {
                        var selKey = RecipeFingerprint(selectedRecipe);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: selectedRecipe fingerprint={selKey}");
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: selectedRecipe consumesResult={RecipeConsumesResult(selectedRecipe)}");

                        if (value != null)
                        {
                            Recipe extractedFromWrapper;
                            string extractedPath;
                            if (TryExtractRecipeFromWrapper(value, selectedRecipe, out extractedFromWrapper, out extractedPath))
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: extracted Recipe from selectedRecipe wrapper at '{extractedPath}' -> {RecipeInfo(extractedFromWrapper)}");
                                _upgradeGuiRecipe = extractedFromWrapper;
                                try
                                {
                                    var keyg = RecipeFingerprint(extractedFromWrapper);
                                    lock (_suppressedRecipeKeysLock) { _suppressedRecipeKeys.Add(keyg); }
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: recorded GUI-wrapped recipe fingerprint {keyg}");
                                }
                                catch { }
                            }
                            else
                            {
                                var earlyGuiRecipe = GetUpgradeRecipeFromGui(__instance);
                                if (earlyGuiRecipe != null)
                                {
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: earlyGuiRecipe detected = {RecipeInfo(earlyGuiRecipe)} (hash={earlyGuiRecipe?.GetHashCode():X8})");
                                    _upgradeGuiRecipe = earlyGuiRecipe;
                                    try
                                    {
                                        var keyg = RecipeFingerprint(earlyGuiRecipe);
                                        lock (_suppressedRecipeKeysLock) { _suppressedRecipeKeys.Add(keyg); }
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: recorded GUI-wrapped recipe fingerprint {keyg}");
                                    }
                                    catch { }
                                }
                                else
                                {
                                    UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix-DBG: no early GUI-wrapped recipe found.");
                                }
                            }
                        }

                        if (_upgradeGuiRecipe == null || ReferenceEquals(_upgradeGuiRecipe, selectedRecipe))
                        {
                            var candidateEarly = FindBestUpgradeRecipeCandidate(selectedRecipe);
                            if (candidateEarly != null)
                            {
                                _upgradeRecipe = candidateEarly;
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: stored ObjectDB candidate as _upgradeRecipe = {RecipeInfo(candidateEarly)}");
                                if (RecipeConsumesResult(candidateEarly))
                                {
                                    _isUpgradeDetected = true;
                                    UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix-DBG: ObjectDB candidate consumes result -> marking as upgrade-detected early.");
                                }
                            }
                            else
                            {
                                UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix-DBG: no ObjectDB upgrade candidate found early.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: exception during early upgrade discovery: {ex}");
                    }

                    // Save exact recipe instance for the call (so Postfix uses same object)
                    _savedRecipeForCall = selectedRecipe;

                    // --- GUI-provided requirements detection (treat as upgrade when GUI differs from recipe) ---
                    try
                    {
                        // Capture upgrade target item early if possible
                        if (_upgradeTargetItem == null)
                        {
                            try
                            {
                                var igType = typeof(InventoryGui);
                                var fCraftUpgradeItem = igType.GetField("m_craftUpgradeItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (fCraftUpgradeItem != null)
                                {
                                    var cand = fCraftUpgradeItem.GetValue(__instance) as ItemDrop.ItemData;
                                    if (cand != null) _upgradeTargetItem = cand;
                                }

                                if (_upgradeTargetItem == null)
                                {
                                    var fUpgradeItems = igType.GetField("m_upgradeItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    var fUpgradeIndex = igType.GetField("m_upgradeItemIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (fUpgradeItems != null)
                                    {
                                        var list = fUpgradeItems.GetValue(__instance) as System.Collections.IList;
                                        if (list != null && list.Count > 0)
                                        {
                                            int idx = -1;
                                            if (fUpgradeIndex != null)
                                            {
                                                var idxObj = fUpgradeIndex.GetValue(__instance);
                                                if (idxObj is int ii) idx = ii;
                                            }
                                            if (idx >= 0 && idx < list.Count)
                                            {
                                                var cand = list[idx] as ItemDrop.ItemData;
                                                if (cand != null) _upgradeTargetItem = cand;
                                            }
                                            else
                                            {
                                                var cand = list[0] as ItemDrop.ItemData;
                                                if (cand != null) _upgradeTargetItem = cand;
                                            }
                                        }
                                    }
                                }

                                if (_upgradeTargetItem == null)
                                {
                                    var candidateNames = new[] { "m_selectedItem", "m_selected", "m_selectedItemData", "m_selectedInventoryItem" };
                                    foreach (var nm in candidateNames)
                                    {
                                        try
                                        {
                                            var f = igType.GetField(nm, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (f != null)
                                            {
                                                var v = f.GetValue(__instance) as ItemDrop.ItemData;
                                                if (v != null)
                                                {
                                                    _upgradeTargetItem = v;
                                                    break;
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }

                                if (_upgradeTargetItem != null)
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: captured upgrade target early: {ItemInfo(_upgradeTargetItem)}");
                            }
                            catch { /* ignore */ }
                        }

                        // Read GUI requirement list (TryGetRequirementsFromGui now prefers m_amount total first)
                        List<(string name, int amount)> guiReqs;
                        if (TryGetRequirementsFromGui(__instance, out guiReqs) && guiReqs != null && guiReqs.Count > 0)
                        {
                            bool guiIndicatesUpgrade = false;

                            // 1) explicit craft-upgrade multiplier
                            try
                            {
                                var craftUpgradeFieldLocal = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                                int craftUpgradeVal = 1;
                                if (craftUpgradeFieldLocal != null)
                                {
                                    try
                                    {
                                        var cvObj = craftUpgradeFieldLocal.GetValue(__instance);
                                        if (cvObj is int v && v > 0) craftUpgradeVal = v;
                                    }
                                    catch { craftUpgradeVal = 1; }
                                }
                                if (craftUpgradeVal > 1) guiIndicatesUpgrade = true;
                            }
                            catch { }

                            // 2) captured target quality vs final quality
                            try
                            {
                                var finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                if (_upgradeTargetItem != null)
                                {
                                    var targetQuality = _upgradeTargetItem.m_quality;
                                    if (finalQuality > targetQuality) guiIndicatesUpgrade = true;
                                }
                            }
                            catch { }

                            // 3) recipe consumes result
                            try { if (RecipeConsumesResult(selectedRecipe)) guiIndicatesUpgrade = true; } catch { }

                            // 4) Compare GUI list to base recipe resources: if they differ, GUI likely shows upgrade costs
                            try
                            {
                                var baseResources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                var resourcesFieldBase = selectedRecipe.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var baseResourcesEnum = resourcesFieldBase?.GetValue(selectedRecipe) as System.Collections.IEnumerable;
                                if (baseResourcesEnum != null)
                                {
                                    foreach (var ritem in baseResourcesEnum)
                                    {
                                        if (ritem == null) continue;
                                        try
                                        {
                                            var rt = ritem.GetType();
                                            var nameObj = rt.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ritem);
                                            string resName = null;
                                            if (nameObj != null)
                                            {
                                                var itemData = nameObj.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(nameObj);
                                                var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                                resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                            }
                                            var amountObj = rt.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ritem);
                                            int amt = 0;
                                            if (amountObj != null) { try { amt = Convert.ToInt32(amountObj); } catch { amt = 0; } }
                                            if (!string.IsNullOrEmpty(resName))
                                            {
                                                if (baseResources.ContainsKey(resName)) baseResources[resName] += amt;
                                                else baseResources[resName] = amt;
                                            }
                                        }
                                        catch { }
                                    }
                                }

                                var guiMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                                foreach (var g in guiReqs) guiMap[g.name] = guiMap.ContainsKey(g.name) ? guiMap[g.name] + g.amount : g.amount;

                                if (guiMap.Count != baseResources.Count) guiIndicatesUpgrade = true;
                                else
                                {
                                    foreach (var kv in guiMap)
                                    {
                                        if (!baseResources.TryGetValue(kv.Key, out var baseAmt) || baseAmt != kv.Value)
                                        {
                                            guiIndicatesUpgrade = true;
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { /* ignore compare errors */ }

                            if (guiIndicatesUpgrade)
                            {
                                // Normalize GUI totals to per-level amounts when safe (even division)
                                var normalized = new List<(string name, int amount)>();
                                int levelsToUpgrade = 1;
                                try
                                {
                                    // Decide which recipe to use to determine the final quality:
                                    // Prefer explicit GUI-wrapped upgrade recipe, then best ObjectDB candidate, then selectedRecipe.
                                    Recipe recipeForQuality = null;
                                    if (_upgradeGuiRecipe != null)
                                    {
                                        recipeForQuality = _upgradeGuiRecipe;
                                    }
                                    else
                                    {
                                        try { recipeForQuality = FindBestUpgradeRecipeCandidate(selectedRecipe) ?? selectedRecipe; }
                                        catch { recipeForQuality = selectedRecipe; }
                                    }

                                    // Log which recipe we used for quality computation (helps diagnosing cases like yours)
                                    var rInfoDbg = RecipeInfo(recipeForQuality);
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: using recipeForQuality={rInfoDbg} to compute levelsToUpgrade");

                                    if (_upgradeTargetItem != null)
                                    {
                                        var finalQuality = recipeForQuality.m_item?.m_itemData?.m_quality ?? 0;
                                        var targetQuality = _upgradeTargetItem.m_quality;
                                        levelsToUpgrade = Math.Max(1, finalQuality - targetQuality);
                                    }
                                    else
                                    {
                                        var craftUpgradeFieldLocal = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                                        if (craftUpgradeFieldLocal != null)
                                        {
                                            var cvObj = craftUpgradeFieldLocal.GetValue(__instance);
                                            if (cvObj is int v && v > 0) levelsToUpgrade = v;
                                        }
                                    }
                                }
                                catch { levelsToUpgrade = 1; }

                                Recipe dbCandidate = null;
                                try { dbCandidate = FindBestUpgradeRecipeCandidate(selectedRecipe); } catch { dbCandidate = null; }

                                foreach (var g in guiReqs)
                                {
                                    int amt = g.amount;
                                    int perLevel = amt;

                                    // If GUI amount likely represents totals for N levels, divide when evenly divisible by levelsToUpgrade
                                    if (levelsToUpgrade > 1 && amt > 0 && (amt % levelsToUpgrade) == 0)
                                    {
                                        perLevel = amt / levelsToUpgrade;
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: normalized GUI req {g.name}:{amt} by levelsToUpgrade={levelsToUpgrade} -> {perLevel}");
                                    }
                                    else if (dbCandidate != null)
                                    {
                                        // Try to infer per-level from DB candidate if it matches and divides GUI total
                                        try
                                        {
                                            var resourcesField2 = dbCandidate.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            var candidateResources = resourcesField2?.GetValue(dbCandidate) as System.Collections.IEnumerable;
                                            if (candidateResources != null)
                                            {
                                                foreach (var req in candidateResources)
                                                {
                                                    try
                                                    {
                                                        var et = req.GetType();
                                                        var nameObj = et.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                                        string resName = null;
                                                        if (nameObj != null)
                                                        {
                                                            var itemData = nameObj.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(nameObj);
                                                            var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                                            resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                                        }
                                                        if (string.IsNullOrEmpty(resName)) continue;
                                                        if (string.Equals(resName, g.name, StringComparison.OrdinalIgnoreCase))
                                                        {
                                                            var amountObj2 = et.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                                            int dbAmount = 0;
                                                            if (amountObj2 != null) { try { dbAmount = Convert.ToInt32(amountObj2); } catch { dbAmount = 0; } }
                                                            if (dbAmount > 0 && dbAmount < amt && (amt % dbAmount) == 0)
                                                            {
                                                                perLevel = dbAmount;
                                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: inferred per-level amount for {g.name} from DB candidate: {dbAmount} (gui total={amt})");
                                                            }
                                                            break;
                                                        }
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                        catch { }
                                    }

                                    normalized.Add((g.name, perLevel));
                                }

                                _isUpgradeDetected = true;
                                _upgradeGuiRequirements = normalized;
                                var dbgJoined = string.Join(", ", _upgradeGuiRequirements.Select(x => x.name + ":" + x.amount));
                                UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix-DBG: GUI requirement list indicates UPGRADE -> marking as upgrade and storing GUI requirements: " + dbgJoined);
                            }
                            else
                            {
                                UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix-DBG: GUI reqs found but not an upgrade (ignored).");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix-DBG: exception while checking GUI requirements: {ex}");
                    }
                    // --- end GUI-requirements detection ---

                    // Fast-path explicit craft upgrade multiplier
                    var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (craftUpgradeField != null)
                    {
                        try
                        {
                            object cv = craftUpgradeField.GetValue(__instance);
                            if (cv is int v && v > 1)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: detected explicit m_craftUpgrade={v} - treating as upgrade, skipping suppression.");
                                _savedRecipeForCall = null;
                                _isUpgradeDetected = true;
                                _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance);
                                _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                                _upgradeTargetItem = GetSelectedInventoryItem(__instance);
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: captured _upgradeGuiRecipe={RecipeInfo(_upgradeGuiRecipe)} _upgradeTargetItem={ItemInfo(_upgradeTargetItem)}");
                                return;
                            }
                        }
                        catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix craftUpgrade check exception: {ex}"); }
                    }

                    // Build resource list and snapshot
                    var resourcesField = selectedRecipe.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (resourcesField == null) return;
                    var resourcesEnumerable = resourcesField.GetValue(selectedRecipe) as System.Collections.IEnumerable;
                    if (resourcesEnumerable == null) return;
                    var resourceList = resourcesEnumerable.Cast<object>().ToList();

                    object GetMember(object obj, string name)
                    {
                        if (obj == null) return null;
                        var t = obj.GetType();
                        var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (f != null) return f.GetValue(obj);
                        var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (p != null) return p.GetValue(obj);
                        return null;
                    }
                    object GetNested(object obj, params string[] names)
                    {
                        object cur = obj;
                        foreach (var n in names)
                        {
                            if (cur == null) return null;
                            cur = GetMember(cur, n);
                        }
                        return cur;
                    }
                    int ToInt(object v)
                    {
                        if (v == null) return 0;
                        try { return Convert.ToInt32(v); } catch { return 0; }
                    }

                    // Snapshot existing inventory entries for crafted name
                    try
                    {
                        var craftedName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                        var localPlayer = Player.m_localPlayer;
                        if (!string.IsNullOrEmpty(craftedName) && localPlayer != null)
                        {
                            var inv = localPlayer.GetInventory();
                            if (inv != null)
                            {
                                var existing = new HashSet<ItemDrop.ItemData>();
                                var existingData = new Dictionary<ItemDrop.ItemData, (int quality, int variant)>();
                                foreach (var it in inv.GetAllItems())
                                {
                                    if (it == null || it.m_shared == null) continue;
                                    if (it.m_shared.m_name == craftedName)
                                    {
                                        existing.Add(it);
                                        existingData[it] = (it.m_quality, it.m_variant);
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: snapshot contains {ItemInfo(it)} (recorded q={it.m_quality} v={it.m_variant})");
                                    }
                                }
                                lock (typeof(ChanceCraftPlugin))
                                {
                                    _preCraftSnapshot = existing;
                                    _preCraftSnapshotData = existingData;
                                    _snapshotRecipe = selectedRecipe;
                                    // Populate hash->quality map so the hash-map revert pass can work
                                    try
                                    {
                                        _preCraftSnapshotHashQuality = new Dictionary<int, int>();
                                        foreach (var it2 in existing)
                                        {
                                            if (it2 == null) continue;
                                            _preCraftSnapshotHashQuality[RuntimeHelpers.GetHashCode(it2)] = it2.m_quality;
                                        }
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: populated _preCraftSnapshotHashQuality with {_preCraftSnapshotHashQuality.Count} entries");
                                    }
                                    catch (Exception ex)
                                    {
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: exception populating pre-craft hash-quality map: {ex}");
                                        _preCraftSnapshotHashQuality = null;
                                    }
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: snapshot stored for recipe {RecipeInfo(selectedRecipe)} with {existing.Count} matching items");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix snapshot exception: {ex}"); }

                    // Inventory-based upgrade detection: try to capture the exact selected inventory item first
                    try
                    {
                        var craftedName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                        int craftedQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                        var localPlayer = Player.m_localPlayer;

                        var selectedInventoryItem = GetSelectedInventoryItem(__instance);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: selectedInventoryItem candidate = {ItemInfo(selectedInventoryItem)}");

                        if (selectedInventoryItem != null &&
                            selectedInventoryItem.m_shared != null &&
                            !string.IsNullOrEmpty(craftedName) &&
                            selectedInventoryItem.m_shared.m_name == craftedName &&
                            selectedInventoryItem.m_quality < craftedQuality)
                        {
                            UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: exact selected inventory item detected as upgrade target.");
                            _isUpgradeDetected = true;
                            _upgradeTargetItem = selectedInventoryItem;
                            _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                            _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                            _savedRecipeForCall = null;
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: captured upgrade target {ItemInfo(_upgradeTargetItem)} upgradeGuiRecipe={RecipeInfo(_upgradeGuiRecipe)}");
                            return;
                        }

                        if (!string.IsNullOrEmpty(craftedName) && craftedQuality > 0 && localPlayer != null)
                        {
                            var inv = localPlayer.GetInventory();
                            if (inv != null)
                            {
                                ItemDrop.ItemData foundLower = null;
                                foreach (var it in inv.GetAllItems())
                                {
                                    if (it == null || it.m_shared == null) continue;
                                    if (it.m_shared.m_name == craftedName && it.m_quality < craftedQuality)
                                    {
                                        foundLower = it;
                                        break;
                                    }
                                }
                                if (foundLower != null)
                                {
                                    UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: detected lower-quality item in inventory - treating as upgrade, skipping suppression.");
                                    _isUpgradeDetected = true;
                                    _upgradeTargetItem = foundLower;
                                    _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                                    _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                                    _savedRecipeForCall = null;
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: captured upgrade target {ItemInfo(foundLower)} and upgradeGuiRecipe={RecipeInfo(_upgradeGuiRecipe)}");
                                    return;
                                }
                            }
                        }
                    }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix upgrade detection exception: {ex}"); }

                    // Recipe-consumes-result check
                    try
                    {
                        if (RecipeConsumesResult(selectedRecipe))
                        {
                            UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: recipe consumes result item - treating as upgrade, skipping suppression.");
                            _isUpgradeDetected = true;
                            _upgradeRecipe = _upgradeRecipe ?? selectedRecipe;
                            _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance) ?? selectedRecipe;
                            _savedRecipeForCall = null;
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: captured upgradeGuiRecipe={RecipeInfo(_upgradeGuiRecipe)}");
                            return;
                        }
                    }
                    catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix RecipeConsumesResult exception: {ex}"); }

                    // Capture GUI wrapper recipe if present
                    try
                    {
                        var guiRecipe = GetUpgradeRecipeFromGui(__instance);
                        if (guiRecipe != null)
                        {
                            _upgradeGuiRecipe = guiRecipe;
                            var keyGui = RecipeFingerprint(guiRecipe);
                            lock (_suppressedRecipeKeysLock)
                            {
                                _suppressedRecipeKeys.Add(keyGui);
                            }
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: captured GUI upgrade recipe and recorded its fingerprint: {keyGui} recipe={RecipeInfo(guiRecipe)} (ReferenceEqual to selectedRecipe? {ReferenceEquals(guiRecipe, selectedRecipe)})");

                            if (ReferenceEquals(guiRecipe, selectedRecipe))
                            {
                                try
                                {
                                    var candidate = FindBestUpgradeRecipeCandidate(selectedRecipe);
                                    if (candidate != null)
                                    {
                                        _upgradeRecipe = candidate;
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: stored ObjectDB upgrade candidate {RecipeInfo(candidate)} for later use.");
                                        if (RecipeConsumesResult(candidate))
                                        {
                                            _isUpgradeDetected = true;
                                            UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: ObjectDB candidate consumes result -> marking as upgrade-detected.");
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: FindBestUpgradeRecipeCandidate exception: {ex}");
                                }
                            }
                        }
                        else
                        {
                            UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: GetUpgradeRecipeFromGui returned null -- no separate GUI upgrade recipe detected (will use selectedRecipe instance).");
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: exception attempting to capture GUI upgrade recipe: {ex}");
                    }

                    // Build validReqs (unchanged)
                    var validReqs = new List<object>();
                    foreach (var req in resourceList)
                    {
                        var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                        string rname = nameObj as string;
                        if (string.IsNullOrEmpty(rname)) continue;
                        int ramount = ToInt(GetMember(req, "m_amount"));
                        if (ramount <= 0) continue;
                        validReqs.Add(req);
                    }

                    // Only suppress multi-resource recipes
                    if (validReqs.Count <= 1)
                    {
                        UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: recipe has <=1 valid req -> skipping suppression.");
                        return;
                    }

                    var itemType = selectedRecipe.m_item?.m_itemData?.m_shared?.m_itemType;
                    bool isEligible =
                        itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                        itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                        itemType == ItemDrop.ItemData.ItemType.Bow ||
                        itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                        itemType == ItemDrop.ItemData.ItemType.Shield ||
                        itemType == ItemDrop.ItemData.ItemType.Helmet ||
                        itemType == ItemDrop.ItemData.ItemType.Chest ||
                        itemType == ItemDrop.ItemData.ItemType.Legs ||
                        itemType == ItemDrop.ItemData.ItemType.Ammo;

                    if (!isEligible)
                    {
                        UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: item type not eligible -> skipping suppression.");
                        return;
                    }

                    // Save original resources for restore
                    lock (_savedResources)
                    {
                        if (!_savedResources.ContainsKey(selectedRecipe))
                            _savedResources[selectedRecipe] = resourcesEnumerable;
                    }

                    try
                    {
                        var key = RecipeFingerprint(selectedRecipe);
                        lock (_suppressedRecipeKeysLock)
                        {
                            _suppressedRecipeKeys.Add(key);
                        }
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: recorded suppressed recipe fingerprint: {key}");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix: exception recording suppressed fingerprint: {ex}");
                    }

                    // Suppress live recipe resources
                    Type fieldType = resourcesField.FieldType;
                    object empty = null;
                    if (fieldType.IsArray) empty = Array.CreateInstance(fieldType.GetElementType(), 0);
                    else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
                        empty = Activator.CreateInstance(fieldType);

                    if (empty != null)
                    {
                        resourcesField.SetValue(selectedRecipe, empty);
                        _suppressedThisCall = true;
                        IsDoCraft = true;
                        UnityEngine.Debug.LogWarning("[ChanceCraft] Prefix: suppressed resources for plugin-managed keep-one-on-fail behavior.");
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Prefix exception: {ex}");
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;
                }
            }

            [UsedImplicitly]
            static void Postfix(InventoryGui __instance, Player player)
            {
                DumpInventoryState("Postfix-before-TrySpawnCraftEffect", player);

                try
                {
                    // Restore saved resources if needed (use exact recipe instance)
                    if (_savedRecipeForCall != null && _suppressedThisCall)
                    {
                        lock (_savedResources)
                        {
                            if (_savedResources.TryGetValue(_savedRecipeForCall, out var saved))
                            {
                                var resourcesField = _savedRecipeForCall.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (resourcesField != null)
                                {
                                    resourcesField.SetValue(_savedRecipeForCall, saved);
                                }
                                _savedResources.Remove(_savedRecipeForCall);
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: restored resources for savedRecipeForCall: {RecipeInfo(_savedRecipeForCall)}");
                            }
                        }
                    }

                    // If we didn't suppress, do nothing
                    if (!_suppressedThisCall)
                    {
                        _suppressedThisCall = false;
                        _savedRecipeForCall = null;
                        IsDoCraft = false;
                        return;
                    }

                    // We suppressed: run chance logic for the same recipe instance
                    var recipeForLogic = _savedRecipeForCall;
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;

                    try
                    {
                        // If upgrade detected delegate to TrySpawnCraftEffect (handles both success/failure)
                        if (_isUpgradeDetected || IsUpgradeOperation(__instance, recipeForLogic))
                        {
                            UnityEngine.Debug.LogWarning("[ChanceCraft] Postfix: upgrade detected — delegating to TrySpawnCraftEffect for success/failure handling.");

                            try
                            {
                                if (_upgradeGuiRecipe == null) _upgradeGuiRecipe = GetUpgradeRecipeFromGui(__instance);
                                if (_upgradeTargetItem == null) _upgradeTargetItem = GetSelectedInventoryItem(__instance);

                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: calling TrySpawnCraftEffect for upgrade recipe handling. _upgradeGuiRecipe={RecipeInfo(_upgradeGuiRecipe)}, _upgradeTargetItem={ItemInfo(_upgradeTargetItem)}");

                                // Explicitly indicate this is an upgrade-handling call so TrySpawnCraftEffect treats it as upgrade
                                Recipe resultFromTry = ChanceCraftPlugin.TrySpawnCraftEffect(__instance, recipeForLogic, true);

                                if (resultFromTry != null)
                                {
                                    UnityEngine.Debug.LogWarning("[ChanceCraft] Postfix: TrySpawnCraftEffect indicated a failed non-upgrade craft; proceeding with created-item removal.");
                                    // fall through: continue to standard created-item removal logic below if needed.
                                }
                                else
                                {
                                    UnityEngine.Debug.LogWarning("[ChanceCraft] Postfix: TrySpawnCraftEffect handled upgrade success/failure — cleanup and return.");
                                    // cleanup and return (upgrade handled)
                                    try
                                    {
                                        if (recipeForLogic != null)
                                        {
                                            var key = RecipeFingerprint(recipeForLogic);
                                            lock (_suppressedRecipeKeysLock) { _suppressedRecipeKeys.Remove(key); }
                                            UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: removed suppressed fingerprint {key}");
                                        }
                                        if (_upgradeGuiRecipe != null)
                                        {
                                            try
                                            {
                                                var keyg = RecipeFingerprint(_upgradeGuiRecipe);
                                                lock (_suppressedRecipeKeysLock) { _suppressedRecipeKeys.Remove(keyg); }
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: removed suppressed GUI-upgrade fingerprint {keyg}");
                                            }
                                            catch { }
                                        }
                                    }
                                    catch { }

                                    lock (typeof(ChanceCraftPlugin))
                                    {
                                        _preCraftSnapshot = null;
                                        _preCraftSnapshotData = null;
                                        _snapshotRecipe = null;
                                    }
                                    _upgradeTargetItem = null;
                                    _upgradeRecipe = null;
                                    _upgradeGuiRecipe = null;
                                    _isUpgradeDetected = false;
                                    return;
                                }
                            }
                            catch (Exception ex)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: Exception while delegating upgrade handling to TrySpawnCraftEffect: {ex}");
                            }
                        }

                        // Non-upgrade suppressed craft: run plugin chance-logic (this may return a Recipe to indicate a failed craft)
                        Recipe recept = ChanceCraftPlugin.TrySpawnCraftEffect(__instance, recipeForLogic, false);

                        if (player != null && recept != null)
                        {
                            try
                            {
                                UnityEngine.Debug.LogWarning("[ChanceCraft] Postfix: failed craft detected for multi-resource recipe — removing newly-created crafted items only.");

                                // Use snapshot if it matches the recipe; otherwise null
                                HashSet<ItemDrop.ItemData> beforeSet = null;
                                lock (typeof(ChanceCraftPlugin))
                                {
                                    if (_snapshotRecipe != null && _snapshotRecipe == recept)
                                    {
                                        beforeSet = _preCraftSnapshot;
                                    }
                                    _preCraftSnapshot = null;
                                    _preCraftSnapshotData = null;
                                    _snapshotRecipe = null;
                                }

                                // Remove up to recept.m_amount of crafted items, preferring items not in beforeSet.
                                int toRemoveCount = recept.m_amount > 0 ? recept.m_amount : 1;
                                var invItems = player.GetInventory()?.GetAllItems();
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: attempting to remove up to {toRemoveCount} of result type {recept.m_item?.m_itemData?.m_shared?.m_name}; snapshotCount={(beforeSet == null ? 0 : beforeSet.Count)}");
                                if (invItems != null)
                                {
                                    int removedTotal = 0;

                                    // First pass: remove items that match recipe and are NOT in beforeSet (newly created instances)
                                    for (int i = invItems.Count - 1; i >= 0 && toRemoveCount > 0; i--)
                                    {
                                        var item = invItems[i];
                                        if (item == null || item.m_shared == null) continue;
                                        var craftedName = recept.m_item?.m_itemData?.m_shared?.m_name;
                                        var craftedQuality = recept.m_item?.m_itemData?.m_quality ?? 0;
                                        var craftedVariant = recept.m_item?.m_itemData?.m_variant ?? 0;

                                        if (item.m_shared.m_name == craftedName && item.m_quality == craftedQuality && item.m_variant == craftedVariant)
                                        {
                                            // Never remove the exact upgrade target item (safety)
                                            if (item == _upgradeTargetItem)
                                            {
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: skipping removal of upgrade target item {ItemInfo(item)}");
                                                continue;
                                            }

                                            if (beforeSet == null || !beforeSet.Contains(item))
                                            {
                                                int remove = Math.Min(item.m_stack, toRemoveCount);
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: removing {remove} from newly created item {ItemInfo(item)}");
                                                item.m_stack -= remove;
                                                toRemoveCount -= remove;
                                                removedTotal += remove;
                                                if (item.m_stack <= 0)
                                                {
                                                    try { player.GetInventory().RemoveItem(item); } catch { }
                                                }
                                            }
                                            else
                                            {
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: not removing existing pre-craft item {ItemInfo(item)} (in snapshot)");
                                            }
                                        }
                                    }

                                    // If nothing new was found and we have a snapshot, DO NOT fall back to deleting by name:
                                    if (toRemoveCount > 0)
                                    {
                                        if (beforeSet != null && removedTotal == 0)
                                        {
                                            UnityEngine.Debug.LogWarning("[ChanceCraft] Postfix: no newly-created items found (all matching items were in snapshot) — skipping fallback removal");
                                            // won't remove anything further
                                        }
                                        else
                                        {
                                            // Fallback: remove by matching name/quality/variant (legacy behavior)
                                            for (int i = invItems.Count - 1; i >= 0 && toRemoveCount > 0; i--)
                                            {
                                                var item = invItems[i];
                                                if (item == null || item.m_shared == null) continue;
                                                var craftedName = recept.m_item?.m_itemData?.m_shared?.m_name;
                                                var craftedQuality = recept.m_item?.m_itemData?.m_quality ?? 0;
                                                var craftedVariant = recept.m_item?.m_itemData?.m_variant ?? 0;

                                                if (item.m_shared.m_name == craftedName && item.m_quality == craftedQuality && item.m_variant == craftedVariant)
                                                {
                                                    if (item == _upgradeTargetItem)
                                                    {
                                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix (fallback): skipping upgrade target {ItemInfo(item)}");
                                                        continue;
                                                    }

                                                    int remove = Math.Min(item.m_stack, toRemoveCount);
                                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix (fallback): removing {remove} from {ItemInfo(item)}");
                                                    item.m_stack -= remove;
                                                    toRemoveCount -= remove;
                                                    if (item.m_stack <= 0)
                                                    {
                                                        try { player.GetInventory().RemoveItem(item); } catch { }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: Exception while removing crafted item: {ex}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix: Exception in upgrade-check/ChanceCraft logic: {ex}");
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] Postfix exception: {ex}");
                    _suppressedThisCall = false;
                    _savedRecipeForCall = null;
                    IsDoCraft = false;
                }
            }
        }

        // TrySpawnCraftEffect - UPDATED with stronger GUI refresh (refresh left crafting panel)
        // NOTE: This is the same method as in your file but with added calls to RefreshCraftingPanel
        // and a small coroutine DelayedRefreshCraftingPanel to force a GUI update on the next frame.
        public static Recipe TrySpawnCraftEffect(InventoryGui gui, Recipe forcedRecipe = null, bool isUpgradeCall = false)
        {
            Recipe selectedRecipe = forcedRecipe;

            if (selectedRecipe == null)
            {
                var selectedRecipeField = typeof(InventoryGui).GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (selectedRecipeField != null)
                {
                    object value = selectedRecipeField.GetValue(gui);
                    if (value != null && value.GetType().Name == "RecipeDataPair")
                    {
                        var recipeProp = value.GetType().GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (recipeProp != null)
                            selectedRecipe = recipeProp.GetValue(value) as Recipe;
                    }
                    else
                    {
                        selectedRecipe = value as Recipe;
                    }
                }
            }

            if (selectedRecipe == null || Player.m_localPlayer == null)
                return null;

            UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect called for recipe: {RecipeInfo(selectedRecipe)} (isUpgradeCall={isUpgradeCall})");

            var itemType = selectedRecipe.m_item?.m_itemData?.m_shared?.m_itemType;
            if (itemType != ItemDrop.ItemData.ItemType.OneHandedWeapon &&
                itemType != ItemDrop.ItemData.ItemType.TwoHandedWeapon &&
                itemType != ItemDrop.ItemData.ItemType.Bow &&
                itemType != ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft &&
                itemType != ItemDrop.ItemData.ItemType.Shield &&
                itemType != ItemDrop.ItemData.ItemType.Helmet &&
                itemType != ItemDrop.ItemData.ItemType.Chest &&
                itemType != ItemDrop.ItemData.ItemType.Legs &&
                itemType != ItemDrop.ItemData.ItemType.Ammo)
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] Item type not eligible for TrySpawnCraftEffect.");
                return null;
            }

            var player = Player.m_localPlayer;
            UnityEngine.Debug.LogWarning("[chancecraft] before rand");

            float chance = 0.6f;
            if (itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                itemType == ItemDrop.ItemData.ItemType.Bow ||
                itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft)
            {
                chance = weaponSuccessChance.Value;
            }
            else if (itemType == ItemDrop.ItemData.ItemType.Shield ||
                     itemType == ItemDrop.ItemData.ItemType.Helmet ||
                     itemType == ItemDrop.ItemData.ItemType.Chest ||
                     itemType == ItemDrop.ItemData.ItemType.Legs)
            {
                chance = armorSuccessChance.Value;
            }
            else if (itemType == ItemDrop.ItemData.ItemType.Ammo)
            {
                chance = arrowSuccessChance.Value;
            }

            int qualityLevel = selectedRecipe.m_item?.m_itemData?.m_quality ?? 1;
            float qualityScalePerLevel = 0.05f;
            float qualityFactor = 1f + qualityScalePerLevel * Math.Max(0, qualityLevel - 1);
            chance = Mathf.Clamp01(chance * qualityFactor);

            // draw RNG once and log
            float randVal = UnityEngine.Random.value;
            UnityEngine.Debug.LogWarning($"[chancecraft] final chance = {chance} rand={randVal}");

            bool isUpgradeNow = isUpgradeCall || _isUpgradeDetected || IsUpgradeOperation(gui, selectedRecipe);

            if (randVal <= chance)
            {
                // SUCCESS path (unchanged)
                UnityEngine.Debug.LogWarning("[chancecraft] success");
                var craftingStationField = typeof(InventoryGui).GetField("currentCraftingStation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var craftingStation = craftingStationField?.GetValue(gui);
                if (craftingStation != null)
                {
                    var m_craftItemEffectsField = craftingStation.GetType().GetField("m_craftItemEffects", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var m_craftItemEffects = m_craftItemEffectsField?.GetValue(craftingStation);
                    if (m_craftItemEffects != null)
                    {
                        var createMethod = m_craftItemEffects.GetType().GetMethod("Create", new Type[] { typeof(Vector3), typeof(Quaternion) });
                        if (createMethod != null)
                        {
                            createMethod.Invoke(m_craftItemEffects, new object[] { player.transform.position, Quaternion.identity });
                        }
                    }
                }

                bool suppressedThisOperation = IsDoCraft;
                try
                {
                    var key = RecipeFingerprint(selectedRecipe);
                    lock (_suppressedRecipeKeysLock)
                    {
                        if (_suppressedRecipeKeys.Contains(key))
                        {
                            suppressedThisOperation = true;
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: detected suppressed recipe fingerprint {key} -> treating as suppressed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: exception checking suppressed fingerprint: {ex}");
                }

                try
                {
                    lock (typeof(ChanceCraftPlugin))
                    {
                        if (_snapshotRecipe != null && _snapshotRecipe == selectedRecipe && _preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                        {
                            suppressedThisOperation = true;
                        }
                    }
                }
                catch { /* ignore */ }

                if (isUpgradeNow)
                {
                    try
                    {
                        var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: chosen recipeToUse = {RecipeInfo(recipeToUse)}");
                        if (_upgradeTargetItem == null) _upgradeTargetItem = GetSelectedInventoryItem(gui);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: upgrade success detected - removing resources; target={ItemInfo(_upgradeTargetItem)}");

                        if (ReferenceEquals(recipeToUse, selectedRecipe))
                        {
                            var candidate = FindBestUpgradeRecipeCandidate(selectedRecipe);
                            if (candidate != null) recipeToUse = candidate;
                        }

                        RemoveRequiredResourcesUpgrade(gui, player, recipeToUse, _upgradeTargetItem, true);

                        lock (typeof(ChanceCraftPlugin))
                        {
                            _preCraftSnapshot = null;
                            _preCraftSnapshotData = null;
                            _snapshotRecipe = null;
                            _upgradeTargetItemIndex = -1;
                            _preCraftSnapshotHashQuality = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: Exception while removing upgrade resources after success: {ex}");
                    }

                    return null;
                }
                else
                {
                    if (suppressedThisOperation)
                    {
                        try { RemoveRequiredResources(gui, player, selectedRecipe, true, false); } catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: Exception while removing craft resources after success: {ex}"); }
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning("[ChanceCraft] TrySpawnCraftEffect: craft success detected, skipping plugin resource removal so the game handles it.");
                    }
                    return null;
                }
            }
            else
            {
                // FAILURE branch
                UnityEngine.Debug.LogWarning("[chancecraft] failed");

                if (isUpgradeCall || IsUpgradeOperation(gui, selectedRecipe) || _isUpgradeDetected)
                {
                    // diagnostic logging
                    try
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: _upgradeTargetItem captured hash={(_upgradeTargetItem != null ? RuntimeHelpers.GetHashCode(_upgradeTargetItem).ToString("X8") : "<null>")}, ItemInfo={ItemInfo(_upgradeTargetItem)}");
                        if (_preCraftSnapshotData != null)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: preCraftSnapshotData contains upgradeTargetItem? {_preCraftSnapshotData.ContainsKey(_upgradeTargetItem)}");
                        }
                    }
                    catch { }

                    // Run revert attempts unconditionally for upgrade failures (as before)
                    try
                    {
                        UnityEngine.Debug.LogWarning("[ChanceCraft] TrySpawnCraftEffect: attempting revert for failed upgrade (unconditional attempt).");

                        bool didRevertAny = false;
                        lock (typeof(ChanceCraftPlugin))
                        {
                            // 1) Snapshot-ref-based revert (authoritative)
                            if (_preCraftSnapshotData != null)
                            {
                                var invItems = Player.m_localPlayer?.GetInventory()?.GetAllItems();
                                var preRefs = _preCraftSnapshot;
                                foreach (var kv in _preCraftSnapshotData)
                                {
                                    var originalRef = kv.Key;
                                    var pre = kv.Value;
                                    try
                                    {
                                        if (originalRef != null && invItems != null && invItems.Contains(originalRef))
                                        {
                                            if (originalRef.m_quality != pre.quality || originalRef.m_variant != pre.variant)
                                            {
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: reverting original item {ItemInfo(originalRef)} -> q={pre.quality} v={pre.variant}");
                                                originalRef.m_quality = pre.quality;
                                                originalRef.m_variant = pre.variant;
                                                didRevertAny = true;
                                            }
                                            continue;
                                        }

                                        // Replacement detection by heuristics
                                        string expectedName = null;
                                        try { expectedName = originalRef?.m_shared?.m_name ?? selectedRecipe.m_item?.m_itemData?.m_shared?.m_name; } catch { expectedName = null; }
                                        if (string.IsNullOrEmpty(expectedName) || invItems == null) continue;

                                        foreach (var cur in invItems)
                                        {
                                            if (cur == null || cur.m_shared == null) continue;
                                            if (!string.Equals(cur.m_shared.m_name, expectedName, StringComparison.OrdinalIgnoreCase)) continue;
                                            if (cur.m_quality <= pre.quality) continue;
                                            if (preRefs != null && preRefs.Contains(cur)) continue;

                                            UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: reverting replacement item {ItemInfo(cur)} -> q={pre.quality} v={pre.variant}");
                                            cur.m_quality = pre.quality;
                                            cur.m_variant = pre.variant;
                                            didRevertAny = true;
                                            break;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: exception while processing pre-snapshot kv for revert: {ex}");
                                    }
                                }
                            }

                            // 2) Conservative fallback by name/quality if snapshot revert didn't find anything
                            if (!didRevertAny)
                            {
                                try
                                {
                                    string resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                                    int expectedPreQuality = Math.Max(0, (selectedRecipe.m_item?.m_itemData?.m_quality ?? 0) - 1);

                                    // Prefer snapshot pre-quality if available
                                    try
                                    {
                                        if (_preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                                        {
                                            var preQs = _preCraftSnapshotData.Values.Select(v => v.quality).ToList();
                                            if (preQs.Count > 0) expectedPreQuality = Math.Max(0, preQs.Max());
                                        }
                                    }
                                    catch { /* ignore */ }

                                    if (!string.IsNullOrEmpty(resultName) && Player.m_localPlayer != null)
                                    {
                                        var invItems2 = Player.m_localPlayer.GetInventory()?.GetAllItems();
                                        if (invItems2 != null)
                                        {
                                            foreach (var it in invItems2)
                                            {
                                                try
                                                {
                                                    if (it == null || it.m_shared == null) continue;
                                                    if (!string.Equals(it.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase)) continue;
                                                    if (it.m_quality <= expectedPreQuality) continue;
                                                    if (_preCraftSnapshot != null && _preCraftSnapshot.Contains(it)) continue;

                                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: fallback revert: changing {ItemInfo(it)} -> q={expectedPreQuality}");
                                                    it.m_quality = expectedPreQuality;
                                                    try
                                                    {
                                                        if (_preCraftSnapshotData != null)
                                                        {
                                                            var kv = _preCraftSnapshotData.FirstOrDefault(p => p.Key != null && p.Key.m_shared != null && string.Equals(p.Key.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase));
                                                            if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int quality, int variant)>)))
                                                                it.m_variant = kv.Value.variant;
                                                        }
                                                    }
                                                    catch { }
                                                    didRevertAny = true;
                                                }
                                                catch (Exception ex)
                                                {
                                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: fallback revert exception for item: {ex}");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: fallback revert exception: {ex}");
                                }
                            }

                            // 3) Index-based authoritative fallback (if captured)
                            if (!didRevertAny && _upgradeTargetItemIndex >= 0)
                            {
                                try
                                {
                                    var inv = Player.m_localPlayer?.GetInventory();
                                    var all = inv?.GetAllItems();
                                    if (all != null && _upgradeTargetItemIndex >= 0 && _upgradeTargetItemIndex < all.Count)
                                    {
                                        var candidate = all[_upgradeTargetItemIndex];
                                        if (candidate != null && candidate.m_shared != null)
                                        {
                                            string resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                                            int finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                            int expectedPreQuality = Math.Max(0, finalQuality - 1);

                                            try
                                            {
                                                if (_preCraftSnapshotData != null)
                                                {
                                                    var kv = _preCraftSnapshotData.FirstOrDefault(p => p.Key != null && p.Key.m_shared != null && string.Equals(p.Key.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase));
                                                    if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int quality, int variant)>)))
                                                        expectedPreQuality = kv.Value.quality;
                                                }
                                            }
                                            catch { }

                                            if (string.Equals(candidate.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase) && candidate.m_quality > expectedPreQuality)
                                            {
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: index-fallback revert at idx {_upgradeTargetItemIndex}: {ItemInfo(candidate)} -> q={expectedPreQuality}");
                                                candidate.m_quality = expectedPreQuality;
                                                didRevertAny = true;
                                            }
                                            else
                                            {
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: index-fallback candidate at idx {_upgradeTargetItemIndex} was not an upgraded match: {ItemInfo(candidate)}");
                                            }
                                        }
                                    }
                                    else
                                    {
                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: index-fallback index {_upgradeTargetItemIndex} invalid for current inventory count {(all == null ? 0 : all.Count)}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: index-fallback revert exception: {ex}");
                                }
                            }

                            // 4) HASH-MAP-BASED detection & revert
                            if (!didRevertAny && _preCraftSnapshotHashQuality != null && _preCraftSnapshotHashQuality.Count > 0)
                            {
                                try
                                {
                                    string resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                                    int finalQuality = selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                    int expectedPreQuality = Math.Max(0, finalQuality - 1);

                                    var invItems3 = Player.m_localPlayer?.GetInventory()?.GetAllItems();
                                    if (invItems3 != null)
                                    {
                                        foreach (var it in invItems3)
                                        {
                                            try
                                            {
                                                if (it == null || it.m_shared == null) continue;
                                                int h = RuntimeHelpers.GetHashCode(it);

                                                if (_preCraftSnapshotHashQuality.TryGetValue(h, out int prevQ))
                                                {
                                                    if (it.m_quality > prevQ)
                                                    {
                                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: hash-map revert: item {ItemInfo(it)} (hash={h:X8}) prevQ={prevQ} -> curQ={it.m_quality}. Reverting to {prevQ}");
                                                        it.m_quality = prevQ;
                                                        try
                                                        {
                                                            if (_preCraftSnapshotData != null)
                                                            {
                                                                var kv = _preCraftSnapshotData.FirstOrDefault(p => RuntimeHelpers.GetHashCode(p.Key) == h);
                                                                if (!kv.Equals(default(KeyValuePair<ItemDrop.ItemData, (int quality, int variant)>)))
                                                                    it.m_variant = kv.Value.variant;
                                                            }
                                                        }
                                                        catch { }
                                                        didRevertAny = true;
                                                    }
                                                }
                                                else
                                                {
                                                    if (!string.IsNullOrEmpty(resultName) &&
                                                        string.Equals(it.m_shared.m_name, resultName, StringComparison.OrdinalIgnoreCase) &&
                                                        it.m_quality > expectedPreQuality)
                                                    {
                                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: hash-map new-instance revert: item {ItemInfo(it)} (hash={h:X8}) appears new+upgraded -> reverting to q={expectedPreQuality}");
                                                        it.m_quality = expectedPreQuality;
                                                        didRevertAny = true;
                                                    }
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: hash-map revert exception for item: {ex}");
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: hash-map revert top-level exception: {ex}");
                                }
                            }

                            UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: revert attempts finished, didRevertAny={didRevertAny}");

                            // clear state
                            _preCraftSnapshot = null;
                            _preCraftSnapshotData = null;
                            _snapshotRecipe = null;
                            _upgradeTargetItemIndex = -1;
                            _preCraftSnapshotHashQuality = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: exception while reverting in-place upgrade: {ex}");
                    }

                    // Now remove the upgrade resources (preserve target item since we've reverted it)
                    try
                    {
                        var recipeToUse = _upgradeGuiRecipe ?? _upgradeRecipe ?? GetUpgradeRecipeFromGui(gui) ?? selectedRecipe;
                        if (ReferenceEquals(recipeToUse, selectedRecipe))
                        {
                            var candidate = FindBestUpgradeRecipeCandidate(selectedRecipe);
                            if (candidate != null) recipeToUse = candidate;
                        }

                        var upgradeTarget = _upgradeTargetItem ?? GetSelectedInventoryItem(gui);

                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: removing upgrade resources using {RecipeInfo(recipeToUse)}; target={ItemInfo(upgradeTarget)}");
                        RemoveRequiredResourcesUpgrade(gui, Player.m_localPlayer, recipeToUse, upgradeTarget, false);

                        // After resources removed, force a left-panel refresh as well (some UI shows "upgraded" until panel updates)
                        //                        try
                        //                        {
                        //                            try { RefreshCraftingPanel(gui); } catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: RefreshCraftingPanel(after-Remove) exception: {ex}"); }
                        //                            try { gui?.StartCoroutine(DelayedRefreshCraftingPanel(gui, 1)); } catch { /* best-effort */ }
                        //                        }
                        //                        catch { }

                        //                        Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=red>Upgrade failed — materials consumed, item preserved.</color>");

                        // Option A: simple, uses the helper that finds the panel and schedules refresh

                        //                        ChanceCraftUIRefreshUsage.RefreshCraftingUiAfterChange();

                        // Option B: direct call if you already have the panel GameObject reference
                        // GameObject craftingPanel = /* your cached panel root or find by name */;
                        // UIRemoteRefresher.Instance.RefreshNextFrame(craftingPanel);

                        //                        Debug.Log("[ChanceCraft] Postfix: scheduled UI refresher after failed upgrade (didRevertAny)");

                        Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=red>Upgrade failed!</color>");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: exception while removing resources after failure: {ex}");
                    }

                    return null;
                }

                // Non-upgrade failure handling (unchanged)
                bool gameAlreadyHandledNormal = false;
                try
                {
                    lock (typeof(ChanceCraftPlugin))
                    {
                        if (_snapshotRecipe != null && _snapshotRecipe == selectedRecipe && _preCraftSnapshotData != null && _preCraftSnapshotData.Count > 0)
                        {
                            foreach (var kv in _preCraftSnapshotData)
                            {
                                var item = kv.Key;
                                var pre = kv.Value;
                                if (item == null || item.m_shared == null) continue;
                                int currentQuality = item.m_quality;
                                int currentVariant = item.m_variant;
                                if (currentQuality > pre.quality && currentVariant == pre.variant)
                                {
                                    gameAlreadyHandledNormal = true;
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: detected pre-snapshot item upgraded in-place: {ItemInfo(item)} -> treating as success, skipping plugin failure handling.");
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TrySpawnCraftEffect: exception while checking snapshot for in-place upgrade: {ex}");
                }

                if (gameAlreadyHandledNormal)
                {
                    lock (typeof(ChanceCraftPlugin))
                    {
                        _preCraftSnapshot = null;
                        _preCraftSnapshotData = null;
                        _snapshotRecipe = null;
                        _upgradeTargetItemIndex = -1;
                        _preCraftSnapshotHashQuality = null;
                    }
                    return null;
                }

                // Normal failed craft -> remove resources (keep-one)
                RemoveRequiredResources(gui, player, selectedRecipe, false, false);
                player.Message(MessageHud.MessageType.Center, "<color=red>Crafting failed!</color>");
                return selectedRecipe;
            }
        }

        // --- UI helpers to force left-panel refresh ---
        // best-effort synchronous refresh of InventoryGui crafting/recipe UI
        private static void RefreshCraftingPanel(InventoryGui gui)
        {
            if (gui == null) return;
            var t = gui.GetType();

            // Try a list of likely methods that update the left panel / recipe list
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
                        try { m.Invoke(gui, null); UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshCraftingPanel: invoked {name}()"); } catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshCraftingPanel: {name}() threw: {ex}"); }
                    }
                }
                catch { /* ignore per-method */ }
            }

            // Force a selected-recipe toggle to make the GUI re-bind its display state
            try
            {
                var selField = t.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (selField != null)
                {
                    var cur = selField.GetValue(gui);
                    try { selField.SetValue(gui, null); } catch { }
                    try { selField.SetValue(gui, cur); } catch { }
                    UnityEngine.Debug.LogWarning("[ChanceCraft] RefreshCraftingPanel: toggled m_selectedRecipe to force rebind");
                }
            }
            catch { /* ignore */ }

            // Try to refresh player-inventory UI component if present
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
                                try { rm.Invoke(invObj, null); UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshCraftingPanel: invoked player-inventory {name}()"); } catch { }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { /* ignore */ }
        }

        // coroutine-based delayed refresh — call this to schedule a GUI refresh on the next frame
        private static IEnumerator DelayedRefreshCraftingPanel(InventoryGui gui, int delayFrames = 1)
        {
            if (gui == null)
                yield break;

            for (int i = 0; i < Math.Max(1, delayFrames); i++)
                yield return null; // wait frames

            try { RefreshCraftingPanel(gui); } catch (Exception ex) { UnityEngine.Debug.LogWarning($"[ChanceCraft] DelayedRefreshCraftingPanel exception: {ex}"); }
        }

        // RemoveRequiredResources: compute amounts; per-level only if upgrade context
        public static void RemoveRequiredResources(InventoryGui gui, Player player, Recipe selectedRecipe, Boolean crafted, bool skipRemovingResultResource = false)
        {
            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources called recipe={RecipeInfo(selectedRecipe)} crafted={crafted} skipResult={skipRemovingResultResource}");
            if (player == null || selectedRecipe == null) return;
            var inventory = player.GetInventory();
            if (inventory == null) return;

            // Preserve craft upgrade multiplier if present
            var craftUpgradeField = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
            int craftUpgrade = 1;
            if (craftUpgradeField != null)
            {
                try
                {
                    object value = craftUpgradeField.GetValue(gui);
                    if (value is int q && q > 1)
                        craftUpgrade = q;
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: craftUpgrade={craftUpgrade}");
                }
                catch { /* ignore */ }
            }

            object GetMember(object obj, string name)
            {
                if (obj == null) return null;
                var t = obj.GetType();
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(obj);
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p.GetValue(obj);
                return null;
            }

            object GetNested(object obj, params string[] names)
            {
                object cur = obj;
                foreach (var n in names)
                {
                    if (cur == null) return null;
                    cur = GetMember(cur, n);
                }
                return cur;
            }

            int ToInt(object v)
            {
                if (v == null) return 0;
                try { return Convert.ToInt32(v); } catch { return 0; }
            }

            string resultName = null;
            try
            {
                resultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: resultName='{resultName}'");
            }
            catch { resultName = null; }

            var resourcesObj = GetMember(selectedRecipe, "m_resources");
            var resources = resourcesObj as System.Collections.IEnumerable;
            if (resources == null)
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResources: no resources found on recipe");
                return;
            }

            var resourceList = resources.Cast<object>().ToList();

            int RemoveAmountFromInventoryLocal(string resourceName, int amount)
            {
                if (string.IsNullOrEmpty(resourceName) || amount <= 0) return 0;

                int remaining = amount;
                var items = inventory.GetAllItems();

                void TryRemove(Func<ItemDrop.ItemData, bool> predicate)
                {
                    if (remaining <= 0) return;
                    for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
                    {
                        var it = items[i];
                        if (it == null || it.m_shared == null) continue;
                        if (!predicate(it)) continue;
                        int toRemove = Math.Min(it.m_stack, remaining);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventory: removing {toRemove} from {ItemInfo(it)} for resource '{resourceName}'");
                        it.m_stack -= toRemove;
                        remaining -= toRemove;
                        if (it.m_stack <= 0)
                        {
                            try { inventory.RemoveItem(it); } catch { /* best-effort */ }
                        }
                    }
                }

                try
                {
                    TryRemove(it => it.m_shared.m_name == resourceName);
                    TryRemove(it => string.Equals(it.m_shared.m_name, resourceName, StringComparison.OrdinalIgnoreCase));
                    TryRemove(it => it.m_shared.m_name != null && resourceName != null &&
                                     it.m_shared.m_name.IndexOf(resourceName, StringComparison.OrdinalIgnoreCase) >= 0);
                    TryRemove(it => it.m_shared.m_name != null && resourceName != null &&
                                     resourceName.IndexOf(it.m_shared.m_name, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventory: exception during attempts: {ex}");
                }

                if (remaining > 0)
                {
                    try
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventory: fallback RemoveItem by name '{resourceName}' remaining={remaining}");
                        inventory.RemoveItem(resourceName, remaining);
                        remaining = 0;
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveAmountFromInventory: RemoveItem exception: {ex}");
                    }
                }

                return amount - remaining;
            }

            var validReqs = new List<(object req, string name, int amount)>();
            foreach (var req in resourceList)
            {
                var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                if (shared == null) continue;
                var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                string resourceName = nameObj as string;
                if (string.IsNullOrEmpty(resourceName)) continue;

                // compute amount: prefer recipe m_amount for NORMAL crafting; only use perLevel when upgrade context
                int baseAmount = ToInt(GetMember(req, "m_amount"));
                int perLevel = ToInt(GetMember(req, "m_amountPerLevel"));

                bool isUpgradeNow = _isUpgradeDetected || IsUpgradeOperation(gui, selectedRecipe);

                int finalAmount;
                if (isUpgradeNow && perLevel > 0)
                {
                    int craftUpgradeVal = 1;
                    if (craftUpgradeField != null)
                    {
                        try
                        {
                            var cvObj = craftUpgradeField.GetValue(gui);
                            if (cvObj is int v && v > 0) craftUpgradeVal = v;
                        }
                        catch { craftUpgradeVal = 1; }
                    }
                    finalAmount = perLevel * (craftUpgradeVal > 1 ? craftUpgradeVal : 1);
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: using per-level amount for '{resourceName}' -> perLevel={perLevel} craftUpgrade={craftUpgradeVal} final={finalAmount}");
                }
                else
                {
                    finalAmount = baseAmount;
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: using base m_amount for '{resourceName}' -> {finalAmount}");
                }

                if (finalAmount <= 0) continue;
                validReqs.Add((req, resourceName, finalAmount));
            }

            List<(object req, string name, int amount)> validReqsFiltered = validReqs;
            if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName))
            {
                validReqsFiltered = validReqs.Where(v => !string.Equals(v.name, resultName, StringComparison.OrdinalIgnoreCase)).ToList();
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: validReqsFiltered count={validReqsFiltered.Count}");
            }

            // SINGLE-RESOURCE CASE
            if (validReqs.Count == 1 && resourceList.Count <= 1)
            {
                var single = validReqs[0];
                if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) && string.Equals(single.name, resultName, StringComparison.OrdinalIgnoreCase))
                {
                    UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResources: single-resource recipe is upgrade-result — skipping resource removal of result item.");
                    return;
                }
                try
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: single-resource removal '{single.name}' amount={single.amount}");
                    RemoveAmountFromInventoryLocal(single.name, single.amount);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources single-resource removal failed: {ex}");
                }
                return;
            }

            // If crafting failed: remove all required resources EXCEPT one random resource.
            if (!crafted)
            {
                if (validReqs.Count == 0) return;
                if (validReqsFiltered.Count == 0) return;

                int keepIndex = UnityEngine.Random.Range(0, validReqsFiltered.Count);
                var keepTuple = validReqsFiltered[keepIndex];
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: keeping resource '{keepTuple.name}' amount={keepTuple.amount}");

                bool skippedKeep = false;
                foreach (var req in resourceList)
                {
                    var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                    if (shared == null) continue;

                    var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                    string resourceName = nameObj as string;
                    if (string.IsNullOrEmpty(resourceName)) continue;

                    if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) &&
                        string.Equals(resourceName, resultName, StringComparison.OrdinalIgnoreCase))
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: preserving result-resource '{resourceName}'");
                        continue;
                    }

                    var match = validReqs.FirstOrDefault(v => string.Equals(v.name, resourceName, StringComparison.OrdinalIgnoreCase));
                    if (match.name == null) continue;
                    int amount = match.amount;

                    if (!skippedKeep && resourceName == keepTuple.name && amount == keepTuple.amount)
                    {
                        skippedKeep = true;
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: keep-one -> skipping '{resourceName}' amount={amount}");
                        continue;
                    }

                    try
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: removing '{resourceName}' amount={amount}");
                        RemoveAmountFromInventoryLocal(resourceName, amount);
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources removal failed: {ex}");
                    }
                }

                return;
            }

            // crafted == true: remove all required resources (plugin-managed)
            foreach (var req in resourceList)
            {
                var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                if (shared == null) continue;

                var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                string resourceName = nameObj as string;
                if (string.IsNullOrEmpty(resourceName)) continue;

                if (skipRemovingResultResource && !string.IsNullOrEmpty(resultName) &&
                    string.Equals(resourceName, resultName, StringComparison.OrdinalIgnoreCase))
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: skipping removal of recipe-result resource '{resourceName}' on crafted=true");
                    continue;
                }

                var match = validReqs.FirstOrDefault(v => string.Equals(v.name, resourceName, StringComparison.OrdinalIgnoreCase));
                if (match.name == null) continue;
                int amount = match.amount;

                if (amount <= 0) continue;

                try
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources: crafted removal of '{resourceName}' amount={amount}");
                    RemoveAmountFromInventoryLocal(resourceName, amount);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResources removal failed: {ex}");
                }
            }
        }

        private static void DumpInventoryState(string tag, Player player, int max = 64)
        {
            try
            {
                if (player == null)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] {tag}: player is null");
                    return;
                }
                var inv = player.GetInventory();
                if (inv == null)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] {tag}: player inventory is null");
                    return;
                }

                var items = inv.GetAllItems();
                UnityEngine.Debug.LogWarning($"[ChanceCraft] {tag}: inventory contains {items?.Count ?? 0} items (showing up to {max}):");
                int i = 0;
                foreach (var it in items)
                {
                    if (i++ >= max) break;
                    if (it == null || it.m_shared == null)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] {tag}: item[{i - 1}] = <null>");
                        continue;
                    }
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] {tag}: item[{i - 1}] hash={it.GetHashCode():X8} name='{it.m_shared.m_name}' q={it.m_quality} v={it.m_variant} stack={it.m_stack}");
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] DumpInventoryState('{tag}') exception: {ex}");
            }
        }

        // Helper: best-effort InventoryGui refresh to make reverted changes visible immediately in the UI.
        // Tries a list of likely method names (private/public) and invokes any that exist.
        private static void RefreshInventoryGui(InventoryGui gui)
        {
            if (gui == null) return;

            try
            {
                var t = gui.GetType();
                var candidates = new[]
                {
            "UpdateCraftingPanel",
            "UpdateRecipeList",
            "UpdateAvailableRecipes",
            "UpdateCrafting",
            "Refresh",
            "RefreshList",
            "UpdateAvailableCrafting",
            "UpdateRecipes",
            "UpdateInventory",
            "UpdateSelectedItem",
            "OnInventoryChanged",
            "RefreshInventory",
            "UpdateIcons",
            "Setup",
            "OnOpen",
            "OnShow"
        };

                foreach (var name in candidates)
                {
                    try
                    {
                        var m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (m != null && m.GetParameters().Length == 0)
                        {
                            try
                            {
                                m.Invoke(gui, null);
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshInventoryGui: invoked {name}()");
                            }
                            catch (Exception ex)
                            {
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshInventoryGui: failed invoking {name}(): {ex}");
                            }
                        }
                    }
                    catch { /* ignore per-candidate */ }
                }

                // Also try to refresh player inventory UI component(s) if accessible
                try
                {
                    var inventoryField = t.GetField("m_playerInventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                      ?? t.GetField("m_inventory", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var inventoryObj = inventoryField?.GetValue(gui);
                    if (inventoryObj != null)
                    {
                        var invType = inventoryObj.GetType();
                        var refreshMethods = new[] { "Refresh", "UpdateIfNeeded", "Update", "OnChanged" };
                        foreach (var rm in refreshMethods)
                        {
                            var rmMethod = invType.GetMethod(rm, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (rmMethod != null && rmMethod.GetParameters().Length == 0)
                            {
                                try
                                {
                                    rmMethod.Invoke(inventoryObj, null);
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshInventoryGui: invoked player-inventory {rm}()");
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { /* ignore inventory refresh errors */ }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RefreshInventoryGui exception: {ex}");
            }
        }

        // RemoveRequiredResourcesUpgrade: prefer GUI-provided normalized requirements; fallback to recipeToUse resources (guard per-level use)
        public static void RemoveRequiredResourcesUpgrade(InventoryGui gui, Player player, Recipe selectedRecipe, ItemDrop.ItemData upgradeTargetItem, bool crafted)
        {
            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade ENTRY: incoming selectedRecipe = {RecipeInfo(selectedRecipe)} (hash={selectedRecipe?.GetHashCode():X8}), upgradeTargetItem={ItemInfo(upgradeTargetItem)}, crafted={crafted}");
            if (player == null || selectedRecipe == null) return;
            var inventory = player.GetInventory();
            if (inventory == null) return;

            // If we captured GUI requirements earlier in Prefix, prefer them (already normalized), but re-normalize defensively.
            if (_upgradeGuiRequirements != null && _upgradeGuiRequirements.Count > 0)
            {
                // Make a copy and clear the global so future calls won't reuse stale values
                var guiReqsLocal = _upgradeGuiRequirements.ToList();
                _upgradeGuiRequirements = null;

                // Determine levelsToUpgrade similar to other code paths
                int levelsToUpgrade = 1;
                try
                {
                    if (upgradeTargetItem != null)
                    {
                        var recipeForQuality = _upgradeGuiRecipe ?? _upgradeRecipe ?? selectedRecipe;
                        int finalQuality = recipeForQuality?.m_item?.m_itemData?.m_quality ?? 0;
                        levelsToUpgrade = Math.Max(1, finalQuality - upgradeTargetItem.m_quality);
                    }
                    else
                    {
                        var craftUpgradeFieldLocal = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (craftUpgradeFieldLocal != null)
                        {
                            try
                            {
                                var cvObj = craftUpgradeFieldLocal.GetValue(gui);
                                if (cvObj is int cv && cv > 0)
                                {
                                    int selQuality = selectedRecipe?.m_item?.m_itemData?.m_quality ?? 0;
                                    if (selQuality > 0 && cv > selQuality)
                                        levelsToUpgrade = Math.Max(1, cv - selQuality);
                                    else
                                        levelsToUpgrade = Math.Max(1, cv);
                                }
                            }
                            catch { levelsToUpgrade = 1; }
                        }
                    }
                }
                catch { levelsToUpgrade = 1; }

                // Try to find a DB candidate recipe to act as an authoritative hint (per-level amounts)
                Recipe dbCandidate = null;
                try
                {
                    if (upgradeTargetItem != null && ObjectDB.instance != null)
                    {
                        string desiredResultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                        int desiredQuality = upgradeTargetItem.m_quality + 1;
                        foreach (var r in ObjectDB.instance.m_recipes)
                        {
                            try
                            {
                                if (r == null || r.m_item == null) continue;
                                var rn = r.m_item.m_itemData?.m_shared?.m_name;
                                if (!string.Equals(rn, desiredResultName, StringComparison.OrdinalIgnoreCase)) continue;
                                int q = r.m_item.m_itemData?.m_quality ?? 0;
                                if (q == desiredQuality)
                                {
                                    dbCandidate = r;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    if (dbCandidate == null)
                    {
                        dbCandidate = FindBestUpgradeRecipeCandidate(selectedRecipe);
                    }
                }
                catch { dbCandidate = null; }

                // Normalize each stored GUI entry defensively:
                var normalized = new List<(string name, int amount)>();
                foreach (var rr in guiReqsLocal)
                {
                    try
                    {
                        string name = rr.name;
                        int guiAmt = rr.amount;
                        int perLevel = guiAmt;

                        // If the GUI total looks like a multi-level total, divide when evenly divisible
                        if (levelsToUpgrade > 1 && guiAmt > 0 && (guiAmt % levelsToUpgrade) == 0)
                        {
                            perLevel = guiAmt / levelsToUpgrade;
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (fast-path): normalized stored GUI req {name}:{guiAmt} by levelsToUpgrade={levelsToUpgrade} -> {perLevel}");
                        }
                        else if (dbCandidate != null && guiAmt > 0)
                        {
                            // Seek authoritative per-level value from DB candidate if available
                            try
                            {
                                var resourcesField2 = dbCandidate.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var candidateResources = resourcesField2?.GetValue(dbCandidate) as System.Collections.IEnumerable;
                                if (candidateResources != null)
                                {
                                    foreach (var req in candidateResources)
                                    {
                                        try
                                        {
                                            var et = req.GetType();
                                            var nameObj = et.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                            string resName = null;
                                            if (nameObj != null)
                                            {
                                                var itemData = nameObj.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(nameObj);
                                                var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                                resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                            }
                                            if (string.IsNullOrEmpty(resName)) continue;
                                            if (!string.Equals(resName, name, StringComparison.OrdinalIgnoreCase)) continue;

                                            // prefer m_amountPerLevel if present
                                            int dbPerLevel = 0;
                                            var perLevelObj = et.GetField("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                            if (perLevelObj != null) { try { dbPerLevel = Convert.ToInt32(perLevelObj); } catch { dbPerLevel = 0; } }

                                            if (dbPerLevel > 0)
                                            {
                                                perLevel = dbPerLevel;
                                                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (fast-path): using DB m_amountPerLevel={dbPerLevel} for {name}");
                                            }
                                            else
                                            {
                                                int dbAmount = 0;
                                                var amountObj2 = et.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                                if (amountObj2 != null) { try { dbAmount = Convert.ToInt32(amountObj2); } catch { dbAmount = 0; } }

                                                if (dbAmount > 0)
                                                {
                                                    if (levelsToUpgrade > 1 && (dbAmount % levelsToUpgrade) == 0)
                                                    {
                                                        perLevel = dbAmount / levelsToUpgrade;
                                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (fast-path): inferred per-level from DB m_amount {dbAmount} / levels {levelsToUpgrade} -> {perLevel}");
                                                    }
                                                    else if (dbAmount < guiAmt)
                                                    {
                                                        perLevel = dbAmount;
                                                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (fast-path): using DB m_amount={dbAmount} for {name} (fallback)");
                                                    }
                                                }
                                            }

                                            break;
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                        }

                        normalized.Add((name, perLevel));
                    }
                    catch { /* ignore per-entry */ }
                }

                // Remove using normalized amounts
                foreach (var rr in normalized)
                {
                    try
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (fast-path): removing '{rr.name}' amount={rr.amount} (GUI-provided normalized) skipping target={ItemInfo(upgradeTargetItem)}");
                        int removed = RemoveAmountFromInventorySkippingTarget(inventory, upgradeTargetItem, rr.name, rr.amount);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (fast-path): removed {removed}/{rr.amount} of '{rr.name}'");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (fast-path) per-resource exception: {ex}");
                    }
                }

                return;
            }

            // Try GUI-provided requirement list first (fallback)
            List<(string name, int amount)> guiReqs;
            bool haveGuiReqs = TryGetRequirementsFromGui(gui, out guiReqs);
            if (haveGuiReqs && guiReqs != null && guiReqs.Count > 0)
            {
                var dbgJoined = string.Join(", ", guiReqs.Select(x => x.name + ":" + x.amount));
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: using GUI-provided requirements: {dbgJoined}");
            }
            else
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: no GUI-provided requirements found (will try ObjectDB candidate or fallback to recipe resources).");
            }

            // Determine the recipe to use for fallback reading / logging
            var guiRecipeNow = GetUpgradeRecipeFromGui(gui);
            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: GetUpgradeRecipeFromGui returned: {(guiRecipeNow != null ? RecipeInfo(guiRecipeNow) : "<null>")}");
            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: _upgradeGuiRecipe={(_upgradeGuiRecipe != null ? RecipeInfo(_upgradeGuiRecipe) : "<null>")}, _upgradeRecipe={(_upgradeRecipe != null ? RecipeInfo(_upgradeRecipe) : "<null>")}");
            Recipe recipeToUse = selectedRecipe;
            if (guiRecipeNow != null) recipeToUse = guiRecipeNow;
            else if (_upgradeGuiRecipe != null) recipeToUse = _upgradeGuiRecipe;
            else if (_upgradeRecipe != null) recipeToUse = _upgradeRecipe;
            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: initial recipeToUse = {RecipeInfo(recipeToUse)} (hash={recipeToUse?.GetHashCode():X8})");

            // If GUI had no separate list, and recipeToUse is the same as crafting recipe, attempt to find an ObjectDB candidate (upgrade recipe)
            if ((!haveGuiReqs || guiReqs == null || guiReqs.Count == 0) && ReferenceEquals(recipeToUse, selectedRecipe))
            {
                try
                {
                    var candidate = FindBestUpgradeRecipeCandidate(selectedRecipe);
                    if (candidate != null)
                    {
                        recipeToUse = candidate;
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: using ObjectDB candidate recipe {RecipeInfo(candidate)}");
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: no suitable ObjectDB candidate found (will fall back to craft recipe resources).");
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: FindBestUpgradeRecipeCandidate exception: {ex}");
                }
            }

            object GetMember(object obj, string name)
            {
                if (obj == null) return null;
                var t = obj.GetType();
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(obj);
                var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p.GetValue(obj);
                return null;
            }
            object GetNested(object obj, params string[] names)
            {
                object cur = obj;
                foreach (var n in names)
                {
                    if (cur == null) return null;
                    cur = GetMember(cur, n);
                }
                return cur;
            }
            int ToInt(object v)
            {
                if (v == null) return 0;
                try { return Convert.ToInt32(v); } catch { return 0; }
            }

            // Build validReqs either from GUI list or recipeToUse.m_resources
            var validReqs = new List<(object req, string name, int amount)>();

            // --- NEW: normalize GUI-provided totals into per-level amounts when appropriate ---
            if (haveGuiReqs && guiReqs != null && guiReqs.Count > 0)
            {
                // Determine levelsToUpgrade similar to Prefix logic: prefer upgradeTargetItem if available
                int levelsToUpgrade = 1;
                try
                {
                    if (upgradeTargetItem != null)
                    {
                        var recipeForQuality = _upgradeGuiRecipe ?? _upgradeRecipe ?? recipeToUse ?? selectedRecipe;
                        int finalQuality = recipeForQuality?.m_item?.m_itemData?.m_quality ?? 0;
                        levelsToUpgrade = Math.Max(1, finalQuality - upgradeTargetItem.m_quality);
                    }
                    else
                    {
                        var craftUpgradeFieldLocal = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                        if (craftUpgradeFieldLocal != null)
                        {
                            try
                            {
                                var cvObj = craftUpgradeFieldLocal.GetValue(gui);
                                if (cvObj is int cv && cv > 0)
                                {
                                    int selQuality = selectedRecipe?.m_item?.m_itemData?.m_quality ?? 0;
                                    if (selQuality > 0 && cv > selQuality)
                                        levelsToUpgrade = Math.Max(1, cv - selQuality);
                                    else
                                        levelsToUpgrade = Math.Max(1, cv);
                                }
                            }
                            catch { levelsToUpgrade = 1; }
                        }
                    }
                }
                catch { levelsToUpgrade = 1; }

                // Try to use DB candidate as a hint for per-level amounts (prefer m_amountPerLevel, prefer next-quality recipe)
                Recipe dbCandidate = null;
                try
                {
                    if (upgradeTargetItem != null && ObjectDB.instance != null)
                    {
                        // Look specifically for the recipe that produces the next quality (preferred)
                        string desiredResultName = selectedRecipe.m_item?.m_itemData?.m_shared?.m_name;
                        int desiredQuality = upgradeTargetItem.m_quality + 1;
                        foreach (var r in ObjectDB.instance.m_recipes)
                        {
                            try
                            {
                                if (r == null || r.m_item == null) continue;
                                var rn = r.m_item.m_itemData?.m_shared?.m_name;
                                if (!string.Equals(rn, desiredResultName, StringComparison.OrdinalIgnoreCase)) continue;
                                int q = r.m_item.m_itemData?.m_quality ?? 0;
                                if (q == desiredQuality)
                                {
                                    dbCandidate = r;
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: selected DB candidate by exact quality match (next-level) -> {RecipeInfo(r)}");
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                    // fallback...
                    if (dbCandidate == null)
                    {
                        try { dbCandidate = FindBestUpgradeRecipeCandidate(selectedRecipe); } catch { dbCandidate = null; }
                    }
                }
                catch { dbCandidate = null; }

                foreach (var g in guiReqs)
                {
                    int guiAmt = g.amount;
                    int perLevel = guiAmt;

                    // If GUI total is divisible by detected levels, normalize quickly
                    if (levelsToUpgrade > 1 && guiAmt > 0 && (guiAmt % levelsToUpgrade) == 0)
                    {
                        perLevel = guiAmt / levelsToUpgrade;
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: normalized GUI req {g.name}:{guiAmt} by levelsToUpgrade={levelsToUpgrade} -> {perLevel}");
                    }
                    else if (dbCandidate != null && guiAmt > 0)
                    {
                        // Prefer m_amountPerLevel from DB candidate (if present), otherwise try m_amount
                        try
                        {
                            var resourcesField2 = dbCandidate.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var candidateResources = resourcesField2?.GetValue(dbCandidate) as System.Collections.IEnumerable;
                            if (candidateResources != null)
                            {
                                foreach (var req in candidateResources)
                                {
                                    try
                                    {
                                        var et = req.GetType();
                                        var nameObj = et.GetField("m_resItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                        string resName = null;
                                        if (nameObj != null)
                                        {
                                            var itemData = nameObj.GetType().GetField("m_itemData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(nameObj);
                                            var shared = itemData?.GetType().GetField("m_shared", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(itemData);
                                            resName = shared?.GetType().GetField("m_name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(shared) as string;
                                        }
                                        if (string.IsNullOrEmpty(resName)) continue;
                                        if (!string.Equals(resName, g.name, StringComparison.OrdinalIgnoreCase)) continue;

                                        // Read per-level first
                                        int dbPerLevel = 0;
                                        var perLevelObj = et.GetField("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                        if (perLevelObj != null) { try { dbPerLevel = Convert.ToInt32(perLevelObj); } catch { dbPerLevel = 0; } }

                                        if (dbPerLevel > 0)
                                        {
                                            // Use the DB per-level amount (most authoritative for upgrades)
                                            perLevel = dbPerLevel;
                                            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: using DB m_amountPerLevel={dbPerLevel} for {g.name}");
                                        }
                                        else
                                        {
                                            // Fallback to DB m_amount (per-recipe total)
                                            int dbAmount = 0;
                                            var amountObj2 = et.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(req);
                                            if (amountObj2 != null) { try { dbAmount = Convert.ToInt32(amountObj2); } catch { dbAmount = 0; } }
                                            // If dbAmount divides GUI total, infer per-level as dbAmount/levelsToUpgrade when sensible
                                            if (dbAmount > 0)
                                            {
                                                if (levelsToUpgrade > 1 && (dbAmount % levelsToUpgrade) == 0)
                                                {
                                                    perLevel = dbAmount / levelsToUpgrade;
                                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: inferred per-level from DB m_amount {dbAmount} / levels {levelsToUpgrade} -> {perLevel}");
                                                }
                                                else if (dbAmount < guiAmt)
                                                {
                                                    // If DB reports a smaller total than GUI and doesn't divide cleanly, prefer dbAmount (likely more authoritative)
                                                    perLevel = dbAmount;
                                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: using DB m_amount={dbAmount} for {g.name} (fallback)");
                                                }
                                            }
                                        }

                                        break;
                                    }
                                    catch { /* ignore per-req */ }
                                }
                            }
                        }
                        catch { /* ignore DB parse errors */ }
                    }

                    validReqs.Add((null, g.name, perLevel));
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: GUI req '{g.name}' normalized amount={perLevel} (guiTotal={guiAmt})");
                }
            }
            else
            {
                // Read recipeToUse.m_resources (existing logic, but uses per-level only when we detect an upgrade context)
                try
                {
                    var resourcesObj2 = GetMember(recipeToUse, "m_resources");
                    var resources2 = resourcesObj2 as System.Collections.IEnumerable;
                    if (resources2 != null)
                    {
                        // preserve craftUpgrade multiplier if present (used only as fallback to infer levels)
                        var craftUpgradeField2 = typeof(InventoryGui).GetField("m_craftUpgrade", BindingFlags.Instance | BindingFlags.NonPublic);
                        int craftUpgrade2 = 1;
                        if (craftUpgradeField2 != null)
                        {
                            try
                            {
                                object val = craftUpgradeField2.GetValue(gui);
                                if (val is int q && q > 1) craftUpgrade2 = q;
                            }
                            catch { }
                        }

                        foreach (var req in resources2.Cast<object>())
                        {
                            try
                            {
                                var shared = GetNested(req, "m_resItem", "m_itemData", "m_shared");
                                if (shared == null) continue;
                                var nameObj = GetNested(req, "m_resItem", "m_itemData", "m_shared", "m_name");
                                string resourceName = nameObj as string;
                                if (string.IsNullOrEmpty(resourceName)) continue;

                                int baseAmount = ToInt(GetMember(req, "m_amount"));
                                int perLevel = ToInt(GetMember(req, "m_amountPerLevel"));

                                // Use per-level only if this is actually an upgrade context
                                bool isUpgradeNowLocal = _isUpgradeDetected || IsUpgradeOperation(gui, recipeToUse);

                                int amount;
                                if (isUpgradeNowLocal && perLevel > 0)
                                {
                                    int levelsToUpgrade = 1;
                                    try
                                    {
                                        if (upgradeTargetItem != null)
                                        {
                                            int finalQuality = recipeToUse.m_item?.m_itemData?.m_quality ?? selectedRecipe.m_item?.m_itemData?.m_quality ?? 0;
                                            levelsToUpgrade = Math.Max(1, finalQuality - upgradeTargetItem.m_quality);
                                        }
                                        else
                                        {
                                            if (craftUpgrade2 > 1)
                                            {
                                                int selQuality = selectedRecipe?.m_item?.m_itemData?.m_quality ?? 0;
                                                if (selQuality > 0 && craftUpgrade2 > selQuality)
                                                    levelsToUpgrade = Math.Max(1, craftUpgrade2 - selQuality);
                                                else
                                                    levelsToUpgrade = Math.Max(1, craftUpgrade2);
                                            }
                                        }
                                    }
                                    catch
                                    {
                                        levelsToUpgrade = 1;
                                    }

                                    amount = perLevel * levelsToUpgrade;
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: using per-level amount for '{resourceName}' -> perLevel={perLevel} levelsToUpgrade={levelsToUpgrade} amount={amount}");
                                }
                                else
                                {
                                    amount = baseAmount;
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: using base m_amount for '{resourceName}' -> {amount}");
                                }

                                if (amount <= 0) continue;
                                validReqs.Add((req, resourceName, amount));
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: recipe req '{resourceName}' amount={amount} (base={baseAmount} perLevel={perLevel})");
                            }
                            catch { /* ignore per-resource */ }
                        }
                    }
                    else
                    {
                        UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: recipeToUse has no m_resources to read.");
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: exception reading recipeToUse resources: {ex}");
                }
            }

            // Filter out result-resource (we never remove resource that equals recipe result)
            string resultName = null;
            try
            {
                resultName = recipeToUse.m_item?.m_itemData?.m_shared?.m_name;
                UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: recipeToUse resultName='{resultName}'");
            }
            catch { resultName = null; }

            var validReqsFiltered2 = validReqs.Where(v => !string.Equals(v.name, resultName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (validReqsFiltered2.Count == 0)
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: filtered valid reqs empty (all resources were result-resource?) -> nothing to remove.");
                return;
            }

            // crafted == true: remove all required resources (plugin-managed)
            if (crafted)
            {
                foreach (var req in validReqs)
                {
                    var name = req.name;
                    var amount = req.amount;

                    if (!string.IsNullOrEmpty(resultName) && string.Equals(name, resultName, StringComparison.OrdinalIgnoreCase))
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: skipping removal of result-resource '{name}' on crafted=true");
                        continue;
                    }

                    try
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (crafted): removing '{name}' amount={amount}");
                        int removed = RemoveAmountFromInventorySkippingTarget(inventory, upgradeTargetItem, name, amount);
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (crafted): removed {removed}/{amount} of '{name}'");
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade (crafted) removal failed: {ex}");
                    }
                }
                return;
            }

            // Failure behavior (keep-one except result resource)
            if (validReqs.Count == 0)
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: no valid requirements found -> nothing to remove.");
                return;
            }

            var keepCandidates = validReqs.Where(v => !string.Equals(v.name, resultName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (keepCandidates.Count == 0)
            {
                UnityEngine.Debug.LogWarning("[ChanceCraft] RemoveRequiredResourcesUpgrade: after filtering by result-resource, no keep candidates -> nothing to remove.");
                return;
            }

            int keepIndex = UnityEngine.Random.Range(0, keepCandidates.Count);
            var keepTuple = keepCandidates[keepIndex];
            UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: keepIndex={keepIndex} keepTuple={keepTuple.name}:{keepTuple.amount}");

            bool skippedKeep = false;
            foreach (var entry in validReqs)
            {
                var resourceName = entry.name;
                int amount = entry.amount;

                if (string.IsNullOrEmpty(resourceName)) continue;

                if (!string.IsNullOrEmpty(resultName) && string.Equals(resourceName, resultName, StringComparison.OrdinalIgnoreCase))
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: preserving result-resource '{resourceName}'");
                    continue;
                }

                if (!skippedKeep && resourceName == keepTuple.name && amount == keepTuple.amount)
                {
                    skippedKeep = true;
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: keeping resource '{resourceName}' amount={amount} (keep-one)");
                    continue;
                }

                try
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: removing resource '{resourceName}' amount={amount}");
                    int removed = RemoveAmountFromInventorySkippingTarget(inventory, upgradeTargetItem, resourceName, amount);
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade: removed {removed}/{amount} of '{resourceName}'");
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] RemoveRequiredResourcesUpgrade removal failed: {ex}");
                }
            }
        }

        // (Replace the existing TryGetRequirementsFromGui method in ChanceCraft.cs with this implementation.)
        private static bool TryGetRequirementsFromGui(InventoryGui gui, out List<(string name, int amount)> requirements)
        {
            requirements = null;
            if (gui == null) return false;

            try
            {
                var tGui = typeof(InventoryGui);

                List<(string name, int amount)> ParseRequirementEnumerable(System.Collections.IEnumerable reqEnum)
                {
                    var outList = new List<(string name, int amount)>();
                    if (reqEnum == null) return outList;
                    int idx = 0;
                    foreach (var elem in reqEnum)
                    {
                        idx++;
                        if (elem == null)
                        {
                            if (idx > 200) break;
                            else continue;
                        }
                        try
                        {
                            var et = elem.GetType();

                            // Resolve resource name
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

                            // Prefer base m_amount first (total). Use m_amountPerLevel only as fallback when total not present.
                            int amount = 0;
                            object amountObj = null;
                            object amountPerLevelObj = null;

                            var fAmount = et.GetField("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var pAmount = et.GetProperty("m_amount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fAmount != null) amountObj = fAmount.GetValue(elem);
                            else if (pAmount != null) amountObj = pAmount.GetValue(elem);

                            var fAmountPerLevel = et.GetField("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var pAmountPerLevel = et.GetProperty("m_amountPerLevel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fAmountPerLevel != null) amountPerLevelObj = fAmountPerLevel.GetValue(elem);
                            else if (pAmountPerLevel != null) amountPerLevelObj = pAmountPerLevel.GetValue(elem);

                            int parsedAmount = 0;
                            int parsedPerLevel = 0;
                            if (amountObj != null)
                            {
                                try { parsedAmount = Convert.ToInt32(amountObj); } catch { parsedAmount = 0; }
                                if (parsedAmount > 0)
                                {
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TryGetRequirementsFromGui: found m_amount for resource '{resName}' -> {parsedAmount}");
                                }
                            }

                            if (amountPerLevelObj != null)
                            {
                                try { parsedPerLevel = Convert.ToInt32(amountPerLevelObj); } catch { parsedPerLevel = 0; }
                                if (parsedPerLevel > 0)
                                {
                                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TryGetRequirementsFromGui: found m_amountPerLevel for resource '{resName}' -> {parsedPerLevel}");
                                }
                            }

                            // IMPORTANT: always prefer the recipe total (m_amount) when available.
                            // Use m_amountPerLevel only as a fallback when m_amount is absent or zero.
                            if (parsedAmount > 0)
                            {
                                amount = parsedAmount;
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] TryGetRequirementsFromGui: using m_amount for resource '{resName}' -> {amount}");
                            }
                            else if (parsedPerLevel > 0)
                            {
                                amount = parsedPerLevel;
                                UnityEngine.Debug.LogWarning($"[ChanceCraft] TryGetRequirementsFromGui: using m_amountPerLevel (no m_amount) for '{resName}' -> {amount}");
                            }
                            else
                            {
                                amount = 0;
                            }

                            if (!string.IsNullOrEmpty(resName) && amount > 0)
                                outList.Add((resName, amount));
                        }
                        catch { /* ignore per-element parse failures */ }

                        if (idx > 200) break;
                    }
                    return outList;
                }

                // 0) Quick path: InventoryGui.m_reqList
                try
                {
                    var reqListField = tGui.GetField("m_reqList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (reqListField != null)
                    {
                        var reqListObj = reqListField.GetValue(gui) as System.Collections.IEnumerable;
                        if (reqListObj != null)
                        {
                            var list = ParseRequirementEnumerable(reqListObj);
                            if (list.Count > 0)
                            {
                                requirements = list;
                                var joined = string.Join(", ", list.Select(x => x.name + ":" + x.amount));
                                UnityEngine.Debug.LogWarning("[ChanceCraft] TryGetRequirementsFromGui: found " + list.Count + " reqs via m_reqList: " + joined);
                                return true;
                            }
                            else
                            {
                                UnityEngine.Debug.LogWarning("[ChanceCraft] TryGetRequirementsFromGui: m_reqList present but empty or could not be parsed.");
                            }
                        }
                    }
                }
                catch (Exception exReqList)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] TryGetRequirementsFromGui: exception while reading m_reqList: {exReqList}");
                }

                // (rest of method unchanged: fallback scanning wrappers/fields/properties, calling ParseRequirementEnumerable as before)
                // Fallback scanning of wrappers / fields / properties
                var selectedRecipeField = tGui.GetField("m_selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (selectedRecipeField != null)
                {
                    try
                    {
                        var selVal = selectedRecipeField.GetValue(gui);
                        if (selVal != null)
                        {
                            object maybeResources = null;
                            var wrapperType = selVal.GetType();
                            var candNames = new[] { "m_resources", "m_requirements", "resources", "requirements", "m_recipeResources", "m_resourceList", "m_reqList", "Requirements", "Resources" };
                            foreach (var name in candNames)
                            {
                                try
                                {
                                    var f = wrapperType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                    if (f != null)
                                    {
                                        maybeResources = f.GetValue(selVal);
                                        if (maybeResources != null) break;
                                    }
                                    var p = wrapperType.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                    if (p != null && p.CanRead)
                                    {
                                        maybeResources = p.GetValue(selVal);
                                        if (maybeResources != null) break;
                                    }
                                }
                                catch { /* ignore per-candidate */ }
                            }

                            if (maybeResources is System.Collections.IEnumerable enumRes)
                            {
                                var list = ParseRequirementEnumerable(enumRes);
                                if (list.Count > 0)
                                {
                                    requirements = list;
                                    var joined = string.Join(", ", list.Select(x => x.name + ":" + x.amount));
                                    UnityEngine.Debug.LogWarning("[ChanceCraft] TryGetRequirementsFromGui: found " + list.Count + " reqs on wrapper.m_selectedRecipe: " + joined);
                                    return true;
                                }
                            }

                            // Try extracting inner Recipe and reading its m_resources
                            Recipe innerRecipe;
                            string path;
                            Recipe selectedInWrapper = null;
                            try
                            {
                                var rp = wrapperType.GetProperty("Recipe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (rp != null) selectedInWrapper = rp.GetValue(selVal) as Recipe;
                            }
                            catch { /* ignore */ }

                            if (TryExtractRecipeFromWrapper(selVal, selectedInWrapper, out innerRecipe, out path))
                            {
                                var resourcesField2 = innerRecipe?.GetType().GetField("m_resources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var resObj = resourcesField2?.GetValue(innerRecipe) as System.Collections.IEnumerable;
                                var list = ParseRequirementEnumerable(resObj);
                                if (list.Count > 0)
                                {
                                    requirements = list;
                                    var joined = string.Join(", ", list.Select(x => x.name + ":" + x.amount));
                                    UnityEngine.Debug.LogWarning("[ChanceCraft] TryGetRequirementsFromGui: got resources from inner recipe at wrapper path '" + path + "' -> " + joined);
                                    return true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] TryGetRequirementsFromGui: error reading selectedRecipe wrapper: {ex}");
                    }
                }

                // Scan InventoryGui fields/properties for enumerables — fallback
                var fields = tGui.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var f in fields)
                {
                    try
                    {
                        var val = f.GetValue(gui);
                        if (val == null) continue;
                        if (!(val is System.Collections.IEnumerable en)) continue;

                        var list = ParseRequirementEnumerable(en);
                        if (list.Count > 0)
                        {
                            requirements = list;
                            var joined = string.Join(", ", list.Select(x => x.name + ":" + x.amount));
                            UnityEngine.Debug.LogWarning("[ChanceCraft] TryGetRequirementsFromGui: found " + list.Count + " reqs on InventoryGui field '" + f.Name + "': " + joined);
                            return true;
                        }
                    }
                    catch { /* ignore per-field */ }
                }

                var props = tGui.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (var p in props)
                {
                    try
                    {
                        if (p.GetIndexParameters().Length > 0) continue;
                        var val = p.GetValue(gui);
                        if (val == null || !(val is System.Collections.IEnumerable en)) continue;

                        var list = ParseRequirementEnumerable(en);
                        if (list.Count > 0)
                        {
                            requirements = list;
                            var joined = string.Join(", ", list.Select(x => x.name + ":" + x.amount));
                            UnityEngine.Debug.LogWarning("[ChanceCraft] TryGetRequirementsFromGui: found " + list.Count + " reqs on InventoryGui prop '" + p.Name + "': " + joined);
                            return true;
                        }
                    }
                    catch { /* ignore per-prop */ }
                }

                UnityEngine.Debug.LogWarning("[ChanceCraft] TryGetRequirementsFromGui: no GUI-provided requirements found.");
                return false;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] TryGetRequirementsFromGui exception: {ex}");
                requirements = null;
                return false;
            }
        }

        // Diagnostic helper: dump InventoryGui structure
        private static void LogInventoryGuiStructure(InventoryGui gui, int maxItemsToShow = 6)
        {
            try
            {
                var t = typeof(InventoryGui);
                UnityEngine.Debug.LogWarning($"[ChanceCraft] LogInventoryGuiStructure: Inspecting InventoryGui instance of type {t.FullName}");

                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        object val = f.GetValue(gui);
                        if (val == null)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] IG.field {f.Name}: <null> type={f.FieldType.FullName}");
                            continue;
                        }
                        string typeName = val.GetType().FullName;
                        if (val is System.Collections.IEnumerable en && !(val is string))
                        {
                            int count = 0;
                            var elems = new System.Text.StringBuilder();
                            foreach (var e in en)
                            {
                                if (count >= maxItemsToShow) break;
                                if (e == null) { elems.Append("<null>,"); }
                                else elems.Append($"{e.GetType().Name}:{e},");
                                count++;
                            }
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] IG.field {f.Name}: type={typeName} enumerable sampleCount={count} sample=[{elems}]");
                        }
                        else
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] IG.field {f.Name}: type={typeName} valueToString='{val}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] IG.field {f.Name}: Exception reading: {ex}");
                    }
                }

                foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    try
                    {
                        if (p.GetIndexParameters().Length > 0) { UnityEngine.Debug.LogWarning($"[ChanceCraft] IG.prop {p.Name}: indexed (skip)"); continue; }
                        object val = null;
                        try { val = p.GetValue(gui); } catch (Exception e) { UnityEngine.Debug.LogWarning($"[ChanceCraft] IG.prop {p.Name}: exception getting value: {e}"); continue; }
                        if (val == null)
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] IG.prop {p.Name}: <null> type={p.PropertyType.FullName}");
                            continue;
                        }
                        string typeName = val.GetType().FullName;
                        if (val is System.Collections.IEnumerable en && !(val is string))
                        {
                            int count = 0;
                            var elems = new System.Text.StringBuilder();
                            foreach (var e in en)
                            {
                                if (count >= maxItemsToShow) break;
                                if (e == null) { elems.Append("<null>,"); }
                                else elems.Append($"{e.GetType().Name}:{e},");
                                count++;
                            }
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] IG.prop {p.Name}: type={typeName} enumerable sampleCount={count} sample=[{elems}]");
                        }
                        else
                        {
                            UnityEngine.Debug.LogWarning($"[ChanceCraft] IG.prop {p.Name}: type={typeName} valueToString='{val}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning($"[ChanceCraft] IG.prop {p.Name}: Exception reading: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] LogInventoryGuiStructure exception: {ex}");
            }
        }

        // Strong UI refresh helper: force Unity UI layout rebuilds and toggle likely crafting panel objects.
        // Call this after you've mutated item data and invoked RefreshInventoryGui/RefreshCraftingPanel.
        private static void ForceGuiHardRefresh(InventoryGui gui)
        {
            if (gui == null) return;
            try
            {
                // 1) Run the previous best-effort refresh first
                try { RefreshCraftingPanel(gui); } catch { }

                // 2) Force Canvas update
                try { UnityEngine.Canvas.ForceUpdateCanvases(); } catch { }

                // 3) Find RectTransforms / UI Components on the InventoryGui and force layout rebuild
                var t = gui.GetType();

                // Inspect fields and properties for RectTransform / GameObject / Component samples
                var seen = new HashSet<int>();
                void TryRebuildObject(object obj)
                {
                    if (obj == null) return;
                    int id = RuntimeHelpers.GetHashCode(obj);
                    if (seen.Contains(id)) return;
                    seen.Add(id);

                    // GameObject
                    if (obj is GameObject go)
                    {
                        try
                        {
                            // Force deactivate/activate to force UI rebuild (best-effort)
                            go.SetActive(false);
                            go.SetActive(true);
                        }
                        catch { }

                        var rt = go.GetComponent<RectTransform>();
                        if (rt != null)
                        {
                            try { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rt); } catch { }
                        }
                        return;
                    }

                    // Component
                    if (obj is Component comp)
                    {
                        try
                        {
                            var go2 = comp.gameObject;
                            try { go2.SetActive(false); } catch { }
                            try { go2.SetActive(true); } catch { }
                        }
                        catch { }

                        var rt2 = comp.GetComponent<RectTransform>();
                        if (rt2 != null)
                        {
                            try { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rt2); } catch { }
                        }
                        return;
                    }

                    // RectTransform directly
                    if (obj is RectTransform rtf)
                    {
                        try { UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rtf); } catch { }
                        return;
                    }

                    // Enumerable: try a few elements
                    if (obj is System.Collections.IEnumerable ie && !(obj is string))
                    {
                        int c = 0;
                        foreach (var e in ie)
                        {
                            if (e == null) continue;
                            TryRebuildObject(e);
                            if (++c > 6) break;
                        }
                        return;
                    }
                }

                try
                {
                    // Try InventoryGui fields
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        try
                        {
                            var val = f.GetValue(gui);
                            if (val == null) continue;

                            // If field name hints a crafting panel - toggle it specifically
                            var name = f.Name?.ToLowerInvariant() ?? "";
                            if (name.Contains("craft") || name.Contains("recipe") || name.Contains("panel") || name.Contains("left"))
                            {
                                TryRebuildObject(val);
                            }
                            else
                            {
                                // still attempt rebuild on likely UI types
                                TryRebuildObject(val);
                            }
                        }
                        catch { /* ignore per-field */ }
                    }

                    // Try InventoryGui properties
                    foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        try
                        {
                            if (p.GetIndexParameters().Length > 0) continue;
                            if (!p.CanRead) continue;
                            var val = p.GetValue(gui);
                            if (val == null) continue;
                            TryRebuildObject(val);
                        }
                        catch { /* ignore per-prop */ }
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[ChanceCraft] ForceGuiHardRefresh: reflection scan exception: {ex}");
                }

                // 4) As a fallback, toggle the InventoryGui gameObject itself (forces full redraw)
                try
                {
                    var igGo = (gui as Component)?.gameObject;
                    if (igGo != null)
                    {
                        igGo.SetActive(false);
                        // Wait a tiny bit by scheduling re-enable next frame via coroutine if possible
                        try
                        {
                            gui.StartCoroutine(ReenableNextFrame(igGo));
                        }
                        catch
                        {
                            // immediate reenable if coroutine can't be started
                            try { igGo.SetActive(true); } catch { }
                        }
                    }
                }
                catch { /* ignore */ }
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[ChanceCraft] ForceGuiHardRefresh exception: {ex}");
            }
        }

        // Coroutine helper to re-enable GameObject on next frame (best-effort)
        private static IEnumerator ReenableNextFrame(GameObject go)
        {
            yield return null;
            try { if (go != null) go.SetActive(true); } catch { }
        }



    }
}