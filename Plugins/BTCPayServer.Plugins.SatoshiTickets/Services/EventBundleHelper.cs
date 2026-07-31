using System;
using BTCPayServer.Plugins.SatoshiTickets.Data;

namespace BTCPayServer.Plugins.SatoshiTickets.Services;

public static class EventBundleHelper
{
    public static void ApplyBundleFields(Event entity, int perAdmission, Guid? raffleId)
    {
        entity.BundledRaffleTicketsPerAdmission = Math.Max(0, perAdmission);
        entity.BundledRaffleId = entity.BundledRaffleTicketsPerAdmission > 0 ? raffleId : null;
    }
}
