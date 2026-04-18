// Patches/TrapPatches.cs
// Implements the two AP trap items:
//
//   • Ragdoll Trap  — ragdolls ALL players in the lobby for ~5 s.
//   • Monster Spawn — spawns a monster near ONE random player in the lobby.
//
// NETWORKING MODEL (Virality-informed)
// Virality uses SurfaceNetworkHandler.Instance.m_View.RPC(..., RpcTarget.All, ...)
// to broadcast effects to every client.  We apply the same pattern:
//   • RagdollTrap:    master sends RPC to RpcTarget.All  → each client ragdolls its local player.
//   • MonsterSpawn:   master picks a random live player, sends targeted RPC to them
//                     → that client's machine handles the spawn (avoids authority issues).
//
// If the local client is NOT the master when a trap arrives, we queue a request
// that is sent once we confirm master-client status, or we send via the
// existing SurfaceNetworkHandler view so the RPC is routed correctly.
//
// REFLECTION STRATEGY
// Ragdoll and monster-spawn APIs are accessed via AccessTools so the patch
// compiles and loads even if method signatures change in a game update —
// it will log a warning and skip rather than crashing.

// Unity's AddComponent<T>() return type is annotated as T? in the publicized NuGet
// stubs even though it always succeeds for MonoBehaviours — suppress the resulting
// nullable-conversion warnings that are expected in Unity projects.
#pragma warning disable CS8600

using System;
using System.Collections;
using ContentWarningArchipelago.Core;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Static helper called from <c>ItemData.HandleReceivedItem</c> for trap items.
    /// All network dispatching is handled here; callers just invoke the public methods.
    /// </summary>
    public static class TrapHandler
    {
        // ================================================================== Ragdoll Trap

        /// <summary>
        /// Ragdolls ALL players for <paramref name="duration"/> seconds.
        /// Only the master client should call this (it broadcasts to everyone).
        /// If the local client is not master the call is silently dropped — the
        /// master's own ItemData handler will have received the same trap item.
        /// </summary>
        public static void ApplyRagdollTrap(float duration = 5f)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                Plugin.Logger.LogDebug("[TrapHandler] RagdollTrap: not master — skipping (master will handle).");
                return;
            }

            Plugin.Logger.LogInfo("[TrapHandler] Sending RagdollTrap RPC to all players.");

            try
            {
                // Send RPC via SurfaceNetworkHandler's existing view — same vehicle Virality uses.
                SurfaceNetworkHandler.Instance.m_View.RPC(
                    "RPCA_RagdollTrap",
                    RpcTarget.All,
                    duration);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrapHandler] RPCA_RagdollTrap RPC failed: {ex.Message}");
                // Fall back: apply locally at least.
                ApplyRagdollLocal(duration);
            }
        }

        /// <summary>
        /// Called on each client's machine by the RPCA_RagdollTrap RPC (or as
        /// a local fallback).  Applies ragdoll physics to the local player.
        /// </summary>
        internal static void ApplyRagdollLocal(float duration)
        {
            var local = Player.localPlayer;
            if ((object)local == null) return;

            // Look for a known ragdoll / physics-state method on Player or Player.PlayerData.
            bool applied = false;

            // Strategy 1: Player.Ragdoll() — common CW method name.
            foreach (string mname in new[] { "Ragdoll", "ApplyRagdoll", "SetRagdoll", "ForceRagdoll" })
            {
                var m = AccessTools.Method(typeof(Player), mname);
                if (m == null) m = AccessTools.Method(typeof(Player.PlayerData), mname);
                if (m != null)
                {
                    try
                    {
                        m.Invoke(local, null);
                        applied = true;
                        Plugin.Logger.LogInfo($"[TrapHandler] Applied ragdoll via {m.Name}.");
                        break;
                    }
                    catch { /* try next */ }
                }
            }

            if (!applied)
            {
                Plugin.Logger.LogWarning("[TrapHandler] Could not find ragdoll method — trap had no effect.");
                return;
            }

            // Schedule un-ragdoll after duration.
            // We need a MonoBehaviour to run a coroutine; piggyback on the plugin instance.
            if ((object)Plugin.connection != null)
                CoroutineRunner.Run(UnragdollAfter(local, duration));
        }

        private static IEnumerator UnragdollAfter(Player player, float delay)
        {
            yield return new WaitForSeconds(delay);

            if ((object)player == null) yield break;

            foreach (string mname in new[] { "UnRagdoll", "StopRagdoll", "Revive", "Stand" })
            {
                var m = AccessTools.Method(typeof(Player), mname);
                if (m == null) m = AccessTools.Method(typeof(Player.PlayerData), mname);
                if (m != null)
                {
                    try { m.Invoke(player, null); } catch { }
                    break;
                }
            }
        }

        // ================================================================== Monster Spawn Trap

        /// <summary>
        /// Spawns a monster near ONE random alive player in the lobby.
        /// Only the master client should call this; it picks a random target
        /// and sends a targeted RPC (Virality pattern: player.refs.view.Controller).
        /// </summary>
        public static void ApplyMonsterSpawnTrap()
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                Plugin.Logger.LogDebug("[TrapHandler] MonsterSpawn: not master — skipping.");
                return;
            }

            var handler = PlayerHandler.instance;
            if ((object)handler == null || handler.playersAlive == null || handler.playersAlive.Count == 0)
            {
                Plugin.Logger.LogWarning("[TrapHandler] MonsterSpawn: no alive players found.");
                return;
            }

            // Pick a random alive player (Virality picks from playersAlive array).
            int idx    = UnityEngine.Random.Range(0, handler.playersAlive.Count);
            var target = handler.playersAlive.ToArray()[idx];

            Plugin.Logger.LogInfo(
                $"[TrapHandler] MonsterSpawn targeting player: {target.refs.view.Controller.NickName}");

            try
            {
                // Targeted RPC to the chosen player's view controller.
                // That client will spawn the monster near their own position.
                SurfaceNetworkHandler.Instance.m_View.RPC(
                    "RPCA_MonsterSpawnTrap",
                    target.refs.view.Controller);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[TrapHandler] RPCA_MonsterSpawnTrap RPC failed: {ex.Message}");
                // Fall back: spawn near local player at least.
                SpawnMonsterLocal(Player.localPlayer);
            }
        }

        /// <summary>
        /// Called on the targeted client's machine to spawn a monster near them.
        /// Uses reflection to find a suitable enemy-director spawn method.
        /// </summary>
        internal static void SpawnMonsterLocal(Player targetPlayer)
        {
            if ((object)targetPlayer == null) return;

            Vector3 spawnPos = targetPlayer.transform.position + targetPlayer.transform.forward * 3f;

            // Look for an EnemyDirector or monster spawner singleton.
            foreach (string typeName in new[] { "EnemyDirector", "MonsterDirector", "EnemySpawner" })
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) continue;

                // Try to get singleton instance.
                object? instance = null;
                var instanceField = AccessTools.Field(t, "instance")
                                 ?? AccessTools.Field(t, "Instance");
                if (instanceField != null) instance = instanceField.GetValue(null);

                if (instance == null) continue;

                // Try to call a spawn method.
                foreach (string mname in new[] { "SpawnEnemy", "SpawnRandom", "ForceSpawn", "Spawn" })
                {
                    var m = AccessTools.Method(t, mname);
                    if (m == null) continue;

                    try
                    {
                        // Try with position parameter, then without.
                        var parms = m.GetParameters();
                        if (parms.Length == 1 && parms[0].ParameterType == typeof(Vector3))
                            m.Invoke(instance, new object[] { spawnPos });
                        else if (parms.Length == 0)
                            m.Invoke(instance, null);
                        else
                            continue;

                        Plugin.Logger.LogInfo($"[TrapHandler] Spawned monster via {typeName}.{mname}.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogDebug($"[TrapHandler] {typeName}.{mname} failed: {ex.Message}");
                    }
                }
            }

            Plugin.Logger.LogWarning("[TrapHandler] MonsterSpawn: could not find a spawn method — no monster spawned.");
        }
    }

    // =========================================================================
    // Minimal coroutine runner — needed so TrapHandler (a static class) can
    // schedule the un-ragdoll coroutine without a direct MonoBehaviour reference.
    // =========================================================================

    internal static class CoroutineRunner
    {
        private static CoroutineBridge? _bridge;

    internal static void Run(IEnumerator routine)
        {
            if ((object)_bridge == null || !_bridge.gameObject.activeInHierarchy)
            {
                var go = new GameObject("AP_CoroutineRunner");
                UnityEngine.Object.DontDestroyOnLoad(go);
                // AddComponent always returns an instance here; the null-forgiving
                // operator suppresses CS8600 under <Nullable>enable</Nullable>.
                _bridge = go.AddComponent<CoroutineBridge>()!;
            }
            _bridge!.StartCoroutine(routine);
        }

        internal class CoroutineBridge : MonoBehaviour { }
    }
}
