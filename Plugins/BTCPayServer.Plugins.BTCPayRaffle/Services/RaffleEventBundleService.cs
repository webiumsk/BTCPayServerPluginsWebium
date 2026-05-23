#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public sealed class RaffleEventBundleService : IRaffleEventBundleService
{
    private const int MaxPerCall = 100;

    private readonly RaffleService _raffle;
    private readonly RaffleTicketEmailService _ticketEmail;
    private readonly RaffleStringLocalizer _localizer;
    private readonly ILogger<RaffleEventBundleService> _logger;

    public RaffleEventBundleService(
        RaffleService raffle,
        RaffleTicketEmailService ticketEmail,
        RaffleStringLocalizer localizer,
        ILogger<RaffleEventBundleService> logger)
    {
        _raffle = raffle;
        _ticketEmail = ticketEmail;
        _localizer = localizer;
        _logger = logger;
    }

    public async Task<(bool Ok, string? Error)> ValidateBundledRaffleAsync(string storeId, Guid raffleId)
    {
        var raffle = await _raffle.GetRaffleAsync(raffleId);
        if (raffle is null)
            return (false, "Raffle not found");
        if (!string.Equals(raffle.StoreId, storeId, StringComparison.Ordinal))
            return (false, "Raffle does not belong to this store");
        if (raffle.Status != RaffleStatus.Open)
            return (false, "Raffle must be open for ticket sales (Open)");
        return (true, null);
    }

    public async Task<RaffleEventBundleResult> AllocateForEventOrderAsync(
        string storeId,
        Guid raffleId,
        int count,
        string buyerEmail,
        string? buyerName,
        string eventOrderId,
        string baseUrl)
    {
        if (count < 1)
            return RaffleEventBundleResult.Skipped();

        if (string.IsNullOrWhiteSpace(eventOrderId))
            return RaffleEventBundleResult.Fail("Event order id is required for raffle bundle");

        var (valid, validationError) = await ValidateBundledRaffleAsync(storeId, raffleId);
        if (!valid)
            return RaffleEventBundleResult.Fail(validationError ?? "Raffle validation failed");

        var normalizedEmail = RaffleBuyerEmail.Normalize(buyerEmail);
        if (string.IsNullOrEmpty(normalizedEmail))
            return RaffleEventBundleResult.Fail("Buyer email is required for raffle bundle");

        var invoiceId = RaffleTicketIds.EventBundle(eventOrderId.Trim(), normalizedEmail);
        var allocated = 0;
        var isNew = false;

        try
        {
            if (count > MaxPerCall)
                return RaffleEventBundleResult.Fail($"Cannot allocate more than {MaxPerCall} raffle tickets per buyer per order");

            var (tickets, batchIsNew) = await _raffle.AddBundleTicketsAsync(
                raffleId, count, invoiceId, normalizedEmail, buyerName);
            isNew = batchIsNew;
            allocated = tickets.Count;

            if (isNew && allocated > 0)
            {
                var raffle = await _raffle.GetRaffleAsync(raffleId);
                if (raffle is not null && !string.IsNullOrWhiteSpace(baseUrl))
                {
                    var allTickets = await _raffle.GetTicketsByBuyerAsync(raffleId, normalizedEmail);
                    var bundleTickets = allTickets
                        .Where(t => t.InvoiceId == invoiceId)
                        .OrderBy(t => t.TicketNumber)
                        .ToList();
                    if (bundleTickets.Count > 0)
                    {
                        await _ticketEmail.SendTicketsEmailAsync(
                            raffleId,
                            raffle.Name,
                            normalizedEmail,
                            buyerName,
                            bundleTickets,
                            baseUrl,
                            receiptUrl: null,
                            manualAllocation: true,
                            introOverride: _localizer["email.event_bundle_intro"]);
                    }
                }
            }

            return RaffleEventBundleResult.Ok(allocated, isNew);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Event raffle bundle failed (store={StoreId}, raffle={RaffleId}, order={OrderId})",
                storeId, raffleId, eventOrderId);
            return RaffleEventBundleResult.Fail(
                "An internal error occurred while processing the raffle event");
        }
    }
}
