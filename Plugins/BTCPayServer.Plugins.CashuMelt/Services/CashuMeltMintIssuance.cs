using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>Durable issuance intent and NUT-09 recovery, before any recoverable bearer asset exists.</summary>
public sealed class CashuMeltMintIssuance
{
    private readonly CashuMeltMintClient _mint;
    private readonly CashuMeltAssetProtector _assets;
    public CashuMeltMintIssuance(CashuMeltMintClient mint, CashuMeltAssetProtector assets)
    { _mint = mint; _assets = assets; }

    public sealed record Output(long Amount, string Secret, string R, BlindedMessage Blinded);
    public sealed record Intent(string MintUrl, string QuoteId, string Unit, long Amount, MintKeyset Keyset, Output[] Outputs);

    public async Task<CashuMeltProof[]> IssueAsync(string mintUrl, string quoteId, string unit, long amount,
        bool alreadyIssued, string? savedIntent, Func<string, Task> persistIntent,
        Func<CashuMeltProof[], Task> persistProofs, CancellationToken ct)
    {
        Intent intent;
        var recovering = !string.IsNullOrEmpty(savedIntent);
        if (recovering)
        {
            intent = JsonSerializer.Deserialize<Intent>(_assets.Unprotect(savedIntent!))
                ?? throw new InvalidOperationException("Invalid persisted issuance intent");
            if (intent.MintUrl != mintUrl || intent.QuoteId != quoteId || intent.Unit != unit || intent.Amount != amount)
                throw new InvalidOperationException("Issuance intent does not match the original payment");
        }
        else
        {
            if (alreadyIssued) throw new InvalidOperationException("Mint already issued tokens without a recoverable issuance intent. Manual recovery required.");
            var keys = await _mint.GetKeysAsync(mintUrl, ct);
            var keyset = keys?.Keysets?.FirstOrDefault(k => k.Unit == unit)
                ?? throw new InvalidOperationException("Mint keyset unavailable");
            var outputs = CashuMeltCrypto.DecomposeAmount(amount).Select(denomination =>
            {
                if (!keyset.Keys.ContainsKey(denomination.ToString())) throw new InvalidOperationException("Missing denomination key");
                var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                var (blinded, r) = CashuMeltCrypto.CreateBlindedMessage(Encoding.UTF8.GetBytes(secret));
                return new Output(denomination, secret, Convert.ToHexString(r), new BlindedMessage(denomination, keyset.Id, blinded));
            }).ToArray();
            if (outputs.Length == 0) throw new InvalidOperationException("Invalid mint amount");
            intent = new Intent(mintUrl, quoteId, unit, amount, keyset, outputs);
            // This save must complete before sending the mint request.
            await persistIntent(_assets.Protect(JsonSerializer.Serialize(intent)));
        }

        var messages = intent.Outputs.Select(o => o.Blinded).ToArray();
        BlindSignature[]? signatures = null;
        if (recovering)
        {
            var restored = await _mint.RestoreSignaturesAsync(mintUrl, messages, ct);
            if (restored?.Outputs is null || restored.Signatures is null || restored.Outputs.Length != restored.Signatures.Length)
                throw new InvalidOperationException("Mint did not provide a valid NUT-09 recovery response");
            if (restored.Outputs.Length > 0)
            {
                if (restored.Outputs.Length != messages.Length) throw new InvalidOperationException("Partial issuance requires manual recovery");
                signatures = messages.Select(m =>
                {
                    var matches = restored.Outputs.Select((o, i) => (o, i)).Where(x => x.o.B_ == m.B_ && x.o.Id == m.Id).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException("Restored outputs do not match issuance intent");
                    return restored.Signatures[matches[0].i];
                }).ToArray();
            }
            else if (alreadyIssued) throw new InvalidOperationException("Issued signatures could not be restored; preserving issuance intent");
        }
        signatures ??= (await _mint.MintTokensAsync(mintUrl, quoteId, messages, ct))?.Signatures;
        if (signatures?.Length != intent.Outputs.Length) throw new InvalidOperationException("Unexpected mint signatures");
        var proofs = intent.Outputs.Select((o, i) =>
        {
            var sig = signatures![i];
            if (sig.Id != intent.Keyset.Id || sig.Amount != o.Amount) throw new InvalidOperationException("Mint signature keyset or amount mismatch");
            var c = CashuMeltCrypto.UnblindSignature(sig.C_, intent.Keyset.Keys[o.Amount.ToString()], Convert.FromHexString(o.R));
            return new CashuMeltProof(o.Amount, sig.Id, o.Secret, c);
        }).ToArray();
        await persistProofs(proofs);
        return proofs;
    }
}
