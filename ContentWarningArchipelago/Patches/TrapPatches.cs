// Patches/TrapPatches.cs
// Implements the two AP trap items:
//
//   • Ragdoll Trap   — ragdolls the local player for 10 s.
//   • Monster Spawn  — spawns a random monster at a remote, line-of-sight-clear
//                      PatrolPoint, then ~3-5 s later calls the vanilla
//                      teleport-closer routine (Bot.Teleport →
//                      Level.GetClosestHiddenPoint near the recipient) so the
//                      monster ambushes naturally instead of materialising on
//                      the player.
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
//   PhotonNetwork.Instantiate may be called from any connected client; the
//   recipient becomes the spawned PhotonView's owner.  The spawned object's
//   Bot component self-registers with BotHandler.instance in Bot.Start().
//   No master-client guard on the teleport step — the recipient owns the
//   bot and per-bot Teleport() implementations propagate via Photon transform
//   sync (vanilla pattern, mirrors Puppetmonster.Intervene's call site).
//
//   GUARD: Monsters may only be spawned while players are underground
//   (SurfaceNetworkHandler.Instance == null).  If the item arrives while
//   the team is still on the surface, the spawn is DEFERRED via a Unity
//   coroutine until the player enters the old world; the spawn command is
//   never silently discarded.

using System.Collections;
using System.Reflection;
using ContentWarningArchipelago.Core;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Static helper called from <c>ItemData.HandleReceivedItem</c> for trap items.
    /// </summary>
    public static class TrapHandler
    {
        // Monster prefab names — must match the Photon prefab pool registration
        // exactly (case-sensitive). "Spawn 3 Zombe" is approximated as a
        // double-weighted "Zombe" entry; triple-spawning is a future enhancement.
        private static readonly string[] MonsterPrefabNames =
        {
            "Arms",
            "Bomber",
            "Zombe",
            "Zombe",
            "Whisk",
            "Eye Guy",
            "Knifo",
            "Button Robot",
            "Mouthe",
            "Puffo",
        };

        // Random horizontal offset radius used as a last-resort fallback when
        // no PatrolPoint is reachable in the level (Unity units).
        private const float FallbackSpawnOffsetRadius = 5f;

        // Reflection cache: Bot.Teleport(Vector3) and Level.GetClosestHiddenPoint
        // are both `internal`, so they can't be called directly from this
        // assembly.  AccessTools resolves them by name; missing methods log a
        // warning and we silently degrade (the trap still spawns, just without
        // the teleport-closer step).
        private static MethodInfo? _botTeleport;
        private static MethodInfo? _getClosestHiddenPoint;
        private static bool _reflectionInitialised;

        // ================================================================== Ragdoll Trap

        /// <summary>
        /// Ragdolls the local player for <paramref name="duration"/> seconds by calling
        /// <c>PlayerRagdoll.Fall</c> directly on <c>Player.localPlayer</c>.
        /// Works on any client — no master-client restriction required.
        /// </summary>
        public static void ApplyRagdollTrap(float duration = 10f)
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

            local.refs.ragdoll.Fall(duration);
            Plugin.Logger.LogInfo($"[TrapHandler] RagdollTrap: applied Fall({duration}s) to local player.");
        }

        // ================================================================== Monster Spawn Trap

        /// <summary>
        /// Spawns a random monster at a remote line-of-sight-clear PatrolPoint
        /// and schedules a vanilla-style teleport-closer ambush.
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

            // Defer if still on the surface — old-world transition unloads the
            // surface scene and SurfaceNetworkHandler.Instance becomes null.
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

        private static IEnumerator WaitForOldWorldThenSpawn()
        {
            Plugin.Logger.LogInfo("[TrapHandler] MonsterSpawn (deferred): waiting for old world…");

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
        /// Picks a random monster, finds a remote LOS-clear PatrolPoint to
        /// spawn it at, instantiates via <c>MonsterSpawner.SpawnMonster</c>
        /// (which handles ground-snapping), and schedules a teleport-closer
        /// ambush after a short delay.  Falls back to player.Center() + offset
        /// if no PatrolPoints are reachable, and skips the teleport step if
        /// reflection lookup of the internal vanilla methods failed.
        /// </summary>
        private static void ExecuteMonsterSpawn(Player local)
        {
            int idx = UnityEngine.Random.Range(0, MonsterPrefabNames.Length);
            string chosenMonster = MonsterPrefabNames[idx];

            // Bot.Start() calls BotHandler.instance.bots.Add(this) — that NREs
            // if the old-world scene is still finishing init.  Retry after 2 s.
            if (BotHandler.instance == null)
            {
                Plugin.Logger.LogWarning(
                    $"[TrapHandler] MonsterSpawn: BotHandler.instance is null — " +
                    $"retrying '{chosenMonster}' in 2 s.");
                Plugin.Instance.StartCoroutine(RetrySpawnAfterDelay(local, chosenMonster, 2f));
                return;
            }

            Vector3 spawnPos = ChooseRemoteSpawnPosition(local);

            Plugin.Logger.LogInfo(
                $"[TrapHandler] MonsterSpawn: spawning '{chosenMonster}' at {spawnPos} " +
                $"(targeting {local.refs.view.Controller.NickName}).");

            GameObject? spawned;
            try
            {
                spawned = MonsterSpawner.SpawnMonster(chosenMonster, spawnPos);
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[TrapHandler] MonsterSpawn failed for '{chosenMonster}': {ex.Message}");
                return;
            }

            if (spawned == null)
            {
                Plugin.Logger.LogWarning(
                    $"[TrapHandler] MonsterSpawn: SpawnMonster returned null for '{chosenMonster}' — " +
                    $"skipping teleport-closer step.");
                return;
            }

            var bot = spawned.GetComponent<Bot>() ?? spawned.GetComponentInChildren<Bot>();
            if (bot == null)
            {
                Plugin.Logger.LogWarning(
                    $"[TrapHandler] MonsterSpawn: spawned '{chosenMonster}' has no Bot component — " +
                    $"skipping teleport-closer step.");
                return;
            }

            float delay = UnityEngine.Random.Range(3f, 5f);
            Plugin.Instance.StartCoroutine(TeleportCloserAfterDelay(bot, delay, chosenMonster));
        }

        // ================================================================== Spawn-position picker

        /// <summary>
        /// Returns a random PatrolPoint position in the current level whose
        /// transform is not visible to any alive player (≈100-attempt random
        /// sample, mirroring <c>RoundSpawner.SpawnMonstersOutOfSight</c>).
        /// Falls back to the player's center + horizontal offset when no
        /// PatrolPoints are reachable (very early scene init, or unusual map).
        /// </summary>
        private static Vector3 ChooseRemoteSpawnPosition(Player local)
        {
            var allPoints = Object.FindObjectsOfType<PatrolPoint>();
            if (allPoints == null || allPoints.Length == 0)
            {
                Plugin.Logger.LogWarning(
                    "[TrapHandler] MonsterSpawn: no PatrolPoints found — falling back to player-relative spawn.");
                return PlayerCenteredFallback(local);
            }

            const int maxAttempts = 100;
            for (int i = 0; i < maxAttempts; i++)
            {
                var candidate = allPoints[UnityEngine.Random.Range(0, allPoints.Length)];
                if (candidate == null) continue;

                Vector3 pos = candidate.transform.position;
                if (PlayerHandler.instance == null ||
                    !PlayerHandler.instance.CanAnAlivePlayerSeePoint(pos, out _))
                {
                    return pos;
                }
            }

            // No LOS-clear point in 100 attempts — take any point (mirrors
            // RoundSpawner's fallback).
            var anyPoint = allPoints[UnityEngine.Random.Range(0, allPoints.Length)];
            if (anyPoint != null)
            {
                Plugin.Logger.LogInfo(
                    "[TrapHandler] MonsterSpawn: no LOS-clear point in 100 attempts — using any PatrolPoint.");
                return anyPoint.transform.position;
            }

            return PlayerCenteredFallback(local);
        }

        private static Vector3 PlayerCenteredFallback(Player local)
        {
            Vector2 circle = UnityEngine.Random.insideUnitCircle * FallbackSpawnOffsetRadius;
            return local.Center() + new Vector3(circle.x, 0f, circle.y);
        }

        // ================================================================== Teleport-closer coroutine

        /// <summary>
        /// Waits <paramref name="delay"/> seconds, then invokes
        /// <c>Bot.Teleport(Level.GetClosestHiddenPoint(player.Center()))</c>
        /// — the vanilla "ambush from out of sight near the player" pattern
        /// used by <c>Puppetmonster.Intervene</c>.  Both target methods are
        /// <c>internal</c>, so this goes through reflection.
        /// </summary>
        private static IEnumerator TeleportCloserAfterDelay(Bot bot, float delay, string monsterName)
        {
            yield return new WaitForSeconds(delay);

            if (bot == null || bot.gameObject == null)
            {
                Plugin.Logger.LogInfo(
                    $"[TrapHandler] MonsterSpawn: '{monsterName}' was destroyed before teleport-closer fired.");
                yield break;
            }

            var local = Player.localPlayer;
            if ((object)local == null)
            {
                Plugin.Logger.LogInfo(
                    $"[TrapHandler] MonsterSpawn: local player gone before teleport-closer fired — " +
                    $"leaving '{monsterName}' at original spawn point.");
                yield break;
            }

            EnsureReflectionInitialised();
            if (_botTeleport == null || _getClosestHiddenPoint == null || Level.currentLevel == null)
            {
                Plugin.Logger.LogWarning(
                    "[TrapHandler] MonsterSpawn: teleport-closer reflection unavailable — " +
                    "monster will not ambush.");
                yield break;
            }

            object? hiddenPoint;
            try
            {
                hiddenPoint = _getClosestHiddenPoint.Invoke(
                    Level.currentLevel, new object[] { local.Center(), false });
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[TrapHandler] MonsterSpawn: GetClosestHiddenPoint threw: {ex.Message}");
                yield break;
            }

            if (hiddenPoint is not PatrolPoint pp || pp == null)
            {
                Plugin.Logger.LogInfo(
                    "[TrapHandler] MonsterSpawn: no hidden point near player — leaving monster at spawn.");
                yield break;
            }

            try
            {
                _botTeleport.Invoke(bot, new object[] { pp.transform.position });
                Plugin.Logger.LogInfo(
                    $"[TrapHandler] MonsterSpawn: teleported '{monsterName}' to {pp.transform.position} " +
                    $"(near {local.refs.view.Controller.NickName}).");
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[TrapHandler] MonsterSpawn: Bot.Teleport threw: {ex.Message}");
            }
        }

        private static void EnsureReflectionInitialised()
        {
            if (_reflectionInitialised) return;
            _reflectionInitialised = true;

            _botTeleport = AccessTools.Method(typeof(Bot), "Teleport", new[] { typeof(Vector3) });
            if (_botTeleport == null)
            {
                Plugin.Logger.LogWarning(
                    "[TrapHandler] AccessTools could not resolve Bot.Teleport(Vector3).");
            }

            _getClosestHiddenPoint = AccessTools.Method(
                typeof(Level), "GetClosestHiddenPoint", new[] { typeof(Vector3), typeof(bool) });
            if (_getClosestHiddenPoint == null)
            {
                Plugin.Logger.LogWarning(
                    "[TrapHandler] AccessTools could not resolve Level.GetClosestHiddenPoint(Vector3, bool).");
            }
        }

        // ================================================================== Spawn retry coroutine

        private static IEnumerator RetrySpawnAfterDelay(Player local, string chosenMonster, float delay)
        {
            Plugin.Logger.LogInfo(
                $"[TrapHandler] MonsterSpawn (retry): waiting {delay} s for scene to finish " +
                $"initialising before spawning '{chosenMonster}'…");
            yield return new WaitForSeconds(delay);

            if (BotHandler.instance == null)
            {
                Plugin.Logger.LogWarning(
                    $"[TrapHandler] MonsterSpawn (retry): BotHandler.instance is still null " +
                    $"after {delay} s — spawn of '{chosenMonster}' aborted.");
                yield break;
            }

            // Re-pick the spawn position now that BotHandler is up — the
            // earlier ChooseRemoteSpawnPosition call would have run before
            // PatrolPoints were registered.
            Vector3 spawnPos = ChooseRemoteSpawnPosition(local);

            Plugin.Logger.LogInfo(
                $"[TrapHandler] MonsterSpawn (retry): spawning '{chosenMonster}' at {spawnPos}.");

            GameObject? spawned;
            try
            {
                spawned = MonsterSpawner.SpawnMonster(chosenMonster, spawnPos);
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[TrapHandler] MonsterSpawn (retry) failed for '{chosenMonster}': {ex.Message}");
                yield break;
            }

            if (spawned == null) yield break;

            var bot = spawned.GetComponent<Bot>() ?? spawned.GetComponentInChildren<Bot>();
            if (bot == null) yield break;

            float teleportDelay = UnityEngine.Random.Range(3f, 5f);
            Plugin.Instance.StartCoroutine(TeleportCloserAfterDelay(bot, teleportDelay, chosenMonster));
        }
    }
}
