// Core/ArchipelagoClient.cs
// Manages the Archipelago.MultiClient.Net session for Content Warning.
// Modelled after R.E.P.O.-Archipelago-Client-Mod/Core/ArchipelagoConnection.cs.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Packets;
using ContentWarningArchipelago.Data;
using ContentWarningArchipelago.UI;
using HarmonyLib;
using MyceliumNetworking;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using Zorro.Core;

namespace ContentWarningArchipelago.Core
{
    public class ArchipelagoClient
    {
        // ------------------------------------------------------------------ public state

        /// <summary>The live AP session; null when disconnected.</summary>
        public ArchipelagoSession? session { get; private set; }

        /// <summary>True while the WebSocket is open and authenticated.</summary>
        public bool connected => session?.Socket.Connected ?? false;

        /// <summary>Slot data dictionary returned by the AP server on login.</summary>
        public Dictionary<string, object>? slotData { get; private set; }

        // Coroutine handles — ticked every frame by Plugin.Update().
        public IEnumerator<bool>? checkItemsReceived   { get; private set; }
        public IEnumerator<bool>? incomingItemHandler  { get; private set; }
        public IEnumerator<bool>? outgoingCheckHandler { get; private set; }

        // ------------------------------------------------------------------ private state

        /// <summary>Next index into session.Items.AllItemsReceived to process.</summary>
        private int _itemIndex = 0;

        /// <summary>Items queued for in-game application (thread-safe).</summary>
        private ConcurrentQueue<(ItemInfo item, int index)> _incomingItems = new();

        /// <summary>Location IDs queued to be sent to the server.</summary>
        private ConcurrentQueue<long> _pendingChecks = new();

        /// <summary>The DataStorage key (Slot scope) that holds the lobby-shared
        /// Meta Coin balance for this AP slot.  Empty when disconnected.
        /// See <see cref="InitMetaCoinsDataStorage"/> for the bootstrap flow.</summary>
        public string MetaCoinsKey { get; private set; } = string.Empty;

        /// <summary>Cached DataStorageElement so the OnValueChanged handler
        /// can be unhooked on disconnect (the indexer returns a fresh element
        /// each call, so we must keep the same reference we subscribed on).</summary>
        private DataStorageElement? _metaCoinsElement;

        /// <summary>The Delegate we registered on
        /// <see cref="DataStorageElement.OnValueChanged"/>.  Stored as a
        /// non-generic <see cref="Delegate"/> because the event's delegate
        /// signature uses Newtonsoft.Json.Linq.JToken — a type defined in
        /// an assembly version (11.0.0.0) that we deliberately avoid
        /// spelling in this project's compile units to dodge a NuGet
        /// version conflict between AP.MultiClient.Net and CW.GameLibs.Steam.
        /// We build the handler via reflection in
        /// <see cref="InitMetaCoinsDataStorage"/> instead.</summary>
        private Delegate? _metaCoinsHandler;

        /// <summary>Reflection handle for <c>MetaProgressionHandler.metaCoins</c>
        /// (private int).  Set on first use; reused for every DS-driven write
        /// so the per-frame UI poll picks up the new balance immediately.</summary>
        private static FieldInfo? _metaCoinsField;

        // ================================================================== CONNECTION

        /// <summary>
        /// Attempt to connect and log in to the Archipelago server.
        /// Fire-and-forget — await this from a non-async context via `_ = TryConnect(...)`.
        /// </summary>
        public async Task TryConnect(string address, int port, string password, string slot)
        {
            if (connected)
            {
                Plugin.Logger.LogDebug("[AP] Already connected — skipping.");
                return;
            }

            TryDisconnect(); // clean up any stale session

            Plugin.Logger.LogInfo($"[AP] Connecting to {address}:{port} as '{slot}'…");

            try
            {
                session = ArchipelagoSessionFactory.CreateSession(address, port);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[AP] Failed to create session: {ex.Message}");
                return;
            }

            // ---- Connect ----
            LoginResult result;
            try
            {
                await session.ConnectAsync();
                result = await session.LoginAsync(
                    game:                "Content Warning",
                    name:                slot,
                    itemsHandlingFlags:  ItemsHandlingFlags.AllItems,
                    requestSlotData:     true,
                    password:            string.IsNullOrEmpty(password) ? null : password
                );
            }
            catch (Exception ex)
            {
                result = new LoginFailure(ex.GetBaseException().Message);
            }

            // ---- Handle result ----
            if (result is LoginSuccessful success)
            {
                slotData = success.SlotData;

                string playerName = session.Players.ActivePlayer.Name;
                string seed       = session.RoomState.Seed;

                Plugin.Logger.LogInfo($"[AP] Connected!  Player={playerName}  Seed={seed}");

                // Initialise (or load) the per-slot save file.
                APSave.Init(playerName, seed);

                // Cache slot-data values we care about.
                if (slotData.TryGetValue("quota_count", out var qc))
                    APSave.saveData.quotaCount = Convert.ToInt32(qc);

                if (slotData.TryGetValue("monster_tiers", out var mt))
                    APSave.saveData.monsterTiersEnabled = Convert.ToBoolean(mt);

                // ---- Resync items the server already sent while we were offline ----
                _itemIndex = APSave.saveData.itemReceivedIndex;

                // Start frame-tick coroutines.
                _incomingItems  = new ConcurrentQueue<(ItemInfo, int)>();
                _pendingChecks  = new ConcurrentQueue<long>();

                checkItemsReceived  = CheckItemsReceived();
                incomingItemHandler = IncomingItemHandler();
                outgoingCheckHandler = OutgoingCheckHandler();

                // ---- Bind the lobby-shared Meta Coins DataStorage key ----
                InitMetaCoinsDataStorage();
            }
            else
            {
                var failure = (LoginFailure)result;
                string msg = "[AP] Login failed:\n";
                foreach (string err in failure.Errors)     msg += $"  • {err}\n";
                foreach (var   err in failure.ErrorCodes)  msg += $"  • {err}\n";
                Plugin.Logger.LogWarning(msg);

                TryDisconnect();
            }
        }

        // ------------------------------------------------------------------ DISCONNECT

        public void TryDisconnect()
        {
            try
            {
                // Drop the Meta Coins DS listener before tearing the session
                // down — the OnValueChanged delegate captures `this`, so a
                // dangling subscription would keep us alive across reconnects.
                if (_metaCoinsElement != null && _metaCoinsHandler != null)
                {
                    UnsubscribeMetaCoinsListener(_metaCoinsElement, _metaCoinsHandler);
                }
                _metaCoinsElement = null;
                _metaCoinsHandler = null;
                MetaCoinsKey      = string.Empty;

                if (session != null)
                {
                    _ = session.Socket.DisconnectAsync();
                    session = null;
                }

                slotData             = null;
                _itemIndex           = 0;
                checkItemsReceived   = null;
                incomingItemHandler  = null;
                outgoingCheckHandler = null;
                _incomingItems       = new ConcurrentQueue<(ItemInfo, int)>();
                _pendingChecks       = new ConcurrentQueue<long>();

                Plugin.Logger.LogInfo("[AP] Disconnected.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[AP] Error while disconnecting: {ex.Message}");
            }
        }

        // ================================================================== LOCATION CHECKS

        /// <summary>
        /// Send a location check to the server and record it locally.
        /// Safe to call from any context; actual network call is async.
        /// </summary>
        public void ActivateCheck(long locationId)
        {
            if (!connected)
            {
                Plugin.Logger.LogWarning($"[AP] ActivateCheck called while disconnected (id={locationId}).");
                return;
            }

            if (APSave.IsLocationChecked(locationId))
            {
                Plugin.Logger.LogDebug($"[AP] Location {locationId} already checked — skipping.");
                return;
            }

            string locName = LocationData.GetName(locationId);
            Plugin.Logger.LogInfo($"[AP] Checking location: {locName} ({locationId})");

            // Show a HUD notification so the local player sees feedback when a check fires.
            APNotificationUI.ShowLocationFound(locName);

            // Broadcast the location-found notification to all OTHER players in the lobby
            // so everyone sees who found a check (surface or underground).
            // Uses Mycelium RPC instead of Photon to avoid bandwidth costs.
            if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.PlayerCount > 1)
            {
                try
                {
                    MyceliumNetworking.MyceliumNetwork.RPC(
                        Plugin.MyceliumModId,
                        nameof(Plugin.LocationFound),
                        MyceliumNetworking.ReliableType.Reliable,
                        locName);
                    Plugin.Logger.LogDebug(
                        $"[AP] Broadcast location-found notification for '{locName}' to other players.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning(
                        $"[AP] Mycelium RPC for location notification failed: {ex.Message}");
                }
            }

            // Record locally first so we never double-send even if the server call throws.
            APSave.AddLocationChecked(locationId);

            // Fire-and-forget to the server.
            _ = session!.Locations.CompleteLocationChecksAsync(locationId);
        }

        // ================================================================== COMPLETION

        /// <summary>
        /// Notify the AP server that this player has achieved their goal.
        /// Call once when "Viral Sensation" (the win condition) is triggered.
        /// </summary>
        public void SendCompletion()
        {
            if (!connected) return;
            Plugin.Logger.LogInfo("[AP] Sending goal completion.");
            session!.Socket.SendPacket(new StatusUpdatePacket
            {
                Status = ArchipelagoClientState.ClientGoal
            });
        }

        // ================================================================== COROUTINES (ticked by Plugin.Update)

        /// <summary>Polls AllItemsReceived and enqueues new items for in-game processing.</summary>
        private IEnumerator<bool> CheckItemsReceived()
        {
            while (connected)
            {
                if (session!.Items.AllItemsReceived.Count > _itemIndex)
                {
                    ItemInfo item = session.Items.AllItemsReceived[_itemIndex];
                    Plugin.Logger.LogDebug($"[AP] Queuing item idx={_itemIndex}: {item.ItemName}");
                    _incomingItems.Enqueue((item, _itemIndex));
                    _itemIndex++;
                }
                yield return true;
            }
        }

        /// <summary>Dequeues items and calls ItemData.HandleReceivedItem for each.</summary>
        private IEnumerator<bool> IncomingItemHandler()
        {
            while (connected)
            {
                if (!_incomingItems.TryPeek(out var pending))
                {
                    yield return true;
                    continue;
                }

                // Skip items we already applied in a previous session.
                if (APSave.saveData.itemReceivedIndex > pending.index)
                {
                    _incomingItems.TryDequeue(out _);
                    Plugin.Logger.LogDebug($"[AP] Skipping already-processed item idx={pending.index}.");
                    yield return true;
                    continue;
                }

                // Resolve the sender's display name.  For items from our own world the
                // sender slot is ourself; we pass an empty string so the notification
                // just shows the item name without a "from" line.
                string senderName = string.Empty;
                try
                {
                    string localName = session?.Players.ActivePlayer.Name ?? string.Empty;
                    string srcName   = pending.item.Player.Name;
                    if (!string.Equals(srcName, localName, StringComparison.OrdinalIgnoreCase))
                        senderName = srcName;
                }
                catch { /* sender resolution is best-effort */ }

                Plugin.Logger.LogInfo(
                    $"[AP] Applying item: {pending.item.ItemName} (id={pending.item.ItemId})" +
                    (string.IsNullOrEmpty(senderName) ? "" : $" from '{senderName}'"));
                ItemData.HandleReceivedItem(pending.item.ItemId, senderName);
                APSave.IncrementItemIndex();
                _incomingItems.TryDequeue(out _);

                yield return true;
            }
        }

        /// <summary>
        /// Drains the pending-checks queue and sends them to the server.
        /// (Checks enqueued via EnqueueCheck() are sent here on the next frame.)
        /// </summary>
        private IEnumerator<bool> OutgoingCheckHandler()
        {
            while (connected)
            {
                if (_pendingChecks.TryDequeue(out long locId))
                {
                    ActivateCheck(locId);
                }
                yield return true;
            }
        }

        // ================================================================== HELPERS

        /// <summary>
        /// Thread-safe alternative to ActivateCheck when calling from a non-main thread.
        /// The check will be dispatched on the next Update() tick.
        /// </summary>
        public void EnqueueCheck(long locationId) => _pendingChecks.Enqueue(locationId);

        public string GetLocationName(long id) =>
            session?.Locations.GetLocationNameFromId(id) ?? LocationData.GetName(id);

        public string GetItemName(long id) =>
            session?.Items.GetItemName(id) ?? ItemData.GetName(id);

        // ================================================================== META COINS (DataStorage)

        /// <summary>
        /// Set up the lobby-shared Meta Coin balance.  Called once after a
        /// successful login (per <see cref="TryConnect"/>).
        ///
        /// <para>
        /// All clients in the lobby share a single AP slot, so they all bind
        /// to the same <c>CW_MetaCoins_{slot}</c> key.  Calling
        /// <see cref="DataStorageElement.Initialize(JToken)"/> from every
        /// client is safe — AP only writes the default if the key is absent.
        /// First-join therefore lands at 0 (overriding the player's vanilla
        /// MC balance), and subsequent connects pull whatever the lobby has
        /// spent or accumulated so far.
        /// </para>
        ///
        /// <para>
        /// We subscribe <see cref="OnMetaCoinsChanged"/> on the cached
        /// element so we can <em>unhook</em> in <see cref="TryDisconnect"/>;
        /// the helper indexer returns a fresh <see cref="DataStorageElement"/>
        /// each call, so unsubscribing from a different instance would be
        /// a no-op.
        /// </para>
        /// </summary>
        private void InitMetaCoinsDataStorage()
        {
            if (session == null) return;

            try
            {
                int slot = session.ConnectionInfo.Slot;
                MetaCoinsKey = $"CW_MetaCoins_{slot}";

                _metaCoinsElement = session.DataStorage[Scope.Slot, MetaCoinsKey];

                // Default-on-first-connect failsafe: 0 if the key was never set.
                // Initialize's only overloads take JToken/IEnumerable, which
                // would drag a JToken type reference into our compile units;
                // invoke it reflectively against the JToken overload instead
                // (the value is wrapped via JToken's implicit-from-int operator).
                InitializeWithZero(_metaCoinsElement);

                // Subscribe before reading so we never miss an in-flight write.
                // The event delegate signature contains JToken from
                // Newtonsoft.Json v11.0.0.0; we build the handler reflectively
                // to avoid pulling JToken into our compile units.  See
                // <see cref="_metaCoinsHandler"/>.
                _metaCoinsHandler = SubscribeMetaCoinsListener(_metaCoinsElement);

                // Read the current authoritative value and apply it locally.
                // Using the synchronous int conversion is safe because we just
                // ensured the key exists via Initialize() above.
                int current = _metaCoinsElement.To<int>();
                ApplyMetaCoinsLocally(current);

                Plugin.Logger.LogInfo(
                    $"[AP] Meta Coins DataStorage bound to '{MetaCoinsKey}' (current value: {current}).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    $"[AP] Failed to initialise Meta Coins DataStorage: {ex.Message}");
            }
        }

        /// <summary>
        /// Build a delegate matching <c>DataStorageElement.OnValueChanged</c>'s
        /// signature via reflection and register it on the given element.
        /// Returns the <see cref="Delegate"/> so callers can unhook on
        /// disconnect.  All access to the JToken parameters happens through
        /// runtime binding (<c>dynamic</c>), so no compile-time reference to
        /// Newtonsoft.Json is needed.
        /// </summary>
        private Delegate SubscribeMetaCoinsListener(DataStorageElement element)
        {
            var eventInfo = typeof(DataStorageElement).GetEvent(
                nameof(DataStorageElement.OnValueChanged),
                BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    "DataStorageElement.OnValueChanged event not found");

            var handlerType = eventInfo.EventHandlerType
                ?? throw new InvalidOperationException(
                    "DataStorageElement.OnValueChanged has no delegate type");

            // Bind to our private callback method that takes (object, object, object).
            var callback = AccessTools.Method(typeof(ArchipelagoClient), nameof(MetaCoinsValueChanged));

            // Delegate.CreateDelegate validates that the bound method is
            // assignable to the event's delegate; reference types collapse
            // to object via inheritance, so JToken/Dictionary parameters
            // pass through fine.
            var del = Delegate.CreateDelegate(handlerType, this, callback);
            eventInfo.AddEventHandler(element, del);
            return del;
        }

        /// <summary>
        /// Reflectively call <c>DataStorageElement.Initialize(JToken)</c> with
        /// the int 0.  We resolve the JToken overload by name + parameter
        /// count and rely on the runtime to perform the int → JToken
        /// implicit conversion via <c>Convert.ChangeType</c>.  Falls back to
        /// passing the boxed int directly if the runtime accepts it.
        /// </summary>
        private static void InitializeWithZero(DataStorageElement element)
        {
            // Find the JToken overload (the IEnumerable one would crash on a
            // boxed int — int is not IEnumerable).  Both have the same name
            // and arity, so pick the one whose param type is *not* IEnumerable.
            MethodInfo? jtokenOverload = null;
            foreach (var m in typeof(DataStorageElement).GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Initialize") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                // Reject the IEnumerable overload by name.
                if (ps[0].ParameterType.FullName == "System.Collections.IEnumerable") continue;
                jtokenOverload = m;
                break;
            }

            if (jtokenOverload == null)
            {
                Plugin.Logger.LogWarning(
                    "[AP] DataStorageElement.Initialize(JToken) not found — first-join failsafe skipped.");
                return;
            }

            // Invoke a JToken's implicit operator from int to wrap 0 without
            // referencing JToken in our compile units.
            var jtokenType = jtokenOverload.GetParameters()[0].ParameterType;
            var implicitOp = jtokenType.GetMethod(
                "op_Implicit",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(int) },
                modifiers: null);

            if (implicitOp == null)
            {
                Plugin.Logger.LogWarning(
                    "[AP] JToken.op_Implicit(int) not found — first-join failsafe skipped.");
                return;
            }

            object zeroToken = implicitOp.Invoke(null, new object[] { 0 })!;
            jtokenOverload.Invoke(element, new[] { zeroToken });
        }

        private void UnsubscribeMetaCoinsListener(DataStorageElement element, Delegate handler)
        {
            try
            {
                var eventInfo = typeof(DataStorageElement).GetEvent(
                    nameof(DataStorageElement.OnValueChanged),
                    BindingFlags.Public | BindingFlags.Instance);
                eventInfo?.RemoveEventHandler(element, handler);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    $"[AP] Unhooking Meta Coins listener failed (best-effort): {ex.Message}");
            }
        }

        /// <summary>
        /// Send a delta to the lobby's shared Meta Coins key.  Only the master
        /// client should call this for AP-item-driven grants and for hat
        /// purchases — all other clients receive the change via the
        /// <see cref="OnMetaCoinsChanged"/> listener.
        /// </summary>
        public void AddMetaCoinsDelta(int amount)
        {
            if (!connected || _metaCoinsElement == null)
            {
                Plugin.Logger.LogWarning(
                    $"[AP] AddMetaCoinsDelta({amount}) called while disconnected — dropping.");
                return;
            }

            try
            {
                // Use a fresh element for the math op — chaining operators on
                // our cached element would mutate the variable that holds the
                // listener subscription.  The cached element is for read /
                // unsubscribe only.
                session!.DataStorage[Scope.Slot, MetaCoinsKey] += amount;
                Plugin.Logger.LogInfo(
                    $"[AP] DataStorage Meta Coins {(amount >= 0 ? "+" : "")}{amount} → '{MetaCoinsKey}'.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    $"[AP] AddMetaCoinsDelta({amount}) failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reflection-bound listener for the lobby's Meta Coins key.  Bound
        /// to <c>DataStorageElement.OnValueChanged</c> via
        /// <see cref="SubscribeMetaCoinsListener"/>; the runtime supplies the
        /// real arguments (<c>JToken originalValue, JToken newValue,
        /// Dictionary&lt;string, JToken&gt; additionalArguments</c>) and we
        /// read them via <c>dynamic</c> dispatch so the JToken type never
        /// appears in our compile units.
        /// </summary>
        private void MetaCoinsValueChanged(object originalValue, object newValue, object additionalArguments)
        {
            try
            {
                // JToken implements IConvertible — Convert.ToInt32 walks the
                // IConvertible interface at runtime, so we can extract the
                // numeric value without spelling JToken in our compile units.
                int newInt = Convert.ToInt32(newValue);
                ApplyMetaCoinsLocally(newInt);
                Plugin.Logger.LogInfo(
                    $"[AP] Meta Coins synced from server: {originalValue} → {newInt}.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    $"[AP] MetaCoinsValueChanged handler failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reflection-write the private <c>metaCoins</c> field on the singleton.
        /// We cannot call <c>SetMetaCoins</c> here because that triggers
        /// <c>UpdateAndSave</c>, which would persist the AP balance to the
        /// player's vanilla save file — defeating the whole point of this
        /// patch.  (Even though <c>UpdateAndSave</c> is also Harmony-patched
        /// to skip in AP mode, going around it directly is safer and avoids
        /// the cost of building a <c>SerializedMetaProgression</c> on every
        /// listener tick.)
        /// </summary>
        private static void ApplyMetaCoinsLocally(int value)
        {
            if (_metaCoinsField == null)
            {
                _metaCoinsField = AccessTools.Field(typeof(MetaProgressionHandler), "metaCoins");
                if (_metaCoinsField == null)
                {
                    Plugin.Logger.LogWarning(
                        "[AP] MetaProgressionHandler.metaCoins field not found — " +
                        "Meta Coins HUD will not reflect AP balance.");
                    return;
                }
            }

            var instance = RetrievableSingleton<MetaProgressionHandler>.Instance;
            if (instance == null)
            {
                Plugin.Logger.LogDebug(
                    "[AP] MetaProgressionHandler not yet instantiated — Meta Coins write deferred.");
                return;
            }

            _metaCoinsField.SetValue(instance, value);
        }

        // ================================================================== SCOUTING

        /// <summary>
        /// Scouts the given location IDs on the AP server using
        /// <c>HintCreationPolicy.None</c> (no hint-point cost) and returns a
        /// dictionary mapping each location ID to the item name placed there.
        ///
        /// <para>
        /// Used by <c>HatShopPatch</c> to show the AP item name behind each hat
        /// in the shop UI.  Returns an empty dictionary if not connected, if all
        /// IDs are invalid, or if the server call fails.
        /// </para>
        /// </summary>
        public async Task<Dictionary<long, string>> ScoutLocationsAsync(IEnumerable<long> locationIds)
        {
            var result = new Dictionary<long, string>();

            if (session == null || !connected)
            {
                Plugin.Logger.LogWarning("[AP] ScoutLocationsAsync called while disconnected.");
                return result;
            }

            long[] ids = System.Linq.Enumerable.ToArray(locationIds);
            if (ids.Length == 0) return result;

            try
            {
                // Archipelago.MultiClient.Net v6 API:
                // Task<Dictionary<long, ScoutedItemInfo>> ScoutLocationsAsync(
                //     HintCreationPolicy policy, params long[] locationIds)
                var scouted = await session.Locations.ScoutLocationsAsync(
                    HintCreationPolicy.None, ids);

                if (scouted == null) return result;

                foreach (var kvp in scouted)
                {
                    long   locId    = kvp.Key;
                    string itemName = kvp.Value.ItemName
                                   ?? $"Item {kvp.Value.ItemId}";
                    result[locId] = itemName;
                }

                Plugin.Logger.LogInfo(
                    $"[AP] Scouted {result.Count}/{ids.Length} hat location(s).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[AP] ScoutLocationsAsync failed: {ex.Message}");
            }

            return result;
        }
    }
}
