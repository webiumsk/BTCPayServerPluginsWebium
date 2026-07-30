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
            existing.AmountTolerance = settings.AmountTolerance;
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

    /// <summary>Decrypted backend credentials of a store (empty record when unset).</summary>
    public SepaBackendCredentials GetCredentials(SepaStoreSettings settings)
        => SepaBackendCredentials.FromJson(UnprotectCredentials(settings.EncryptedCredentialsJson));

    /// <summary>Encrypts and stores the credentials blob on the settings entity (caller saves).</summary>
    public void ApplyCredentials(SepaStoreSettings settings, SepaBackendCredentials credentials)
        => settings.EncryptedCredentialsJson = ProtectCredentials(credentials.ToJson());
}
