using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>Encrypts temporary bearer assets; the BTCPay Data Protection key ring must be backed up.</summary>
public sealed class CashuMeltAssetProtector
{
    private const string Prefix = "protected:v1:";
    private readonly IDataProtector _protector;
    public CashuMeltAssetProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("CashuMelt.TemporaryAssets.v1");

    public string Protect(string value) => Prefix + _protector.Protect(value);
    // Legacy plaintext can be read for migration/recovery, but all new writes are protected.
    public string Unprotect(string value) => value.StartsWith(Prefix, StringComparison.Ordinal)
        ? _protector.Unprotect(value[Prefix.Length..]) : value;
}
