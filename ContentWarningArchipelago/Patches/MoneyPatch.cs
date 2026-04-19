// Patches/MoneyPatch.cs
// Postfix on ShopHandler.InitShop — modelled directly on
// Starting_budget/Patches/ShopHandlerPatch.cs.
//
// PURPOSE
// When AP items that grant in-game currency are received, ItemData.cs first
// tries an immediate grant.  If the subsystem isn't ready the amount is stored
// in a pending field on APSaveData.  This patch drains both pending queues
// every time the shop initialises (start of each new day).
//
// PENDING MONEY — lobby-shared resource (master client only)
//   Only the master client may call RoomStats.AddMoney.  Non-master clients
//   accumulate pendingMoney until they become master, or it is applied via the
//   host (see LateJoinSyncPatch).
//
// PENDING META COINS — per-player resource (all clients)
//   MetaProgressionHandler is a per-player singleton and its AddMetaCoins call
//   is safe to run on every client.  The master-client guard must NOT cover this
//   drain so every player receives their queued meta coins.

using ContentWarningArchipelago.Core;
using HarmonyLib;
using Photon.Pun;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Drains pending AP currency into the game whenever the shop initialises
    /// for a new day.
    /// </summary>
    [HarmonyPatch(typeof(ShopHandler))]
    internal static class MoneyPatch
    {
        [HarmonyPatch(nameof(ShopHandler.InitShop))]
        [HarmonyPostfix]
        private static void InitShopPostfix(ShopHandler __instance)
        {
            // ── Pending lobby money (master client only) ───────────────────────────
            if (PhotonNetwork.IsMasterClient)
            {
                int pendingMoney = APSave.saveData.pendingMoney;
                if (pendingMoney > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[MoneyPatch] Draining {pendingMoney} pending AP money into shop wallet.");

                    __instance.m_RoomStats.AddMoney(pendingMoney);

                    APSave.saveData.pendingMoney = 0;
                    APSave.Flush();
                }
            }

            // ── Pending Meta Coins (all clients — per-player currency) ─────────────
            // MetaProgressionHandler is per-player; every client drains their own
            // queue independently.  No master-client guard needed here.
            int pendingMC = APSave.saveData.pendingMetaCoins;
            if (pendingMC > 0)
            {
                Plugin.Logger.LogInfo(
                    $"[MoneyPatch] Draining {pendingMC} pending AP Meta Coins into MetaProgressionHandler.");

                try
                {
                    MetaProgressionHandler.AddMetaCoins(pendingMC);
                    APSave.saveData.pendingMetaCoins = 0;
                    APSave.Flush();
                }
                catch (System.Exception ex)
                {
                    Plugin.Logger.LogWarning(
                        $"[MoneyPatch] MetaProgressionHandler.AddMetaCoins failed: {ex.Message}. " +
                        $"Will retry on next InitShop.");
                }
            }
        }
    }
}
