# ChanceCraft Upgrade Success Rate Changes

## Summary
Modified the ChanceCraft mod to ensure **100% success rate for all item upgrades** while keeping crafting behavior exactly as it was before.

## Changes Made

### 1. Core Logic Changes in `ChanceCraft.cs`

#### Modification to `TrySpawnCraftEffect` Method (Lines ~1704-1750)
- **Added upgrade detection before RNG check**: Detects if the current operation is an upgrade using `IsUpgradeOperation(gui, selectedRecipe) || _isUpgradeDetected`
- **Forced success for upgrades**: Changed `if (UnityEngine.Random.value <= chance)` to `bool success = isUpgrade || (UnityEngine.Random.value <= chance)`
  - When `isUpgrade` is true, the operation always succeeds (100% success rate)
  - When `isUpgrade` is false (crafting), uses the existing configured chance system
- **Added clear logging**: Logs "Upgrade detected - guaranteed success (100% success rate)" for transparency

#### Removed Upgrade Failure Handling (Lines ~1859-2098, replaced with safety check)
- **Removed 240 lines of upgrade failure code** including:
  - In-place upgrade reversion logic
  - Replacement upgrade detection and reversion
  - Resource removal on upgrade failure
  - "Upgrade failed" message to player
- **Added safety check**: If an upgrade somehow reaches the failure path, it logs an error and returns null without applying failure consequences

### 2. Code Comments Updated
- Added comments explaining that upgrades ignore configured success rates
- Added comments noting that quality scaling is only for crafting, not upgrades
- Added prominent warnings that upgrade failure paths should never execute

## Behavior Changes

### Before
- Upgrades had the same success chance as crafting (60% by default, configurable)
- On upgrade failure:
  - Materials were consumed
  - Item quality was reverted to pre-upgrade state
  - Player received "Upgrade failed — materials consumed, item preserved" message

### After
- **Upgrades always succeed (100% success rate)**
- Materials are consumed on success
- Item quality is successfully upgraded
- No failure messages for upgrades

### Unchanged Behavior
- **Crafting (non-upgrade) operations remain exactly as before:**
  - Weapon crafting: 60% success rate (configurable)
  - Armor crafting: 60% success rate (configurable)
  - Arrow crafting: 60% success rate (configurable)
  - Quality scaling for crafting still applies
  - Failure handling for crafting remains unchanged

## Technical Details

### Upgrade Detection
The mod detects upgrades through multiple methods:
1. `m_craftUpgrade` field value > 1
2. Lower-quality item exists in inventory with same name
3. Recipe consumes its own result item
4. GUI-provided requirements differ from base recipe

All these detection methods remain unchanged and continue to work.

### Success Rate Logic Flow

```
IsUpgrade? → YES → Success = true (100%)
           → NO  → Success = Random.value <= configuredChance
```

### Files Modified
- `ChanceCraft.cs`: Main mod file with all logic changes

### Lines of Code
- **Added**: ~30 lines (upgrade detection, safety checks, comments)
- **Removed**: ~240 lines (upgrade failure handling)
- **Net change**: -210 lines (code is simpler and clearer)

## Testing Recommendations

### Upgrade Testing
1. Upgrade a weapon from quality 1 to quality 2
2. Upgrade armor pieces multiple times
3. Verify materials are consumed correctly
4. Verify item quality increases successfully
5. Verify no failure messages appear for upgrades

### Crafting Testing (Should be unchanged)
1. Craft new weapons and verify ~60% success rate (or configured rate)
2. Craft new armor and verify ~60% success rate (or configured rate)
3. Craft arrows and verify ~60% success rate (or configured rate)
4. Verify failure messages still appear for failed crafting
5. Verify "keep-one-resource" mechanic still works for failed crafts

## Rollback Instructions
If issues arise, revert the changes to `ChanceCraft.cs`:
```bash
git checkout HEAD~1 ChanceCraft.cs
```

## Configuration
No configuration changes are needed. The existing config values for weapon/armor/arrow success rates only apply to crafting, not upgrades.

## Known Limitations
- The mod still needs the Valheim game assemblies to compile (not included in repository)
- No unit tests exist in the codebase (this is a game mod injected via Harmony patches)

## Compatibility
This change should be fully compatible with:
- All Valheim versions supported by the original mod
- All other mods that don't modify the same crafting/upgrade logic
- Existing save games and characters
