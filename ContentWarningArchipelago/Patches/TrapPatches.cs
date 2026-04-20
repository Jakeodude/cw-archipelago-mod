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
//
//   GUARD: Monsters may only be spawned while players are underground
//   (SurfaceNetworkHandler.Instance.IsOldWorld == true).  If the item arrives
//   while the team is still on the surface, the spawn is DEFERRED via a Unity
//   coroutine (WaitUntil) until the player enters the old world.  The spawn
//   command is never silently discarded.

using System;
using System.Collections;
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
        ///
        /// GUARD: If the local player is not yet in the old world (underground),
        /// the spawn is deferred via a <c>WaitUntil</c> coroutine started on
        /// <c>Plugin.Instance</c>.  The spawn is never silently cancelled.
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

            // Guard: monsters only exist in the old world (underground scene).
            // SurfaceNetworkHandler.Instance is non-null only while the surface scene is
            // loaded.  When players transition underground, the surface scene unloads and
            // Instance becomes null — that null state means "we are in the old world".
            // If we are still on the surface (Instance != null), defer the spawn via a
            // WaitUntil coroutine so the command is never silently discarded.
            if (SurfaceNetworkHandler.Instance != null)
            {
                Plugin.Logger.LogInfo(
                    "[TrapHandler] MonsterSpawn: player is not in the old world — " +
                    "deferring spawn until player is underground.");
                Plugin.Instance.StartCoroutine(WaitForOldWorldThenSpawn());
                return;
            }

            ExecuteMonsterSpawn(local);
        }

        // ================================================================== Deferred spawn coroutine

        /// <summary>
        /// Waits until the surface scene has unloaded (i.e. <c>SurfaceNetworkHandler.Instance</c>
        /// becomes <c>null</c>), which signals that the player has transitioned underground
        /// into the old world.  Then executes the monster spawn.
        /// Started on <c>Plugin.Instance</c> so it survives scene transitions.
        /// </summary>
        private static IEnumerator WaitForOldWorldThenSpawn()
        {
            Plugin.Logger.LogInfo("[TrapHandler] MonsterSpawn (deferred): waiting for old world…");

            // SurfaceNetworkHandler.Instance is null only when the surface scene is not loaded,
            // meaning players have transitioned underground into the old world.
            yield return new WaitUntil(() => SurfaceNetworkHandler.Instance == null);

            var local = Player.localPlayer;
            if ((object)local == null)
            {
                Plugin.Logger.LogWarning(
                    "[TrapHandler] MonsterSpawn (deferred): Player.localPlayer is null " +
                    "after entering old world — spawn aborted.");
                yield break;
            }

            Plugin.Logger.LogInfo(
                "[TrapHandler] MonsterSpawn (deferred): old world confirmed — executing spawn.");
            ExecuteMonsterSpawn(local);
        }

        // ================================================================== Core spawn logic

        /// <summary>
        /// Picks a random monster from <see cref="MonsterPrefabNames"/>, applies a
        /// random horizontal offset from <paramref name="local"/>'s position, and calls
        /// <c>MonsterSpawner.SpawnMonster</c> which handles ground-snapping and
        /// <c>PhotonNetwork.Instantiate</c>.
        /// </summary>
        private static void ExecuteMonsterSpawn(Player local)
        {
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
