// Patches/ChorbyPatches.cs
//
// Chorby is a unique map-spawned Archipelago collectible (issue #14).
// One Chorby spawns per dive; picking it up is intercepted, the GameObject
// is destroyed, and a sequential "Found Chorby N" location check fires.
// QuotaCount controls how many checks exist (max 21).
//
// ─────────────────────────────────────────────────────────────────────────────
// PATCH 1 — ChorbySpawnPatch
//   Target : RoundArtifactSpawner.GetArtifactsToSpawn(int, float)  [private]
//
//   WHY THIS METHOD:
//   RoundArtifactSpawner.Start() runs once per dive on the master client and
//   calls SpawnRound → GetArtifactsToSpawn → CreateArtifactSpawners.
//   GetArtifactsToSpawn returns the list of Items the dive will spawn,
//   weighted by Item.rarity and the per-day budget.  Chorby's vanilla rarity
//   is ~0.005, so it almost never makes the list.
//
//   We postfix the result list to:
//     1. Remove any vanilla-picked Chorby entries (cap to 0).
//     2. Insert exactly one Chorby reference.
//   CreateArtifactSpawners then spawns one ArtifactSpawner for it like any
//   other artifact, so it lands at a randomised PatrolPoint and behaves
//   normally apart from our pickup interception.
//
//   No master-client guard needed — Start() already gates the only caller
//   with PhotonNetwork.IsMasterClient.
//
// ─────────────────────────────────────────────────────────────────────────────
// PATCH 2 — ChorbyPickupPatch
//   Target : Pickup.RPC_RequestPickup(int photonView)  [PunRPC, master-only]
//
//   WHY THIS METHOD:
//   Pickup.Interact sends RPC_RequestPickup to the master client; the master
//   is the single authority that decides whether the pickup succeeds.  By
//   prefixing this method we can:
//     1. Detect a Chorby pickup attempt (by Item.name).
//     2. Cancel the vanilla pickup so Chorby never enters inventory.
//     3. Destroy the GameObject network-wide via RPC_Remove.
//     4. Increment APSaveData.chorbiesFound, send the AP check.
//
//   Past quotaCount, all subsequent Chorbies are destroyed silently.  The
//   first such over-cap pickup broadcasts the "All Chorby checks found"
//   lobby toast via Mycelium; subsequent ones suppress the broadcast.
//
//   Master-client guard: implicit — RPC_RequestPickup only executes on the
//   master client (Pickup.Interact targets RpcTarget.MasterClient).  We add
//   a defensive PhotonNetwork.IsMasterClient guard anyway.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Reflection;
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Data;
using ContentWarningArchipelago.UI;
using HarmonyLib;
using MyceliumNetworking;
using Photon.Pun;
using Steamworks;

namespace ContentWarningArchipelago.Patches
{
    // =========================================================================
    // PATCH 1 — Force exactly one Chorby into the per-dive artifact list
    // =========================================================================
    [HarmonyPatch]
    internal static class ChorbySpawnPatch
    {
        // The Chorby Item asset, resolved lazily once the spawner exposes its
        // possibleSpawns array.  Comparing by reference is safe because the
        // ItemDatabase / SingletonAsset returns the same instance each call.
        private static object? _chorbyItem;

        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("RoundArtifactSpawner");
            if (type == null)
            {
                Plugin.Logger.LogWarning(
                    "[ChorbySpawnPatch] RoundArtifactSpawner type not found — Chorby spawn-cap disabled.");
                return null;
            }

            var method = AccessTools.Method(type, "GetArtifactsToSpawn");
            if (method == null)
            {
                Plugin.Logger.LogWarning(
                    "[ChorbySpawnPatch] GetArtifactsToSpawn method not found — Chorby spawn-cap disabled.");
                return null;
            }

            Plugin.Logger.LogInfo($"[ChorbySpawnPatch] Patching {type.Name}.{method.Name}");
            return method;
        }

        [HarmonyPostfix]
        static void Postfix(object __instance, object __result)
        {
            try
            {
                if (__result is not System.Collections.IList resultList)
                {
                    Plugin.Logger.LogWarning(
                        "[ChorbySpawnPatch] GetArtifactsToSpawn returned a non-IList — Chorby skipped this dive.");
                    return;
                }

                // Resolve Chorby once via the spawner's possibleSpawns array.
                if (_chorbyItem == null)
                    _chorbyItem = ResolveChorby(__instance);

                if (_chorbyItem == null)
                {
                    Plugin.Logger.LogWarning(
                        "[ChorbySpawnPatch] Chorby Item not found in possibleSpawns — Chorby skipped this dive.");
                    return;
                }

                // Strip any vanilla Chorby picks (cap from 0 from the random pool)…
                int removed = 0;
                for (int i = resultList.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(resultList[i], _chorbyItem))
                    {
                        resultList.RemoveAt(i);
                        removed++;
                    }
                }

                // …then insert exactly one Chorby at the front of the list.
                resultList.Insert(0, _chorbyItem);

                Plugin.Logger.LogInfo(
                    $"[ChorbySpawnPatch] Forced 1 Chorby into the artifact list " +
                    $"(removed {removed} vanilla pick{(removed == 1 ? "" : "s")}, " +
                    $"final count={resultList.Count}).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ChorbySpawnPatch] Postfix failed: {ex}");
            }
        }

        /// <summary>
        /// Walks <c>RoundArtifactSpawner.possibleSpawns</c> looking for the
        /// Chorby Item by Unity asset name.  Returns null if absent.
        /// </summary>
        private static object? ResolveChorby(object spawnerInstance)
        {
            var possibleField = AccessTools.Field(spawnerInstance.GetType(), "possibleSpawns");
            if (possibleField == null) return null;

            if (possibleField.GetValue(spawnerInstance) is not System.Collections.IEnumerable items)
                return null;

            foreach (var item in items)
            {
                if (item is UnityEngine.Object uo &&
                    string.Equals(uo.name, "Chorby", StringComparison.Ordinal))
                {
                    return item;
                }
            }
            return null;
        }
    }

    // =========================================================================
    // PATCH 2 — Intercept Chorby pickup and convert it to an AP check
    // =========================================================================
    [HarmonyPatch]
    internal static class ChorbyPickupPatch
    {
        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("Pickup");
            if (type == null)
            {
                Plugin.Logger.LogWarning(
                    "[ChorbyPickupPatch] Pickup type not found — Chorby pickup intercept disabled.");
                return null;
            }

            var method = AccessTools.Method(type, "RPC_RequestPickup");
            if (method == null)
            {
                Plugin.Logger.LogWarning(
                    "[ChorbyPickupPatch] Pickup.RPC_RequestPickup not found — Chorby pickup intercept disabled.");
                return null;
            }

            Plugin.Logger.LogInfo($"[ChorbyPickupPatch] Patching {type.Name}.{method.Name}");
            return method;
        }

        /// <summary>
        /// Returns false to skip the vanilla pickup body when the targeted
        /// item is Chorby; otherwise lets the original method run normally.
        /// </summary>
        [HarmonyPrefix]
        static bool Prefix(object __instance)
        {
            try
            {
                if (!IsChorby(__instance, out string itemName)) return true;

                if (!PhotonNetwork.IsMasterClient)
                {
                    // Defensive — RPC_RequestPickup is RpcTarget.MasterClient,
                    // so we should never reach here on a non-master.
                    Plugin.Logger.LogDebug(
                        "[ChorbyPickupPatch] Non-master client received RPC_RequestPickup — letting vanilla handle it.");
                    return true;
                }

                Plugin.Logger.LogInfo($"[ChorbyPickupPatch] Chorby pickup intercepted ({itemName}).");

                // Cap reached — broadcast once, then silent for the rest.
                if (Plugin.connection.connected &&
                    APSave.saveData.chorbiesFound >= APSave.saveData.quotaCount)
                {
                    if (!APSave.saveData.allChorbyChecksFoundNotified)
                    {
                        BroadcastAllChorbyChecksFound();
                        APSave.saveData.allChorbyChecksFoundNotified = true;
                        APSave.Flush();
                    }
                    DestroyPickup(__instance);
                    return false;
                }

                // Standard path — increment, send the matching AP check.
                if (Plugin.connection.connected)
                {
                    APSave.saveData.chorbiesFound++;
                    int n = APSave.saveData.chorbiesFound;

                    string locName = LocationNames.FoundChorbyPrefix + n;
                    long   locId   = LocationData.GetId(locName);
                    if (locId > 0)
                    {
                        Plugin.Logger.LogInfo(
                            $"[ChorbyPickupPatch] Chorby #{n} picked up → {locName}");
                        Plugin.SendCheck(locId);
                    }
                    else
                    {
                        Plugin.Logger.LogDebug(
                            $"[ChorbyPickupPatch] Chorby #{n} above the 21-check ceiling — no AP check sent.");
                    }

                    APSave.Flush();
                }

                DestroyPickup(__instance);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ChorbyPickupPatch] Prefix failed: {ex}");
                // Fall through to vanilla so a buggy patch doesn't soft-lock pickups.
                return true;
            }
        }

        /// <summary>
        /// Reads <c>Pickup.m_itemID</c> via reflection, looks up the Item in
        /// the database, and returns true when it's Chorby.
        /// </summary>
        private static bool IsChorby(object pickupInstance, out string itemName)
        {
            itemName = string.Empty;

            var idField = AccessTools.Field(pickupInstance.GetType(), "m_itemID");
            if (idField == null) return false;
            if (idField.GetValue(pickupInstance) is not byte itemId) return false;

            var dbType = AccessTools.TypeByName("ItemDatabase");
            var tryGet = dbType != null ? AccessTools.Method(dbType, "TryGetItemFromID") : null;
            if (tryGet == null) return false;

            var args = new object[] { itemId, null! };
            bool ok = (bool)(tryGet.Invoke(null, args) ?? false);
            if (!ok || args[1] is not UnityEngine.Object item) return false;

            itemName = item.name ?? string.Empty;
            return string.Equals(itemName, "Chorby", StringComparison.Ordinal);
        }

        /// <summary>
        /// Calls <c>m_photonView.RPC("RPC_Remove", RpcTarget.MasterClient)</c>
        /// to network-destroy the pickup, mirroring the cleanup the vanilla
        /// RPC_RequestPickup performs after a successful inventory add.
        /// </summary>
        private static void DestroyPickup(object pickupInstance)
        {
            try
            {
                var pvField = AccessTools.Field(pickupInstance.GetType(), "m_photonView");
                if (pvField?.GetValue(pickupInstance) is not PhotonView pv) return;

                pv.RPC("RPC_Remove", RpcTarget.MasterClient);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[ChorbyPickupPatch] DestroyPickup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Shows the local "All Chorby checks found" toast and broadcasts it
        /// to other lobby members via Mycelium so everyone sees the milestone.
        /// </summary>
        private static void BroadcastAllChorbyChecksFound()
        {
            string msg = LocationNames.AllChorbyChecksFound;

            APNotificationUI.ShowLocationFound(msg);
            Plugin.Logger.LogInfo($"[ChorbyPickupPatch] {msg} (post-quota Chorby pickup).");

            if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.PlayerCount > 1)
            {
                try
                {
                    MyceliumNetwork.RPC(
                        Plugin.MyceliumModId,
                        nameof(Plugin.LocationFound),
                        ReliableType.Reliable,
                        msg);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning(
                        $"[ChorbyPickupPatch] Mycelium broadcast failed: {ex.Message}");
                }
            }
        }
    }
}
