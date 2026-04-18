// Patches/MoneyPatch.cs
// Postfix on ShopHandler.InitShop — modelled directly on
// Starting_budget/Patches/ShopHandlerPatch.cs.
//
// PURPOSE
// When money AP items are received the handler in ItemData.cs first tries an
// immediate RoomStats.AddMoney() call.  If RoomStats is not yet ready (loading
// screen, non-master client, etc.) the amount is stored in
// APSave.saveData.pendingMoney instead.  This patch drains that pending amount
// every time the shop initialises (start of each new day), matching exactly the
// pattern used by StartingBudget.
//
// MASTER-CLIENT GUARD
// Money is a shared lobby resource; only the master client should call AddMoney.
// Non-master clients accumulate the pending amount and it gets applied if/when
// they become master, or the host applies it via a Photon RPC (see LateJoinSyncPatch).

using ContentWarningArchipelago.Core;
using HarmonyLib;
using Photon.Pun;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Drains APSave.saveData.pendingMoney into the shared wallet whenever the
    /// shop initialises for a new day (matches StartingBudget's approach exactly).
    /// </summary>
    [HarmonyPatch(typeof(ShopHandler))]
    internal static class MoneyPatch
    {
        [HarmonyPatch(nameof(ShopHandler.InitShop))]
        [HarmonyPostfix]
        private static void InitShopPostfix(ShopHandler __instance)
        {
            // Only the master client adds money (same guard StartingBudget uses).
            if (!PhotonNetwork.IsMasterClient) return;

            int pending = APSave.saveData.pendingMoney;
            if (pending <= 0) return;

            Plugin.Logger.LogInfo(
                $"[MoneyPatch] Draining {pending} pending AP money into shop wallet.");

            __instance.m_RoomStats.AddMoney(pending);

            // Clear the queue and persist.
            APSave.saveData.pendingMoney = 0;
            APSave.Flush();
        }
    }
}
