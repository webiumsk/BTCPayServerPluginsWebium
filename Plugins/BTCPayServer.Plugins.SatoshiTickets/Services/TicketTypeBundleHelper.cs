using System;
using BTCPayServer.Plugins.SatoshiTickets.Data;

namespace BTCPayServer.Plugins.SatoshiTickets.Services;

public static class TicketTypeBundleHelper
{
    public static void ApplyBundleFields(TicketType entity, int perAdmission, Guid? raffleId)
    {
        entity.BundledRaffleTicketsPerAdmission = Math.Max(0, perAdmission);
        entity.BundledRaffleId = entity.BundledRaffleTicketsPerAdmission > 0 ? raffleId : null;
    }
}
