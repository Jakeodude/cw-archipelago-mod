// Core/ArchipelagoClient.cs
// Manages the Archipelago.MultiClient.Net session for Content Warning.
// Modelled after R.E.P.O.-Archipelago-Client-Mod/Core/ArchipelagoConnection.cs.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using Archipelago.MultiClient.Net.Helpers;
using Archipelago.MultiClient.Net.Models;
using Archipelago.MultiClient.Net.Packets;
using ContentWarningArchipelago.Data;
using ContentWarningArchipelago.UI;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

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

                // ---- Resync items the server already sent while we were offline ----
                _itemIndex = APSave.saveData.itemReceivedIndex;

                // Start frame-tick coroutines.
                _incomingItems  = new ConcurrentQueue<(ItemInfo, int)>();
                _pendingChecks  = new ConcurrentQueue<long>();

                checkItemsReceived  = CheckItemsReceived();
                incomingItemHandler = IncomingItemHandler();
                outgoingCheckHandler = OutgoingCheckHandler();
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

            // Broadcast the location-found notification to all OTHER players in the Photon
            // lobby so everyone sees who found a check (surface or underground).
            // Plugin.APLocationFoundEventCode is received by Plugin.OnPhotonEventReceived,
            // which calls APNotificationUI.ShowLocationFound on the remote clients.
            if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.PlayerCount > 1)
            {
                try
                {
                    var raiseOptions = new RaiseEventOptions { Receivers = ReceiverGroup.Others };
                    PhotonNetwork.RaiseEvent(
                        Plugin.APLocationFoundEventCode,
                        locName,
                        raiseOptions,
                        SendOptions.SendReliable);
                    Plugin.Logger.LogDebug(
                        $"[AP] Broadcast location-found notification for '{locName}' to other players.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning(
                        $"[AP] RaiseEvent for location notification failed: {ex.Message}");
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
    }
}
