using System;
using BTCPayServer.Plugins.SatfluxTickets.Data;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

public static class EventBundleHelper
{
    public static void ApplyBundleFields(Event entity, int perAdmission, Guid? raffleId)
    {
        entity.BundledRaffleTicketsPerAdmission = Math.Max(0, perAdmission);
        entity.BundledRaffleId = entity.BundledRaffleTicketsPerAdmission > 0 ? raffleId : null;
    }
}
