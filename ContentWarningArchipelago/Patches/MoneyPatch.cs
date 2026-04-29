// Patches/MoneyPatch.cs
// Postfix on ShopHandler.InitShop — modelled directly on
// Starting_budget/Patches/ShopHandlerPatch.cs.
//
// PURPOSE
// When AP items that grant in-game currency are received, ItemData.cs tries
// an immediate grant.  If RoomStats isn't ready (or this client isn't the
// master) the amount is stored in pendingMoney; this patch drains it on the
// next shop init.  Meta Coins now flow exclusively through the lobby's AP
// DataStorage key (issue #10) and have no pending queue.

using ContentWarningArchipelago.Core;
using HarmonyLib;
using Photon.Pun;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Drains pending AP money into the shared shop wallet whenever the shop
    /// initialises for a new day.  Only the master client may call AddMoney.
    /// </summary>
    [HarmonyPatch(typeof(ShopHandler))]
    internal static class MoneyPatch
    {
        [HarmonyPatch(nameof(ShopHandler.InitShop))]
        [HarmonyPostfix]
        private static void InitShopPostfix(ShopHandler __instance)
        {
            if (!PhotonNetwork.IsMasterClient) return;

            int pendingMoney = APSave.saveData.pendingMoney;
            if (pendingMoney <= 0) return;

            Plugin.Logger.LogInfo(
                $"[MoneyPatch] Draining {pendingMoney} pending AP money into shop wallet.");

            __instance.m_RoomStats.AddMoney(pendingMoney);

            APSave.saveData.pendingMoney = 0;
            APSave.Flush();
        }
    }
}
