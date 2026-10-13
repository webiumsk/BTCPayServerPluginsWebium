#nullable enable
using System;
using System.Text.Json;
using System.Net.Http;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.CashuMelt.Data;
using BTCPayServer.Plugins.CashuMelt.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>Creates durable, opaque checkout quotes with original payout settings before exposing an invoice.</summary>
public sealed class CashuMeltQuoteService
{
    private readonly CashuMeltMintClient _mintClient;
    private readonly CashuMeltConfigService _configService;
    private readonly CashuMeltDbContextFactory _dbContextFactory;
    private readonly CashuMeltAssetProtector _assets;
    private readonly ILogger<CashuMeltQuoteService> _logger;

    public CashuMeltQuoteService(CashuMeltMintClient mintClient, CashuMeltConfigService configService,
        CashuMeltDbContextFactory dbContextFactory, CashuMeltAssetProtector assets, ILogger<CashuMeltQuoteService> logger)
    {
        _mintClient = mintClient;
        _configService = configService;
        _dbContextFactory = dbContextFactory;
        _assets = assets;
        _logger = logger;
    }

    public async Task<(string? QuoteId, string? Bolt11, string? Error)> CreateQuoteAsync(
        string invoiceId,
        string storeId,
        long amountSats,
        string unit,
        CancellationToken ct = default)
    {
        var settings = await _configService.GetEnabledSettingsAsync(storeId, ct);
        if (settings is null)
            return (null, null, "CashuMelt not configured for this store");

        try
        {
            CashuMeltMintPolicy.ValidateStoreMintAgainstTrustedList(settings);
        }
        catch (InvalidOperationException ex)
        {
            return (null, null, ex.Message);
        }

        await using var storeLease = await CashuMeltDatabaseLease.TryAcquireAsync(_dbContextFactory, "cashumelt:store:" + storeId, true, ct);
        if (storeLease is null) return (null, null, "CashuMelt settings update in progress");
        settings = await _configService.GetEnabledSettingsAsync(storeId, ct);
        if (settings is null) return (null, null, "CashuMelt not configured");
        if (amountSats <= CashuMeltFeePolicy.EstimateFeeBufferSat(amountSats))
            return (null, null, "Payment amount is too small to cover the Lightning payout reserve");
        if (unit != settings.Unit) return (null, null, "CashuMelt configuration changed during invoice creation");
        if (unit != "sat") return (null, null, "Lightning settlement currently requires a sat-denominated mint");
        MintQuoteBolt11Response? quote;
        DateTimeOffset? expiresAt;
        try
        {
            if (!await _mintClient.SupportsRestoreAsync(settings.MintUrl, ct))
                return (null, null, "Mint must support NUT-09 recovery before accepting payments");
            quote = await _mintClient.CreateMintQuoteAsync(settings.MintUrl, amountSats, unit, ct);
            if (quote is null || string.IsNullOrWhiteSpace(quote.Quote))
                return (null, null, "Mint did not return a valid quote");
            if (quote.HasBeenPaid || quote.HasBeenIssued) return (null, null, "Mint returned an already-funded quote");
            if (quote.Request is null) return (null, null, "Mint did not return an invoice");
            CashuMeltSettlementVerification.ValidateInvoice(quote.Request, amountSats);
            if (quote.Unit != unit || quote.Amount != amountSats) return (null, null, "Mint quote amount or unit mismatch");
            expiresAt = quote.Expiry is > 0 ? DateTimeOffset.FromUnixTimeSeconds(quote.Expiry.Value) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, null, "Mint quote request timed out; try again later");
        }
        catch (HttpRequestException)
        {
            return (null, null, "Could not verify mint recovery support; try again later");
        }
        catch (JsonException)
        {
            return (null, null, "Mint returned an invalid recovery response");
        }
        catch (FormatException)
        {
            return (null, null, "Mint returned an invalid Lightning invoice");
        }
        catch (ArgumentException)
        {
            return (null, null, "Mint returned invalid payment quote data");
        }
        catch (InvalidOperationException)
        {
            return (null, null, "Mint returned an invalid Lightning invoice");
        }

        var req = new CashuMeltPaymentRequest
        {
            QuoteId        = Guid.NewGuid().ToString("N"),
            MintQuoteId = _assets.Protect(quote.Quote),
            MintUrl = settings.MintUrl,
            SettingsSnapshotJson = JsonSerializer.Serialize(settings),
            QuoteExpiresAt = expiresAt,
            InvoiceId      = invoiceId,
            StoreId        = storeId,
            AmountSats     = amountSats,
            Unit           = unit,
            Bolt11Invoice  = quote.Request,
            State          = quote.State,
            SettlementState = "PENDING",
            CreatedAt      = DateTimeOffset.UtcNow
        };

        await WithSchemaRetryAsync(async ctx =>
        {
            await ctx.CashuMeltPaymentRequests.AddAsync(req, ct);
            await ctx.SaveChangesAsync(ct);
        }, ct);

        _logger.LogInformation(
            "phase=mint_quote quote={QuoteId} invoice={InvoiceId} amountSat={AmountSat} unit={Unit}",
            req.QuoteId, invoiceId, amountSats, unit);

        return (req.QuoteId, quote.Request, null);
    }

    private async Task WithSchemaRetryAsync(Func<CashuMeltDbContext, Task> action, CancellationToken ct)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        try
        {
            await action(ctx);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
            await CashuMeltSchemaCreator.EnsureSchemaAndTablesAsync(ctx, ct);
            await using var retry = _dbContextFactory.CreateContext();
            await action(retry);
        }
    }
}
