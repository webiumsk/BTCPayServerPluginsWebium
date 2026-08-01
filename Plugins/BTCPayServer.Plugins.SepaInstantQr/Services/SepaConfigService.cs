using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BTCPayServer.Plugins.SepaInstantQr.Services;

/// <summary>Store settings CRUD + data protection for credential blobs.</summary>
public class SepaConfigService
{
    internal const string ProtectorPurpose = "SepaInstantQr";

    private readonly SepaDbContextFactory _dbContextFactory;
    private readonly IDataProtector _protector;

    public SepaConfigService(SepaDbContextFactory dbContextFactory, IDataProtectionProvider dataProtectionProvider)
    {
        _dbContextFactory = dbContextFactory;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    public async Task<SepaStoreSettings?> GetSettingsAsync(string storeId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var ctx = _dbContextFactory.CreateContext();
            return await ctx.SepaStoreSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.StoreId == storeId, cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // Table not created yet (first run before migrations) - behave
            // like "not configured".
            return null;
        }
    }

    public async Task<SepaStoreSettings?> GetEnabledSettingsAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(storeId, cancellationToken);
        return settings is { Enabled: true } ? settings : null;
    }

    public async Task SaveSettingsAsync(SepaStoreSettings settings, CancellationToken cancellationToken = default)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var existing = await ctx.SepaStoreSettings.FindAsync([settings.StoreId], cancellationToken);
        if (existing is null)
        {
            settings.CreatedAt = DateTimeOffset.UtcNow;
            await ctx.SepaStoreSettings.AddAsync(settings, cancellationToken);
        }
        else
        {
            existing.Enabled = settings.Enabled;
            existing.CountryProfile = settings.CountryProfile;
            existing.Iban = settings.Iban;
            existing.Beneficiary = settings.Beneficiary;
            existing.Bic = settings.Bic;
            existing.Message = settings.Message;
            existing.ConfirmationBackend = settings.ConfirmationBackend;
            existing.SkQrVariant = settings.SkQrVariant;
            existing.CheckoutConfirmEnabled = settings.CheckoutConfirmEnabled;
            existing.FioTokenFingerprint = settings.FioTokenFingerprint;
            existing.AmountTolerance = settings.AmountTolerance;
            // NOP identity travels with the certificate - persist both on
            // upload AND on clear (null overwrites stale values).
            existing.NopVatsk = settings.NopVatsk;
            existing.NopPokladnica = settings.NopPokladnica;
            if (settings.EncryptedCredentialsJson is not null)
                existing.EncryptedCredentialsJson = settings.EncryptedCredentialsJson;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await ctx.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Encrypts a credentials JSON blob at rest (never logged).</summary>
    public string ProtectCredentials(string plaintextJson)
        => _protector.Protect(plaintextJson);

    public string? UnprotectCredentials(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return null;

        try
        {
            return _protector.Unprotect(encrypted);
        }
        catch (Exception)
        {
            // Key ring changed - treat as unset so the merchant re-enters
            // credentials instead of the plugin failing hard.
            return null;
        }
    }

    /// <summary>
    /// Validates and stores the Fio token: trimmed, exactly 64 characters
    /// (Fio API Bankovnictví v1.9), and not already used by another store -
    /// the bank keeps the download cursor per token, so a shared token
    /// would make stores steal each other's movements. Returns null on
    /// success, otherwise a human-readable error. The unique index on the
    /// fingerprint is the transaction-safe backstop for concurrent saves.
    /// </summary>
    public async Task<string?> TrySetFioTokenAsync(
        SepaStoreSettings settings, string rawToken, CancellationToken cancellationToken = default)
    {
        var token = rawToken.Trim();
        if (token.Length != 64)
            return "The Fio API token must be exactly 64 characters.";

        var fingerprint = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

        await using var ctx = _dbContextFactory.CreateContext();
        var ownedElsewhere = await ctx.SepaStoreSettings
            .AsNoTracking()
            .AnyAsync(s => s.FioTokenFingerprint == fingerprint && s.StoreId != settings.StoreId, cancellationToken);
        if (ownedElsewhere)
            return "This Fio token is already used by another store - generate a separate token per store (the bank keeps the download cursor per token).";

        ApplyCredentials(settings, GetCredentials(settings) with { FioToken = token });
        settings.FioTokenFingerprint = fingerprint;
        return null;
    }

    public void ClearFioToken(SepaStoreSettings settings)
    {
        ApplyCredentials(settings, GetCredentials(settings) with { FioToken = null });
        settings.FioTokenFingerprint = null;
    }

    /// <summary>Decrypted backend credentials of a store (empty record when unset).</summary>
    public SepaBackendCredentials GetCredentials(SepaStoreSettings settings)
        => SepaBackendCredentials.FromJson(UnprotectCredentials(settings.EncryptedCredentialsJson));

    /// <summary>Encrypts and stores the credentials blob on the settings entity (caller saves).</summary>
    public void ApplyCredentials(SepaStoreSettings settings, SepaBackendCredentials credentials)
        => settings.EncryptedCredentialsJson = ProtectCredentials(credentials.ToJson());
}
