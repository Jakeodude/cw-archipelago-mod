# CLAUDE.md — Project rails for AI sessions

This file is auto-loaded by Claude Code at session start. Read [README.md](README.md) for the full project tour and [doc/](doc/) for topical deep-dives.

## What this project is, in one paragraph

A BepInEx C# mod for *Content Warning* that integrates the game with the [Archipelago](https://archipelago.gg) multi-game randomizer. The mod connects to an Archipelago server over WebSocket, fires location checks when in-game milestones happen, applies received items to the local game state, and broadcasts "location found" toasts to other Steam-lobby members via Mycelium P2P.

## Build & deploy

```
"/c/Program Files/dotnet/dotnet.exe" build ContentWarningArchipelago/ContentWarningArchipelago.csproj
```

Requires the .NET 10 SDK (already installed on this machine). The post-build target ([csproj:122](ContentWarningArchipelago/ContentWarningArchipelago.csproj#L122)) copies the built DLL to `C:\Program Files (x86)\Steam\steamapps\common\Content Warning\BepInEx\plugins\`. **Mycelium is *not* auto-copied** — `MyceliumNetworkingForCW.dll` must already be in that plugins folder for the mod to load.

After any code change: build, then if possible smoke-test in-game with two Steam clients in the same lobby.

## The two-piece architecture (read this before adding items/locations)

Archipelago needs **both** halves to work:

1. **This mod** (C#, runs in the game) — handles client-side: fires checks, applies items, shows UI.
2. **The APWorld** (Python, runs on the AP server) — defines item lists, location lists, randomization rules. Co-developed in sibling repo [`../cw-apworld/`](../cw-apworld/); build output is `content_warning.apworld`. Python lives under `ap_world/` (`items.py`, `locations.py`, `options.py`).

If you add or rename an item/location here, the matching change must land in the APWorld too. **IDs and names must match exactly.** The base ID is `98765000` (`item_base_id` in the APWorld's `items.py`), and each item/location uses a fixed offset from there.

See [doc/adding-items-and-locations.md](doc/adding-items-and-locations.md) for the full procedure.

## No-touch / careful-touch zones

- **Vanilla CW Photon code** — the mod uses Photon only when it has to (master-client item grants, accessing `PhotonNetwork.IsMasterClient` for guards). Don't replace Photon with Mycelium for game-state networking — that's a deep rewrite. Mycelium is for our cross-player AP toasts only.
- **Plugin singleton lifetime** — `Plugin.Instance` is set in `Awake()` and lives for the process. Static helpers (e.g. `Patches.TrapHandler`) hold no MonoBehaviour reference of their own and call `Plugin.Instance.StartCoroutine(...)`. Don't break this assumption.
- **Mycelium load order** — `[BepInDependency("RugbugRedfern.MyceliumNetworking")]` on [Plugin.cs:18](ContentWarningArchipelago/Plugin.cs#L18) is non-negotiable. Without it, `Awake()` NREs because Mycelium's logger isn't initialized yet. See [doc/networking.md](doc/networking.md).
- **Mycelium runtime install** — adding a NuGet reference is not enough; the Mycelium DLL must also be in the game's `BepInEx/plugins/` folder. The csproj's NuGet copy puts it in the build output but does *not* deploy it.

## Logging conventions

Every log line uses a bracketed component tag prefix so multiline grep stays useful:

| Prefix | Used by |
|---|---|
| `[CWArch]` | `Plugin.cs` lifecycle |
| `[AP]` | `ArchipelagoClient` (server connect/disconnect, send/receive) |
| `[APSave]` | `APSaveData.cs` |
| `[APPanel]` | `APConnectionPanelUI.cs` |
| `[<PatchName>]` | each Harmony patch (e.g. `[ShopBuyPatch]`, `[MainMenuAPPatch]`) |

Use `Plugin.Logger.LogInfo/LogDebug/LogWarning/LogError`. `Plugin.Logger` is a `ManualLogSource`, set in `Awake()`.

## Where logs land

| File | Source |
|---|---|
| `BepInEx/LogOutput.log` | BepInEx + plugin logs (this is the primary log to inspect) |
| `Content Warning_Data/output_log.txt` or `%LOCALAPPDATA%Low\Landfall Games\Content Warning\Player.log` | Unity engine + uncaught exception stack traces |

When debugging an `Awake()` failure: the *Unity* log usually has the stack trace; the BepInEx log just stops without explanation. Always check both.

## Conventions for adding new patches

- Drop the file in [ContentWarningArchipelago/Patches/](ContentWarningArchipelago/Patches/). `Harmony.PatchAll()` in `Plugin.Awake` discovers anything decorated with `[HarmonyPatch]` automatically — no manual registration.
- Multiple `[HarmonyPatch]` classes in one file is fine and common (see [ItemPickupPatch.cs](ContentWarningArchipelago/Patches/ItemPickupPatch.cs) — three patch classes share a file because they hook the same purchase/pickup/filming pipeline).
- Use `AccessTools` for type/method resolution rather than direct `typeof(GameType).GetMethod(...)` — patches gracefully no-op if the game renames a target.
- Master-client guards: if a patch reacts to a Photon event broadcast to all clients, guard with `if (!PhotonNetwork.IsMasterClient) return;` so only the host fires the AP check (one check per event, not one per player).
- For RPC fan-out to other players: use `Plugin.SendCheck(locationId)` + Mycelium `MyceliumNetwork.RPC` for AP-side broadcasts, not `PhotonNetwork.RaiseEvent`. (Pre-existing patches that still use Photon for in-game game state are fine — don't change them unless you have a reason.)

## Common entry points

| Need | Use |
|---|---|
| Fire a location check | `Plugin.SendCheck(locationId)` |
| Read AP slot data | `Plugin.connection.slotData["key"]` |
| Read/write persistent AP state | `APSave.saveData.<field>` then `APSave.Flush()` |
| Trigger an in-game effect from a received item | Add a case in `ItemData.HandleReceivedItem` ([Data/ItemData.cs](ContentWarningArchipelago/Data/ItemData.cs)) |
| Show a HUD toast | `APNotificationUI.ShowLocationFound("name")` or `APNotificationUI.ShowItemReceived("name")` |

## When in doubt

- Don't invent file names or APIs — grep first. The codebase is small enough (~20 source files) to walk end-to-end in one pass.
- Don't add backwards-compat shims, dead-code preservation, or speculative abstractions. The mod is at v0.1.0; ship simple changes.
- The reference mods named in source-file headers (R.E.P.O. Archipelago Client Mod, BetterOxygen, StartingBudget, BetterDivingBellUI) are good places to look when you need a new pattern that this codebase doesn't already use.
- For game-internals lookups (where a CW class is defined, what a method does) and additional mod-pattern examples, consult **read-only** sibling repo [`../cw-reference/cw-reference/`](../cw-reference/cw-reference/) — decompiled CW source + curated topic slices + example mods. Its [top-level README](../cw-reference/cw-reference/README.md) §6 system map and §7 task→files table are the primary lookups. Don't edit anything there.
