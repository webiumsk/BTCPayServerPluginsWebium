#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.Flash;

/// <summary>
/// Persists the (in-memory) tracked-invoice registry to BTCPay settings so payment detection survives a
/// BTCPay restart: on startup the non-expired invoices are re-seeded and the shared poller re-polls their
/// LUD-21 verify URLs (detecting any settlement that happened while BTCPay was down). Stored as a single
/// settings blob — adequate for typical volumes; a per-row table would scale better (future work).
/// </summary>
public sealed class FlashPersistence
{
    public const string SettingName = "Flash.TrackedInvoices";
    private readonly ISettingsRepository _settings;

    public FlashPersistence(ISettingsRepository settings) => _settings = settings;

    public async Task SaveAsync()
    {
        var snapshot = new PersistedTrackedInvoices
        {
            Invoices = TrackedInvoiceRegistry.All().Select(t => new PersistedInvoice
            {
                PaymentHash = t.PaymentHash,
                Bolt11 = t.Bolt11,
                VerifyUrl = t.VerifyUrl,
                VerifyHost = t.VerifyHost,
                PayEndpoint = t.PayEndpoint,
                ExpiresAtUnix = t.ExpiresAt.ToUnixTimeSeconds(),
                AmountMsat = t.AmountMsat
            }).ToList()
        };
        await _settings.UpdateSetting(snapshot, SettingName);
    }

    public async Task LoadAsync()
    {
        var persisted = await _settings.GetSettingAsync<PersistedTrackedInvoices>(SettingName);
        if (persisted?.Invoices is null) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var p in persisted.Invoices)
        {
            // A corrupted record (e.g. an out-of-range timestamp) must not abort recovery of the rest.
            DateTimeOffset expiresAt;
            try { expiresAt = DateTimeOffset.FromUnixTimeSeconds(p.ExpiresAtUnix); }
            catch (ArgumentOutOfRangeException) { continue; }
            if (expiresAt <= now) continue; // don't re-arm invoices that already expired while down
            if (string.IsNullOrEmpty(p.PaymentHash) || string.IsNullOrEmpty(p.VerifyUrl)) continue;
            // Same SSRF policy as at accept time — never re-arm a verify URL we would not accept now
            // (the persisted blob is not more trustworthy than the JSON it came from).
            if (!Uri.TryCreate(p.VerifyUrl, UriKind.Absolute, out var verifyUri) ||
                !FlashHttp.IsSafeUrl(verifyUri, out _)) continue;
            TrackedInvoiceRegistry.Add(new TrackedInvoice(
                p.PaymentHash, p.Bolt11, p.VerifyUrl, p.VerifyHost, p.PayEndpoint, expiresAt, p.AmountMsat));
        }
    }
}

public class PersistedTrackedInvoices
{
    public List<PersistedInvoice> Invoices { get; set; } = new();
}

public class PersistedInvoice
{
    public string PaymentHash { get; set; } = "";
    public string Bolt11 { get; set; } = "";
    public string VerifyUrl { get; set; } = "";
    public string VerifyHost { get; set; } = "";
    public string PayEndpoint { get; set; } = "";
    public long ExpiresAtUnix { get; set; }
    public long AmountMsat { get; set; }
}
