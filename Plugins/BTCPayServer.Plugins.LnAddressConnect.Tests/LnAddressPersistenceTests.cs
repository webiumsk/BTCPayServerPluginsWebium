using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.LnAddressConnect;
using Xunit;

namespace BTCPayServer.Plugins.LnAddressConnect.Tests;

public class LnAddressPersistenceTests
{
    static string Uniq(string p) => p + Guid.NewGuid().ToString("N").Substring(0, 8);

    [Fact]
    public async Task Load_restores_non_expired_and_skips_expired()
    {
        var settings = new FakeSettings();
        var live = Uniq("plive_");
        var expired = Uniq("pexp_");
        // Pre-store a self-contained snapshot (not via SaveAsync, to avoid capturing parallel tests' entries).
        var snapshot = new PersistedTrackedInvoices
        {
            Invoices = new()
            {
                new PersistedInvoice { PaymentHash = live, Bolt11 = "lnbc1", VerifyUrl = $"https://h.example/verify/{live}", VerifyHost = "h.example", PayEndpoint = "https://h.example/pay", ExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
                new PersistedInvoice { PaymentHash = expired, Bolt11 = "lnbc1", VerifyUrl = $"https://h.example/verify/{expired}", VerifyHost = "h.example", PayEndpoint = "https://h.example/pay", ExpiresAtUnix = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds() },
            }
        };
        await settings.UpdateSetting(snapshot, LnAddressPersistence.SettingName);

        await new LnAddressPersistence(settings).LoadAsync();

        Assert.True(TrackedInvoiceRegistry.TryGet(live, out var restored));
        Assert.Equal("lnbc1", restored.Bolt11);
        Assert.Equal($"https://h.example/verify/{live}", restored.VerifyUrl);
        Assert.False(TrackedInvoiceRegistry.TryGet(expired, out _)); // expired -> not re-armed

        TrackedInvoiceRegistry.Remove(live);
    }

    [Fact]
    public async Task Load_migrates_tracked_invoices_from_legacy_blitz_and_flash_settings()
    {
        var settings = new FakeSettings();
        var fromBlitz = Uniq("pblitz_");
        var fromFlash = Uniq("pflash_");
        var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        await settings.UpdateSetting(new PersistedTrackedInvoices
        {
            Invoices = new()
            {
                new PersistedInvoice { PaymentHash = fromBlitz, Bolt11 = "lnbc1", VerifyUrl = $"https://b.example/verify/{fromBlitz}", VerifyHost = "b.example", PayEndpoint = "https://b.example/pay", ExpiresAtUnix = expires },
            }
        }, "Blitz.TrackedInvoices");
        await settings.UpdateSetting(new PersistedTrackedInvoices
        {
            Invoices = new()
            {
                new PersistedInvoice { PaymentHash = fromFlash, Bolt11 = "lnbc1", VerifyUrl = $"https://f.example/verify/{fromFlash}", VerifyHost = "f.example", PayEndpoint = "https://f.example/pay", ExpiresAtUnix = expires },
            }
        }, "Flash.TrackedInvoices");

        await new LnAddressPersistence(settings).LoadAsync();

        // In-flight invoices from the superseded plugins survive the upgrade.
        Assert.True(TrackedInvoiceRegistry.TryGet(fromBlitz, out _));
        Assert.True(TrackedInvoiceRegistry.TryGet(fromFlash, out _));

        TrackedInvoiceRegistry.Remove(fromBlitz);
        TrackedInvoiceRegistry.Remove(fromFlash);
    }

    [Fact]
    public async Task Load_skips_record_with_invalid_timestamp_but_restores_the_rest()
    {
        var settings = new FakeSettings();
        var corrupt = Uniq("pcorrupt_");
        var live = Uniq("pok_");
        var snapshot = new PersistedTrackedInvoices
        {
            Invoices = new()
            {
                // Out-of-range unix timestamp: DateTimeOffset.FromUnixTimeSeconds would throw — the
                // record must be skipped without aborting recovery of the records after it.
                new PersistedInvoice { PaymentHash = corrupt, Bolt11 = "lnbc1", VerifyUrl = $"https://h.example/verify/{corrupt}", VerifyHost = "h.example", PayEndpoint = "https://h.example/pay", ExpiresAtUnix = long.MaxValue },
                new PersistedInvoice { PaymentHash = live, Bolt11 = "lnbc1", VerifyUrl = $"https://h.example/verify/{live}", VerifyHost = "h.example", PayEndpoint = "https://h.example/pay", ExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
            }
        };
        await settings.UpdateSetting(snapshot, LnAddressPersistence.SettingName);

        await new LnAddressPersistence(settings).LoadAsync();

        Assert.False(TrackedInvoiceRegistry.TryGet(corrupt, out _));
        Assert.True(TrackedInvoiceRegistry.TryGet(live, out _));
        TrackedInvoiceRegistry.Remove(live);
    }

    [Fact]
    public async Task Load_skips_invoices_with_unsafe_verify_urls()
    {
        var settings = new FakeSettings();
        var unsafe1 = Uniq("punsafe1_");
        var unsafe2 = Uniq("punsafe2_");
        var snapshot = new PersistedTrackedInvoices
        {
            Invoices = new()
            {
                new PersistedInvoice { PaymentHash = unsafe1, Bolt11 = "lnbc1", VerifyUrl = "http://127.0.0.1/verify/x", VerifyHost = "127.0.0.1", PayEndpoint = "https://h.example/pay", ExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
                new PersistedInvoice { PaymentHash = unsafe2, Bolt11 = "lnbc1", VerifyUrl = "https://192.168.1.1/verify/x", VerifyHost = "192.168.1.1", PayEndpoint = "https://h.example/pay", ExpiresAtUnix = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
            }
        };
        await settings.UpdateSetting(snapshot, LnAddressPersistence.SettingName);

        await new LnAddressPersistence(settings).LoadAsync();

        Assert.False(TrackedInvoiceRegistry.TryGet(unsafe1, out _)); // http -> not re-armed
        Assert.False(TrackedInvoiceRegistry.TryGet(unsafe2, out _)); // IP literal -> not re-armed
    }

    [Fact]
    public async Task Save_writes_the_tracked_invoice_to_settings_and_amount_survives_reload()
    {
        var settings = new FakeSettings();
        var hash = Uniq("psave_");
        TrackedInvoiceRegistry.Add(new TrackedInvoice(
            hash, "lnbc1", $"https://h.example/verify/{hash}", "h.example", "https://h.example/pay",
            DateTimeOffset.UtcNow.AddHours(1), AmountMsat: 21_000));

        await new LnAddressPersistence(settings).SaveAsync();

        var stored = await settings.GetSettingAsync<PersistedTrackedInvoices>(LnAddressPersistence.SettingName);
        Assert.NotNull(stored);
        Assert.Contains(stored!.Invoices, i => i.PaymentHash == hash && i.Bolt11 == "lnbc1" && i.AmountMsat == 21_000);

        // The amount round-trips through a reload (BuildInvoice uses it for Amount/AmountReceived).
        TrackedInvoiceRegistry.Remove(hash);
        await new LnAddressPersistence(settings).LoadAsync();
        Assert.True(TrackedInvoiceRegistry.TryGet(hash, out var reloaded));
        Assert.Equal(21_000, reloaded.AmountMsat);

        TrackedInvoiceRegistry.Remove(hash);
    }
}

/// <summary>In-memory ISettingsRepository that round-trips through JSON (like the real one) for tests.</summary>
sealed class FakeSettings : ISettingsRepository
{
    private readonly Dictionary<string, string> _store = new();

    public Task<T?> GetSettingAsync<T>(string? name = null) where T : class
        => Task.FromResult(_store.TryGetValue(name ?? typeof(T).FullName!, out var v)
            ? Newtonsoft.Json.JsonConvert.DeserializeObject<T>(v)
            : null);

    public Task UpdateSetting<T>(T obj, string? name = null) where T : class
    {
        _store[name ?? typeof(T).FullName!] = Newtonsoft.Json.JsonConvert.SerializeObject(obj);
        return Task.CompletedTask;
    }

    public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class
        => throw new NotImplementedException();
}
