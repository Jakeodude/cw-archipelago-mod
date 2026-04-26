# Adding items and locations

The most common change to this mod is "add a new AP item" or "add a new AP location." Each touches multiple files in a specific order, and the changes must be coordinated with the **APWorld** (the Python server-side counterpart). This doc walks through both procedures with worked examples.

## Prereq: the APWorld coordination contract

Item and location IDs are derived from a base ID plus an offset. The base ID is hard-coded in both halves and **must match exactly** between this mod and the APWorld:

- Items: `item_base_id = 98765000` (in the APWorld's `items.py`)
- Locations: `location_base_id` (in the APWorld's `locations.py`)

The actual numeric ID an item/location gets is `base_id + offset`. The offset comes from a per-item/location table in the APWorld. **If you add an item here without adding it to the APWorld (or vice versa), the server will reject your connection or silently drop the item/location.**

Names are case-sensitive strings. They must match between `Data/ItemNames.cs` (this mod) and `items.py` (APWorld) — and likewise for locations.

---

## Adding an item

Five steps. Order matters because `ItemData.HandleReceivedItem` switches on the name string.

### 1. APWorld side (out of scope for this repo)

Add the item to `items.py` with its name, classification (progression / useful / filler / trap), and offset. Add it to whichever item-pool generator produces it.

### 2. `Data/ItemNames.cs` — register the C# name constant

Use the same string the APWorld uses. Convention: PascalCase const, value is the human-readable name.

```csharp
public const string ProgOxygen = "Progressive Oxygen";   // 4 copies
```

### 3. `Core/APSaveData.cs` — add persistent state if needed

Only if the item changes persistent state (a level counter, an unlock flag, a queue). Filler items that apply once and don't need to survive reconnection don't need a field.

Examples:
- Progressive items: `int oxygenUpgradeLevel = 0;`
- Boolean unlocks: `bool diveBellO2Unlocked = false;`
- Queues for deferred application: `int pendingMoney = 0;`

If the state shouldn't be persisted (session-only, e.g. session-scoped hat unlocks), tag it `[JsonIgnore]`.

### 4. `Data/ItemData.cs` — add a `case` to `HandleReceivedItem`

This is the central dispatch. Pattern (from [ItemData.cs:130-140](../ContentWarningArchipelago/Data/ItemData.cs#L130-L140)):

```csharp
case ItemNames.ProgCamera:
{
    APSave.saveData.cameraUpgradeLevel++;          // 1. Update saveData
    APSave.Flush();                                 // 2. Persist immediately
    ProgressionStatsPatch.ApplyCameraUpgrade(       // 3. Apply in-game effect
        APSave.saveData.cameraUpgradeLevel);
    APNotificationUI.ShowItemReceived(name, senderName);  // 4. HUD toast
    Plugin.Logger.LogInfo(                          // 5. Log line
        $"[ItemData] Progressive Camera level {APSave.saveData.cameraUpgradeLevel} — " +
        $"battery {90 + APSave.saveData.cameraUpgradeLevel * 30} s.");
    break;
}
```

The five-step shape is consistent across cases:

1. Update `APSave.saveData`.
2. Call `APSave.Flush()` to persist (do this *before* applying effects, so a crash mid-effect doesn't lose the receipt).
3. Apply the in-game effect — usually a static helper on the relevant patch class (`OxygenPatch.ApplyOxygenUpgrade`, `ProgressionStatsPatch.ApplyCameraUpgrade`, etc.).
4. Show a HUD toast via `APNotificationUI.ShowItemReceived(name, senderName)`.
5. Log line tagged `[ItemData]` so the receipt is searchable in `LogOutput.log`.

### 5. (If the effect needs a Harmony patch) write the patch

If the effect requires intercepting a game method (e.g. inflating `maxOxygen` every frame), add a Harmony patch in `Patches/`. Pattern: read the current level from `APSave.saveData` inside the patch and apply scaling.

Examples worth reading first:
- [Patches/OxygenPatch.cs](../ContentWarningArchipelago/Patches/OxygenPatch.cs) — every-frame Prefix that reads `oxygenUpgradeLevel`
- [Patches/ProgressionStatsPatch.cs](../ContentWarningArchipelago/Patches/ProgressionStatsPatch.cs) — applies stamina/camera levels via static helpers

---

## Adding a location

Locations fire when an in-game milestone happens — a shop purchase, a film extraction, picking up a specific item, completing a content event, etc. Steps:

### 1. APWorld side

Add the location to `locations.py` with its name and offset. Wire it into whatever region/rule generator routes randomized items to it.

### 2. `Data/LocationNames.cs`

Add the C# const matching the APWorld string.

### 3. Hook the in-game event with a Harmony patch

Find a method in the game's code that runs **exactly once per milestone** (no more, no less) and patch it. The patch fires the AP check.

Pattern from [ItemPickupPatch.cs](../ContentWarningArchipelago/Patches/ItemPickupPatch.cs):

```csharp
[HarmonyPatch(typeof(ShopHandler), "RPCA_SpawnDrone")]
public static class ShopBuyPatch
{
    [HarmonyPostfix]
    public static void Postfix(byte[] itemIDs)
    {
        if (!PhotonNetwork.IsMasterClient) return;        // Only host fires the check
        if (Plugin.connection == null || !Plugin.connection.connected) return;

        foreach (byte id in itemIDs)
        {
            string locName = ResolveLocationName(id);     // Use overrides + display name
            long locId = LocationData.GetId(locName);
            if (locId > 0)
                Plugin.SendCheck(locId);
        }
    }
}
```

Three rules every location-firing patch should follow:

1. **Master-client guard.** `if (!PhotonNetwork.IsMasterClient) return;` — game events broadcast to all clients fire this patch on every player. Without the guard, you send N copies of the check.
2. **Connection guard.** `if (Plugin.connection == null || !Plugin.connection.connected) return;` — the patch must run safely even when AP is disconnected (e.g. solo play).
3. **Use `AccessTools` for type/method resolution where possible.** Lets the patch gracefully no-op if the game renames a target on a future update, rather than crashing on plugin load.

### 4. Cross-player notification (automatic)

`Plugin.SendCheck(locId)` queues the check on the outgoing coroutine. When the AP server credits it, `ArchipelagoClient.ActivateCheck` also broadcasts a `MyceliumNetwork.RPC` to everyone in the lobby — they see "*<player>* found *<location>*" via [APNotificationUI.ShowLocationFound](../ContentWarningArchipelago/UI/APNotificationUI.cs). You don't need to do anything extra for the toast to appear on other clients.

---

## Worked example: tracing one existing item

Trace **Progressive Oxygen** end-to-end as a reference:

1. **APWorld** — `items.py` defines `"Progressive Oxygen"` with offset 1, classification `progression`, 4 copies.
2. **[ItemNames.cs:12](../ContentWarningArchipelago/Data/ItemNames.cs#L12)** — `public const string ProgOxygen = "Progressive Oxygen";`
3. **[APSaveData.cs:38](../ContentWarningArchipelago/Core/APSaveData.cs#L38)** — `public int oxygenUpgradeLevel = 0;`
4. **[ItemData.cs:147](../ContentWarningArchipelago/Data/ItemData.cs#L147)** — `case ItemNames.ProgOxygen` increments level, flushes, calls `OxygenPatch.ApplyOxygenUpgrade`, shows toast.
5. **[OxygenPatch.cs](../ContentWarningArchipelago/Patches/OxygenPatch.cs)** — Prefix on the oxygen system reads `APSave.saveData.oxygenUpgradeLevel` every frame and adds `60 × level` to `maxOxygen`. Also exposes `ApplyOxygenUpgrade(int level)` that scales the current oxygen proportionally so the bar's percentage is preserved when the cap grows mid-dive.

---

## Worked example: tracing one existing location

Trace a **shop purchase** location end-to-end:

1. **APWorld** — `locations.py` defines `"Bought Trampoline"` with its offset.
2. **`LocationNames.cs`** — corresponding `const string` (or generated from a table).
3. **[ItemPickupPatch.cs](../ContentWarningArchipelago/Patches/ItemPickupPatch.cs) PATCH 1 — `ShopBuyPatch`** — Postfix on `ShopHandler.RPCA_SpawnDrone(byte[] itemIDs)` runs once per successful purchase (after the `CanAfford` check). The patch:
   - Guards `IsMasterClient` so only the host fires the check.
   - Resolves each `byte id` to a display name via game APIs.
   - Looks up `_shopNameOverrides` first (handles emote items with broken/missing display names), then computes `"Bought " + displayName` as fallback.
   - Calls `Plugin.SendCheck(LocationData.GetId(locName))`.

The override table at [ItemPickupPatch.cs:115](../ContentWarningArchipelago/Patches/ItemPickupPatch.cs#L115) is worth understanding before adding new shop items — the auto-computed "Bought *<asset name>*" doesn't always match the human-readable location name in the APWorld. When you add a new shop-purchase location and the check doesn't fire, the override map is the first place to look.

---

## Common pitfalls

- **Item never arrives, no error in logs.** APWorld and mod disagree on the item name or offset. Check that the string in `ItemNames.cs` matches exactly (including case, punctuation, dollar signs) what's in `items.py`.
- **Item arrives but nothing happens.** Missing `case` in `ItemData.HandleReceivedItem` — the dispatch falls through and the item is silently ignored.
- **State resets after reconnect.** Forgot `APSave.Flush()` after updating `saveData`. Or the field is missing from `APSaveData` (so it's never serialized).
- **Check fires N times for one purchase.** Missing `IsMasterClient` guard in the location-firing patch.
- **Check never fires for a shop item.** Missing entry in `_shopNameOverrides` (the asset name doesn't match the auto-computed location name).
- **NRE on connect when reading slot data.** Missing `if (slotData.TryGetValue(...))` guard. Slot data keys aren't guaranteed to exist for older APWorld versions.

---

## See also

- [doc/save-system.md](save-system.md) — `APSaveData` lifecycle and persistence details
- [doc/networking.md](networking.md) — three-layer networking architecture, including how cross-player notifications get from your check-fire to the other player's HUD
- [README.md](../README.md#how-it-works-architecture-overview) — high-level architecture overview
