// Patches/TrapPatches.cs
// Implements the two AP trap items:
//
//   • Ragdoll Trap  — ragdolls the local player for 5 s.
//   • Monster Spawn — spawns a random monster near the local player's position.
//
// NETWORKING MODEL
// Ragdoll Trap:
//   Directly calls PlayerRagdoll.Fall(duration) on Player.localPlayer.
//   Fall() sets player.data.fallTime; UpdateValues_Fixed() decrements it
//   automatically each FixedUpdate — no un-ragdoll coroutine is needed.
//   No master-client restriction: each AP client applies the effect to
//   their own local player the moment the item is received.
//
// Monster Spawn Trap:
//   Calls MonsterSpawner.SpawnMonster(prefabName, position) which internally
//   calls HelperFunctions.GetGroundPos + PhotonNetwork.Instantiate.
//   PhotonNetwork.Instantiate may be called from any connected client;
//   the spawned object's Bot component self-registers with BotHandler.instance
//   in Bot.Start(), so BotHandler tracks it automatically.
//   Position = Player.localPlayer.Center() + random horizontal offset.

using System;
using ContentWarningArchipelago.Core;
using Photon.Pun;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Static helper called from <c>ItemData.HandleReceivedItem</c> for trap items.
    /// </summary>
    public static class TrapHandler
    {
        // ── Monster prefab names ─────────────────────────────────────────────
        // These must match the Unity resource / Photon resource folder names
        // exactly (case-sensitive). Names taken from item_notes.md.
        // "Spawn 3 Zombe" is represented as a higher-weight "Zombe" entry;
        // triple-spawning can be added as a future enhancement.
        private static readonly string[] MonsterPrefabNames =
        {
            "Arms",
            "Bomber",
            "Zombe",        // item_notes.md spells it "Zombe", not "Zombie"
            "Zombe",        // "Spawn 3 Zombe" — weighted double entry for now
            "Whisk",
            "Eye Guy",
            "Knifo",
            "Button Robot",
            "Mouthe",
            "Puffo",
        };

        // Radius (Unity units) of the random horizontal spawn offset from the player.
        private const float SpawnOffsetRadius = 5f;

        // ================================================================== Ragdoll Trap

        /// <summary>
        /// Ragdolls the local player for <paramref name="duration"/> seconds by calling
        /// <c>PlayerRagdoll.Fall</c> directly on <c>Player.localPlayer</c>.
        /// Works on any client — no master-client restriction required.
        /// </summary>
        public static void ApplyRagdollTrap(float duration = 5f)
        {
            var local = Player.localPlayer;
            if ((object)local == null)
            {
                Plugin.Logger.LogWarning("[TrapHandler] RagdollTrap: Player.localPlayer is null — not in-game yet?");
                return;
            }

            if ((object)local.refs?.ragdoll == null)
            {
                Plugin.Logger.LogWarning("[TrapHandler] RagdollTrap: ragdoll reference is null.");
                return;
            }

            // Fall(duration) sets player.data.fallTime = duration (if greater than
            // the current value), making player.Ragdoll() return true.
            // PlayerData.UpdateValues_Fixed() decrements fallTime every FixedUpdate
            // so the effect expires automatically — no cleanup coroutine needed.
            local.refs.ragdoll.Fall(duration);
            Plugin.Logger.LogInfo($"[TrapHandler] RagdollTrap: applied Fall({duration}s) to local player.");
        }

        // ================================================================== Monster Spawn Trap

        /// <summary>
        /// Spawns a random monster near the local player's current position.
        /// Uses <c>MonsterSpawner.SpawnMonster</c> which handles ground-snapping
        /// via <c>HelperFunctions.GetGroundPos</c> and <c>PhotonNetwork.Instantiate</c>.
        /// The spawned monster's <c>Bot</c> component self-registers with
        /// <c>BotHandler.instance</c> in <c>Bot.Start()</c>.
        /// </summary>
        public static void ApplyMonsterSpawnTrap()
        {
            if (!PhotonNetwork.IsConnected)
            {
                Plugin.Logger.LogWarning("[TrapHandler] MonsterSpawn: not connected to Photon room — cannot spawn.");
                return;
            }

            var local = Player.localPlayer;
            if ((object)local == null)
            {
                Plugin.Logger.LogWarning("[TrapHandler] MonsterSpawn: Player.localPlayer is null — not in-game yet?");
                return;
            }

            // Pick a random monster from the weighted list.
            int idx = UnityEngine.Random.Range(0, MonsterPrefabNames.Length);
            string chosenMonster = MonsterPrefabNames[idx];

            // Random horizontal offset; Y zeroed — MonsterSpawner snaps to ground internally.
            Vector2 circle = UnityEngine.Random.insideUnitCircle * SpawnOffsetRadius;
            Vector3 spawnPos = local.Center() + new Vector3(circle.x, 0f, circle.y);

            Plugin.Logger.LogInfo(
                $"[TrapHandler] MonsterSpawn: spawning '{chosenMonster}' " +
                $"near {local.refs.view.Controller.NickName} at {spawnPos}.");

            try
            {
                // MonsterSpawner.SpawnMonster(string monster, Vector3 position) calls
                //   HelperFunctions.GetGroundPos(position + Vector3.up, ...)
                //   PhotonNetwork.Instantiate(monster, groundPos, identity, 0)
                // Any connected Photon client may call PhotonNetwork.Instantiate.
                // The spawned GameObject's Bot.Start() adds itself to BotHandler.instance.bots.
                MonsterSpawner.SpawnMonster(chosenMonster, spawnPos);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[TrapHandler] MonsterSpawn failed for '{chosenMonster}': {ex.Message}");
            }
        }
    }
}
