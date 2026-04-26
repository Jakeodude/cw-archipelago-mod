# Content Warning Archipelago

A BepInEx mod that integrates [Content Warning](https://store.steampowered.com/app/2881650/Content_Warning/) into the [Archipelago](https://archipelago.gg) multi-game randomizer. Items in your in-game inventory and shop are gated behind randomized AP item drops; in-game milestones (filming events, shop purchases, hat unlocks, etc.) become AP location checks that send items to other players in the multiworld.

**Status:** Pre-release / work-in-progress (v0.1.0). Single-player and multi-player lobbies both work; full feature coverage is still being expanded.

---

## What this is

Archipelago needs **two pieces** to randomize a game. This repo is the **game-side mod** (C# / BepInEx). The server-side **APWorld** (Python) — which defines item lists, location lists, and randomization rules — is co-developed in the sibling repo [`../cw-apworld/`](../cw-apworld/) and produces a `content_warning.apworld` package that drops into an [Archipelago](https://github.com/ArchipelagoMW/Archipelago) server install.

```
┌──────────────────┐  WebSocket  ┌─────────────────────┐
│ This mod (C#)    │ ◀────────▶ │ Archipelago server  │
│ in Content       │            │ (with CW APWorld)   │
│ Warning game     │            │                     │
└────────┬─────────┘            └─────────────────────┘
         │ Mycelium RPC (Steam P2P)
         ▼
┌──────────────────┐
│ Other players in │
│ the same lobby   │
└──────────────────┘
```

---

## Related repositories

Two sibling repos on the same machine make up the rest of the project. **An AI working in this repo should know they exist and when to consult them.**

| Local path | What it is | When to consult |
|---|---|---|
| [`../cw-apworld/`](../cw-apworld/) | The **Content Warning APWorld** (Python) — server-side counterpart to this mod. Defines items, locations, options, and randomization rules. Build output is `content_warning.apworld`. | Any change here that adds/renames/removes an item, location, or YAML option **must** land in `cw-apworld` too. IDs and names must match exactly across both sides — base ID is `98765000` (`item_base_id` in the APWorld's `items.py`). The README there is user-facing (install + YAML); the Python lives under `ap_world/` (`items.py`, `locations.py`, `options.py`, etc.). |
| [`../cw-reference/cw-reference/`](../cw-reference/cw-reference/) | **Read-only** reference workspace for CW modding. Contains the full decompiled game source (`ContentWarning_Source/`), curated topic slices (`Game_Ref/` — Battery, Shop, Hats, Monster_Data, etc.), and example CW mods (`Example_mods/`). Note the doubly-nested folder. | Game-internals lookups ("where is class X defined", "what does method Y do"), mod-shape templates, and topic-scoped reading. The [top-level README](../cw-reference/cw-reference/README.md) is the AI entry point — its **§6 system map** and **§7 task→files table** are the primary lookups when planning a patch. **Do not edit anything in this repo** — it's decompiled output, regenerated from DLLs. |

The complete project deliverable is two files: this repo's `ContentWarningArchipelago.dll` plus `cw-apworld`'s `content_warning.apworld`.

---

## Requirements

| Component | Version | Purpose |
|---|---|---|
| Content Warning | Steam release | The game itself |
| [BepInEx 5](https://github.com/BepInEx/BepInEx) | 5.4.x | Plugin loader |
| [Mycelium Networking for CW](https://github.com/RugbugRedfern/Mycelium-Networking-For-Content-Warning) | 1.0.14+ | Steam-P2P RPC for cross-player AP notifications |
| Archipelago server | 0.4+ | Hosts the multiworld session |

`Archipelago.MultiClient.Net` (the C# AP client library) is a NuGet dependency and ships inside the built mod DLL.

---

## Installation (end users)

1. Install BepInEx 5 to your Content Warning folder.
2. Drop both DLLs into `Content Warning/BepInEx/plugins/`:
   - `ContentWarningArchipelago.dll` (this mod)
   - `MyceliumNetworkingForCW.dll`
3. Launch the game once so BepInEx generates the config file at `BepInEx/config/automagic.cw-archipelago.cfg`. Edit it to set your AP server address, port, slot name, and password.
4. Re-launch and click **Connect to Archipelago** in the main menu panel.

The recommended way to manage CW mods is [r2modman](https://thunderstore.io/c/content-warning/p/ebkr/r2modman/), which resolves the Mycelium dependency automatically.

---

## Building from source

### Prerequisites
- **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10` on Windows).
- Content Warning installed at `C:\Program Files (x86)\Steam\steamapps\common\Content Warning` (override via the `<GamePath>` property in [ContentWarningArchipelago.csproj](ContentWarningArchipelago/ContentWarningArchipelago.csproj) if installed elsewhere).

### Build
```
dotnet build ContentWarningArchipelago/ContentWarningArchipelago.csproj
```

The post-build target ([csproj:122](ContentWarningArchipelago/ContentWarningArchipelago.csproj#L122)) copies `ContentWarningArchipelago.dll` directly into the game's `BepInEx/plugins` folder. **Mycelium is not auto-copied** — install it once by hand or via r2modman.

### Build outputs
- `ContentWarningArchipelago/bin/Debug/netstandard2.1/ContentWarningArchipelago.dll` — the built mod
- `MyceliumNetworkingForCW.dll` is also produced in the same folder as a NuGet-restored compile-time reference (it's the same DLL the runtime plugin ships, so you can copy it from there if you don't have r2modman handy)

---

## Project layout

```
ContentWarningArchipelago/
├── Plugin.cs                      BepInEx entry point. Awake() wires everything up.
├── ContentWarningArchipelago.csproj  Build config, package refs, post-build copy
│
├── Core/
│   ├── ArchipelagoClient.cs       AP server connection + 3 coroutine handlers
│   ├── APConfig.cs                BepInEx-bound persistent connection settings
│   └── APSaveData.cs              Per-seed save state (received items, etc.)
│
├── Data/
│   ├── ItemNames.cs               Item name ↔ AP id mapping (matches APWorld items.py)
│   ├── ItemData.cs                HandleReceivedItem() — single dispatch for incoming AP items
│   ├── LocationNames.cs           Location name ↔ AP id mapping
│   ├── LocationData.cs            Location metadata
│   └── FilmingLocationData.cs     Maps content-event entity IDs to AP location names
│
├── Patches/                       Harmony patches — auto-discovered by Plugin.Awake's PatchAll()
│   ├── MainMenuAPPatch.cs         Injects the AP connection panel into the title screen
│   ├── ModManagerAPPatch.cs       Same, but for in-game Mod Manager UI
│   ├── ItemPickupPatch.cs         Shop purchases, pickups, and filming events → AP location checks
│   │                              (also defines ContentEvaluatorPatch for ContentEvaluator.EvaluateRecording)
│   ├── HatShopPatch.cs            Hat shop integration with AP item gating
│   ├── MetaProgressionHatPatch.cs Hat unlocks via AP MetaCoin items
│   ├── DivingBellAPStatusPatch.cs Shows AP status overlay on the diving bell HUD
│   ├── DivingBellRechargePatch.cs Battery charging tied to AP DivingBellCharger item
│   ├── OxygenPatch.cs             Oxygen-cap item + scaling
│   ├── ProgressionStatsPatch.cs   Progressive stamina / camera upgrades
│   ├── MoneyPatch.cs              AP money items → ShopHandler.AddMoney
│   ├── ViewsMultiplierPatch.cs    Views multiplier item effect
│   ├── TrapPatches.cs             AP trap items → in-game effects (Virality-style RPC dispatch)
│   └── LateJoinSyncPatch.cs       Sync late-joining players' AP state
│
└── UI/
    ├── APConnectionPanelUI.cs     Connection panel (status label, address field, Connect button)
    └── APNotificationUI.cs        HUD toast for "location found" / "item received"
```

---

## How it works (architecture overview)

### Plugin lifecycle
[Plugin.cs](ContentWarningArchipelago/Plugin.cs) is the BepInEx entry point.

| Phase | Action |
|---|---|
| `Awake()` | Bind logger; load config; init `ItemData` & `LocationData`; `Harmony.PatchAll()`; register with Mycelium |
| `Start()` | Construct the singleton `ArchipelagoClient` (no connection attempt yet) |
| `Update()` | Tick the three AP coroutines: `checkItemsReceived`, `incomingItemHandler`, `outgoingCheckHandler` |

`[BepInDependency("RugbugRedfern.MyceliumNetworking")]` ensures Mycelium loads first — its logger must be initialized before `RegisterNetworkObject` runs.

### Three networking layers

| Layer | Library | Used for |
|---|---|---|
| **AP server** | `Archipelago.MultiClient.Net` (WebSocket) | Receive items, send location checks, slot-data on connect |
| **Cross-player** | Mycelium (Steam P2P) | "Location found" toasts to other players in the lobby |
| **Game-internal** | Photon (vanilla CW) | Game state, pickups, master-client item grants — left untouched by the mod where possible |

### Sending a location check
A Harmony patch detects an in-game milestone → calls `Plugin.SendCheck(locationId)` → `ArchipelagoClient.ActivateCheck()` queues it on the outgoing coroutine → the AP server credits the check and dispatches the corresponding item to whichever player is supposed to receive it.

### Receiving an AP item
The AP server pushes an item over WebSocket → `incomingItemHandler` coroutine pulls it off the queue on the main thread → `ItemData.HandleReceivedItem(itemId)` switches on the item type → updates `APSave.saveData`, applies the in-game effect (oxygen cap, stamina, trap, money, hat unlock, etc.), shows a HUD toast, and `APSave.Flush()` persists the new state.

### Cross-player notifications
When a player completes a check, `ArchipelagoClient.ActivateCheck` calls `MyceliumNetwork.RPC` to invoke `Plugin.LocationFound(locName, info)` on every other client in the Steam lobby. Self-echoes are filtered via `info.SenderSteamID`.

---

## Configuration

Configuration lives in `BepInEx/config/automagic.cw-archipelago.cfg`, generated on first launch. Editable in-game via the connection panel. Keys:

- `address` — AP server hostname (default `archipelago.gg`)
- `port` — AP server port (default `38281`)
- `slot` — your slot name in the multiworld
- `password` — server password (optional)

---

## Save data

Per-seed saves live at:
```
%LOCALAPPDATA%Low\Landfall Games\Content Warning\archipelago\saves\<slot>___<seed>.json
```

The seed identifier (`Plugin.connection.session.RoomState.Seed`) keys the save, so multiple AP sessions on different seeds don't collide. Re-connecting to the same seed re-loads the same save.

---

## Credits

- **[Archipelago](https://github.com/ArchipelagoMW/Archipelago)** — the multi-game randomizer framework.
- **[Archipelago.MultiClient.Net](https://github.com/ArchipelagoMW/Archipelago.MultiClient.Net)** — the C# AP client library.
- **[Mycelium Networking for Content Warning](https://github.com/RugbugRedfern/Mycelium-Networking-For-Content-Warning)** by RugbugRedfern — Steam P2P RPC layer used for cross-player AP toasts.
- **[BepInEx](https://github.com/BepInEx/BepInEx)** — the plugin loader that hosts this mod.
- Several existing CW mods served as references for specific patterns (named in the source-file headers): R.E.P.O. Archipelago Client Mod (overall structure), BetterOxygen (config + oxygen scaling), StartingBudget (money grants), BetterDivingBellUI (HUD overlay pattern).

Built by [Jakeodude](https://github.com/Jakeodude).

---

## License

Not yet specified. Add a `LICENSE` file before public distribution.
