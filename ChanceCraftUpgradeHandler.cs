using System;
using UnityEngine;

namespace ChanceCraft
{
    public class ChanceCraftUpgradeHandler
    {
        // External helper that refreshes UI or displays messages
        private readonly ChanceCraftUIRefreshUsage _uiHelper = new ChanceCraftUIRefreshUsage();

        // Apply upgrade and only refresh on success; show failure message on failure
        public void ApplyUpgrade(object upgradeTarget, int upgradeIndex)
        {
            try
            {
                if (upgradeTarget == null)
                {
                    Debug.LogWarning("[ChanceCraft] ApplyUpgrade called with null upgradeTarget.");
                    _uiHelper.ShowFailureMessage("No item selected.");
                    return;
                }

                bool success = TryApplyUpgradeToTarget(upgradeTarget, upgradeIndex);
                if (!success)
                {
                    Debug.LogWarning("[ChanceCraft] ApplyUpgrade: upgrade application failed.");
                    // Show game's red failure warning so player sees it
                    _uiHelper.ShowFailureMessage("Upgrade failed.");
                    return;
                }

                // Only refresh UI after a successful change
                ChanceCraftUIRefreshUsage.RefreshCraftingUiAfterChange();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChanceCraft] ApplyUpgrade: unexpected exception: {ex}");
                // In case of an unexpected exception, show a failure message too
                _uiHelper.ShowFailureMessage("Upgrade failed (exception).");
            }
        }

        // Replace with your real upgrade logic; return true on success, false on failure.
        private bool TryApplyUpgradeToTarget(object target, int index)
        {
            try
            {
                // TODO: cast to your game's item type and call the real upgrade API.
                // Example:
                // var item = target as MyGameItem;
                // if (item == null) return false;
                // return item.TryUpgrade(index); // or whatever the API is

                Debug.Log($"[ChanceCraft] TryApplyUpgradeToTarget: would apply upgrade {index} to {target}");
                // For demonstration, pretend success. Change to real behavior.
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChanceCraft] TryApplyUpgradeToTarget: exception: {ex}");
                return false;
            }
        }
    }
}