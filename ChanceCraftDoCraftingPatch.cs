using System;
using HarmonyLib;
using UnityEngine;

namespace ChanceCraft
{
    // Place this patch class in the ChanceCraft namespace (same assembly as your plugin)
    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    static class InventoryGuiDoCraftingPatch_FixGuiParam
    {
        // Use __instance to receive the patched instance from Harmony.
        // Do NOT name this parameter "gui" (Harmony would try to match it to an original method parameter).
        static void Prefix(InventoryGui __instance, Player player)
        {
            try
            {
                // __instance is the InventoryGui instance of the patched object.
                // Use a local variable named gui for clarity if you like.
                var gui = __instance;
                if (gui == null) return;

                // Example safe calls into your helpers (replace / extend with actual calls you need)
                // These helpers expect InventoryGui and will work when passed gui.
                try
                {
                    // Keep calls minimal and guarded to avoid breaking the prefix.
                    // For example, detect upgrade recipe or requirements:
                    var upgradeRecipe = ChanceCraftUIHelpers.GetUpgradeRecipeFromGui(gui);
                    ChanceCraftUIHelpers.TryGetRequirementsFromGui(gui, out var guiReqs);
                    // ... any other pre-craft preparation using helper methods ...
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ChanceCraft] DoCrafting Prefix helper call failed: {ex}");
                }

                // If you need to preserve original behavior or set plugin state, do it here.
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChanceCraft] InventoryGui.DoCrafting Prefix unexpected exception: {ex}");
            }
        }
    }
}