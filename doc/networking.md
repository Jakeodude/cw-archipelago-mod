# Networking architecture

The mod talks across three independent network layers, each with its own purpose and protocol. Mixing them up is a common source of bugs, so this doc spells out which layer to use for what — and the gotchas we've already hit so you don't hit them again.

## The three layers

| Layer | Library | Transport | Used for |
|---|---|---|---|
| **AP server** | [`Archipelago.MultiClient.Net`](https://github.com/ArchipelagoMW/Archipelago.MultiClient.Net) (NuGet) | WebSocket to `archipelago.gg:<port>` | Receive items from the multiworld; send location checks; fetch slot data; ping for connection liveness |
| **Cross-player AP** | [Mycelium Networking for CW](https://github.com/RugbugRedfern/Mycelium-Networking-For-Content-Warning) | Steam P2P (Steam Datagram) | Broadcast "<player> found <location>" toasts to everyone in the same Steam lobby |
| **Game state** | Photon (vanilla CW) | Photon Cloud | Player movement, item pickups, master-client RPCs — *the game's own networking, mostly untouched by the mod* |

### When to use which

- **You need to talk to the AP server (send a check, react to a received item):** layer 1, via `Plugin.SendCheck(...)` or `ArchipelagoClient.session.*`. Don't reinvent — the existing coroutine handlers (`checkItemsReceived`, `incomingItemHandler`, `outgoingCheckHandler`) handle everything end-to-end.
- **You need to tell other lobby members about an AP-side event** (a check just fired, a player just got a special item, etc.): layer 2 — Mycelium RPC. See `Plugin.LocationFound` ([Plugin.cs:118-128](../ContentWarningArchipelago/Plugin.cs#L118-L128)) for the canonical pattern.
- **You need to react to vanilla game state** (a Photon-broadcast event the game itself raises, e.g. `RPCA_SpawnDrone`): layer 3 — Harmony-patch the relevant method. Don't replace Photon with Mycelium for game-internal traffic; that's a deep, pointless rewrite.

The lines are clear, but the mistake we already made is trying to stuff cross-player AP toasts through Photon's `RaiseEvent`. That's what Mycelium replaced — Photon's free tier has bandwidth caps, and AP toasts are a steady drip of small messages that's a much better fit for Steam's free P2P transport.

## Mycelium load-order rule (non-negotiable)

[Plugin.cs:18](../ContentWarningArchipelago/Plugin.cs#L18) declares:

```csharp
[BepInDependency("RugbugRedfern.MyceliumNetworking")]
```

**Don't remove this.** Without it, BepInEx loads plugins alphabetically and `ContentWarningArchipelago` runs before `MyceliumNetworkingForCW`. Our `Awake()` calls `MyceliumNetwork.RegisterNetworkObject(...)`, which internally calls `RugLogger.Log(...)`, which dereferences `RugLogger.logSource` — set in *Mycelium's* own `Awake()`. Result: a NullReferenceException in our `Awake()`, which causes Unity to skip our `Start()`, which leaves `Plugin.connection` null, which makes the Connect button NRE. We hit this exact bug — see commit `42b197b`.

`[BepInDependency]` forces Mycelium to load first. As a bonus, if Mycelium is missing entirely BepInEx refuses to load us with a clear "missing dependency" error instead of a cryptic NRE.

## Mycelium RPC handler pattern

When you need to fan out an AP-side event to other lobby members, follow this shape:

```csharp
[CustomRPC]
internal void LocationFound(string locName, RPCInfo info)
{
    if (info.SenderSteamID != SteamUser.GetSteamID())
    {
        APNotificationUI.ShowLocationFound(locName);
    }
}
```

Three things to know:

### 1. `internal`, not `private`

The method has to be at least `internal` so the call site (in `ArchipelagoClient.cs`) can reference it via `nameof(Plugin.LocationFound)`. `nameof` requires the symbol to be visible from the use site. Mycelium itself uses reflection to find the method, so visibility doesn't matter for *runtime* — only for `nameof`.

### 2. CSteamID equality, not `m_SteamID` ulong

```csharp
if (info.SenderSteamID != SteamUser.GetSteamID())     // ✅ both CSteamID
if (info.SenderSteamID != SteamUser.GetSteamID().m_SteamID)  // ❌ CSteamID != ulong
```

`CSteamID` defines its own `==` / `!=` operators, so compare CSteamID to CSteamID directly. Adding `.m_SteamID` to one side gives a type mismatch and a CS0019 compile error. We hit this on the Mycelium swap — see commit `e4e1370`.

### 3. Self-RPC echo filter is required

`MyceliumNetwork.RPC` sends to **all** members in `Players[]`, including the local player. Without the `info.SenderSteamID != SteamUser.GetSteamID()` filter, the firing player gets two notifications: one from the direct call in their own `ArchipelagoClient.ActivateCheck`, and one from the RPC echo bouncing back through Mycelium.

## Mycelium runtime install

The NuGet `RugbugRedfern.MyceliumNetworking.CW` reference in [csproj:36](../ContentWarningArchipelago/ContentWarningArchipelago.csproj#L36) gives us *compile-time* types only. At runtime, **Mycelium itself is a separate BepInEx plugin** (`MyceliumNetworkingForCW.dll`) that needs to live in the game's `BepInEx/plugins/` folder alongside our DLL.

Symptoms when Mycelium isn't installed:
- BepInEx log shows `1 plugin to load` (instead of `2`).
- Our plugin fails to load with `[BepInDependency]` error.
- (Without `[BepInDependency]`, you'd instead get the NRE described in the load-order section.)

Either install via [r2modman](https://thunderstore.io/c/content-warning/p/ebkr/r2modman/) (recommended) or copy `MyceliumNetworkingForCW.dll` from the build output (`ContentWarningArchipelago/bin/Debug/netstandard2.1/`) — the NuGet package's bundled DLL is the same plugin that ships standalone via Thunderstore.

## Photon coexistence

The mod runs alongside vanilla Photon networking, not instead of it. Some patches still use Photon — and that's fine:

- **Master-client guards.** Many game events are broadcast via Photon to all clients, so a Harmony patch fires on every player. Use `if (!PhotonNetwork.IsMasterClient) return;` to ensure only the host fires the AP check, then optionally fan out via Mycelium so non-hosts get toasts too.
- **Photon RPCs we patch into.** `ShopHandler.RPCA_SpawnDrone`, `Pickup.RPC_RequestPickup`, etc. — these are Photon RPCs we *patch*, not Photon traffic we generate. We're piggybacking on the game's own networking to detect events.
- **`PhotonNetwork.InRoom` checks.** Used in [ArchipelagoClient.cs](../ContentWarningArchipelago/Core/ArchipelagoClient.cs) before sending Mycelium broadcasts as a cheap "are we in a multiplayer session" check. (Mycelium also no-ops outside a Steam lobby, but the Photon check short-circuits earlier.)

If you find yourself wanting to add a *new* Photon `RaiseEvent`, stop and ask whether Mycelium fits better. The remaining Photon usage is reactive (we observe / hook), not generative (we don't broadcast anything new through Photon ourselves).

## Common networking pitfalls

- **NRE in `Awake()` because Mycelium isn't ready.** See "Mycelium load-order rule" above. Always declare `[BepInDependency]`.
- **Method invisible to `nameof`.** RPC handler is `private`. Make it `internal`.
- **Double notifications on the firing player.** Missing self-filter in the `[CustomRPC]` handler.
- **Mycelium types compile but runtime fails with `TypeLoadException` or `MissingMethodException`.** Mycelium NuGet version doesn't match the installed plugin DLL. Pin the NuGet version to match the Thunderstore release if this happens.
- **Trying to send game state through Mycelium.** Don't. Photon already handles game state. Mycelium is for AP-side cross-player chatter only.
- **Forgetting `IsMasterClient` guard on a Photon-RPC patch.** N players → N AP checks per event. The AP server will deduplicate by location ID, but it's wasted bandwidth and confuses the logs.

## See also

- [doc/adding-items-and-locations.md](adding-items-and-locations.md) — items and locations include the patch patterns that touch each network layer
- [doc/save-system.md](save-system.md) — what AP-side state needs to survive disconnect/reconnect
- The Mycelium source at `E:/code/Mycelium-Networking-For-Content-Warning` — read [`MyceliumNetwork.cs`](file:///E:/code/Mycelium-Networking-For-Content-Warning/MyceliumNetworkingForCW/MyceliumNetwork.cs) when in doubt about an API contract
