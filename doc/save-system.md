# Save system

`APSaveData` is the persistence backbone for Archipelago state. Almost every received-item flow touches it, every reconnect re-loads it, and every state-changing patch should `Flush()` after writing. This doc covers the lifecycle, file layout, and the rules for when to add a field.

## Two types

[Core/APSaveData.cs](../ContentWarningArchipelago/Core/APSaveData.cs) defines two things:

- **`APSaveData`** — the plain serializable record. Holds checked-locations list, item-receive index, slot-data caches, progressive levels, unlock booleans, currency queues. Everything that needs to survive a reconnect.
- **`APSave`** — the static façade. Owns the singleton `APSaveData` instance and exposes `Init`, `Flush`, `AddLocationChecked`, `IsLocationChecked`, `IncrementItemIndex`. Other code touches `APSave.saveData.<field>` then calls `APSave.Flush()`.

## Lifecycle

```
Connect to AP server
  ↓
ArchipelagoClient receives LoginSuccessful
  ↓
session.Players.ActivePlayer.Name → playerName
session.RoomState.Seed             → seed
  ↓
APSave.Init(playerName, seed)
  ↓
  ├── exists? → load JSON, restore APSaveData instance
  └── new?    → create empty APSaveData
  ↓
Cache slot-data values (quotaCount, monsterTiersEnabled, …) onto saveData
  ↓
_itemIndex = APSave.saveData.itemReceivedIndex   ← resume from where we left off
  ↓
Coroutines start; incoming items are applied via ItemData.HandleReceivedItem
```

The actual call sites:
- `APSave.Init(playerName, seed)` — [ArchipelagoClient.cs:110](../ContentWarningArchipelago/Core/ArchipelagoClient.cs#L110)
- `APSave.Flush()` — called inline whenever a field on `saveData` is mutated; also automatically inside `AddLocationChecked` and `IncrementItemIndex`

## File layout

Per-seed save file:
```
%LOCALAPPDATA%Low\Landfall Games\Content Warning\archipelago\saves\<slot>___<seed>.json
```

Path is built from `Application.persistentDataPath + "/archipelago/saves/" + Sanitise(playerName) + "___" + Sanitise(seed) + ".json"`. `Sanitise` replaces invalid filename characters with `_`.

The double-underscore separator (`___`) is intentional — single underscores can appear in slot names. Don't change it without coordinating with anyone who has existing saves.

JSON is pretty-printed with `Newtonsoft.Json` at `Formatting.Indented` for hand-debuggability.

## Per-seed keying — why

Multiple Archipelago sessions can share a single Steam account. Different seeds (different multiworld generations) must not stomp each other's state, otherwise reconnecting to seed B applies items intended for seed A. The `(slot, seed)` pair uniquely identifies a generation, so it's the right key.

If you reconnect to the *same* `(slot, seed)`, you get the same save file back — including which locations you've already checked, which items you've already applied (via `itemReceivedIndex`), and all your progressive levels. This is what makes "quit and resume" work.

## When to call `Flush()`

**After every `saveData.<field>` mutation that changes persistent state.** Not before — the `Flush` should reflect the *post-mutation* state. The pattern in `ItemData.HandleReceivedItem`:

```csharp
APSave.saveData.cameraUpgradeLevel++;
APSave.Flush();                                  // persist immediately
ProgressionStatsPatch.ApplyCameraUpgrade(...);   // then apply effect
APNotificationUI.ShowItemReceived(...);
```

Why flush *before* applying the effect: if applying the effect crashes or NREs, the receipt of the item is already on disk. The next reconnect won't try to grant it again (because `itemReceivedIndex` has advanced). Without that ordering, a buggy effect could put you in an infinite loop where every reconnect re-applies the same broken item.

`AddLocationChecked` and `IncrementItemIndex` already call `Flush()` internally, so callers don't need to.

## What belongs in `APSaveData`

**Yes — persist:**
- Anything granted by an AP item that changes long-term player state: progressive levels, boolean unlocks, queues for deferred application.
- Cached AP slot-data values you want to read every frame (so you don't hit `Plugin.connection.slotData.TryGetValue` in a hot path).
- `itemReceivedIndex` — never reset this manually; it's how reconnect-resume works.
- `locationsChecked` — already managed by `AddLocationChecked`.

**No — don't persist (use a runtime field instead):**
- Anything that resets each play session by design (e.g. session-only hat unlocks → tag with `[JsonIgnore]`, see [APSaveData.cs:119](../ContentWarningArchipelago/Core/APSaveData.cs#L119)).
- Derivable state. If `oxygenUpgradeLevel` is enough to compute `maxOxygen`, don't also persist `maxOxygen`. One source of truth.
- Photon room state, Steam lobby state, transient UI state. These have their own lifecycles.

## The `[JsonIgnore]` carveout

Fields tagged `[JsonIgnore]` exist on the in-memory `APSaveData` instance but aren't written to disk. Use this when the *type* of state is "AP-related, lives on saveData for tidiness" but the *intent* is "session-scoped, reset every connect."

Current example: `sessionUnlockedHats` — the hat-shop integration grants hats during a session, but we don't want them persisted across reconnects (they'd accumulate forever). Tagging `[JsonIgnore]` keeps the field on `saveData` (so all the hat code reads from one place) without the storage commitment.

## Adding a new field

1. Add the field to [APSaveData.cs](../ContentWarningArchipelago/Core/APSaveData.cs) with a default value. Default values matter — they're what existing save files (which won't have the new field) deserialize into.
2. If the field shouldn't survive disconnects, add `[JsonIgnore]`.
3. Decide: is this state derived from received items, or from slot data?
   - **Item-derived:** mutate it in the relevant `case` of `ItemData.HandleReceivedItem`, then `Flush()`. See [doc/adding-items-and-locations.md](adding-items-and-locations.md).
   - **Slot-data-derived:** add a `slotData.TryGetValue(...)` block in [ArchipelagoClient.cs](../ContentWarningArchipelago/Core/ArchipelagoClient.cs) right after `APSave.Init` (around lines 113-117). Also call `Flush()` so the cached value is persisted.

## Common save-system pitfalls

- **State silently resets after reconnect.** Field exists in `APSaveData`, mutated in `ItemData.HandleReceivedItem`, but `Flush()` was never called. The next reconnect deserializes the previous on-disk state, losing the mutation.
- **Save file appears empty / contains only one field.** Newtonsoft.Json failed mid-write (disk full, etc.) and left a truncated file. The load path catches this and starts fresh, but you lose all prior state. There's no automatic backup; if you're worried, copy the JSON before risky operations.
- **`itemReceivedIndex` mismatch causes items to apply twice / not at all.** The index is the *count* of items already applied, not the highest item ID seen. Don't reset it unless you know exactly why.
- **Save-file collision between sessions.** Two AP sessions with the same `(slot, seed)` *will* share state — that's the design. Two sessions with different seeds *should not*. If you see state bleeding, check the seed sanitization.
- **Adding a new field with no default value.** Old save files deserialize the field as zero/null. That's fine for `int` (default 0) and `bool` (default false), but a `null` collection in code expecting non-null will NRE. Always initialize collections in the field declaration.

## See also

- [doc/adding-items-and-locations.md](adding-items-and-locations.md) — how new items hook into `APSaveData` and `HandleReceivedItem`
- [doc/networking.md](networking.md) — what doesn't go through `APSaveData` (Mycelium toasts, Photon game state)
