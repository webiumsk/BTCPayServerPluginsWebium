using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.CashuMelt.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static BTCPayServer.Plugins.CashuMelt.Services.CashuMeltMintClient;

namespace BTCPayServer.Plugins.CashuMelt.Tests;

public class CashuMeltIssuanceRecoveryTests
{
    private sealed class FakeMint : HttpMessageHandler
    {
        public int MintCalls;
        public int RestoreCalls;
        public bool LoseMintResponse;
        public readonly Dictionary<string, (BlindedMessage Output, BlindSignature Signature)> Issued = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object response;
            if (request.RequestUri!.AbsolutePath == "/v1/keys")
            {
                // Signing key = 1, so the mint's blinded signature equals B_. This exercises real unblinding.
                var key = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
                response = new MintKeysResponse(new[] { new MintKeyset("009a1f293253e41e", "sat", new Dictionary<string, string> { ["1"] = key, ["2"] = key, ["4"] = key }) });
            }
            else
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                var input = JsonSerializer.Deserialize<MintTokensRequest>(body)!;
                if (request.RequestUri.AbsolutePath == "/v1/mint/bolt11")
                {
                    MintCalls++;
                    Assert.Empty(Issued);
                    foreach (var o in input.Outputs) Issued[o.B_] = (o, new BlindSignature(o.Amount, o.Id, o.B_));
                    if (LoseMintResponse) throw new HttpRequestException("Response lost after issuance");
                    response = new MintTokensResponse(input.Outputs.Select(o => Issued[o.B_].Signature).ToArray());
                }
                else
                {
                    RestoreCalls++;
                    var restored = input.Outputs.Where(o => Issued.ContainsKey(o.B_)).Reverse().ToArray();
                    response = new RestoreResponse(restored, restored.Select(o => Issued[o.B_].Signature).ToArray());
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
    }

    private static CashuMeltMintIssuance Issuer(FakeMint mint, CashuMeltAssetProtector assets) =>
        new(new CashuMeltMintClient(new HttpClient(mint), NullLogger<CashuMeltMintClient>.Instance), assets);

    [Fact]
    public async Task Lost_mint_response_restores_the_original_proofs_without_a_second_issuance()
    {
        var mint = new FakeMint { LoseMintResponse = true };
        var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
        string? saved = null;
        await Assert.ThrowsAsync<HttpRequestException>(() => Issuer(mint, assets).IssueAsync("https://mint.example", "private-quote", "sat", 7,
            false, null, value => { saved = value; return Task.CompletedTask; }, _ => Task.CompletedTask, default));
        Assert.NotNull(saved);
        Assert.DoesNotContain("private-quote", saved);
        CashuMeltProof[]? committed = null;
        var proofs = await Issuer(mint, assets).IssueAsync("https://mint.example", "private-quote", "sat", 7,
            true, saved, _ => throw new Exception("Must reuse the durable intent"), p => { committed = p; return Task.CompletedTask; }, default);
        Assert.Equal(7, proofs.Sum(p => p.Amount));
        Assert.Equal(proofs, committed);
        Assert.Equal(1, mint.MintCalls);
        Assert.Equal(1, mint.RestoreCalls);
        foreach (var p in proofs) Assert.Equal(CashuMeltCrypto.ComputeYHex(Encoding.UTF8.GetBytes(p.Secret)), p.C);
    }

    [Fact]
    public async Task Failed_intent_commit_never_sends_the_mint_request()
    {
        var mint = new FakeMint();
        var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
        await Assert.ThrowsAsync<IOException>(() => Issuer(mint, assets).IssueAsync("https://mint.example", "private-quote", "sat", 7,
            false, null, _ => throw new IOException("Commit failed"), _ => Task.CompletedTask, default));
        Assert.Equal(0, mint.MintCalls);
    }

    [Fact]
    public async Task Lost_proof_commit_recovers_the_same_outputs_after_restart()
    {
        var mint = new FakeMint();
        var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
        string? saved = null;
        await Assert.ThrowsAsync<IOException>(() => Issuer(mint, assets).IssueAsync("https://mint.example", "private-quote", "sat", 7,
            false, null, value => { saved = value; return Task.CompletedTask; }, _ => throw new IOException("Crash before proof commit"), default));
        var proofs = await Issuer(mint, assets).IssueAsync("https://mint.example", "private-quote", "sat", 7,
            true, saved, _ => throw new Exception(), _ => Task.CompletedTask, default);
        Assert.Equal(7, proofs.Sum(p => p.Amount));
        Assert.Equal(1, mint.MintCalls);
    }

    [Fact]
    public async Task Issued_quote_without_intent_is_preserved_for_manual_recovery()
    {
        var mint = new FakeMint();
        var assets = new CashuMeltAssetProtector(new EphemeralDataProtectionProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Issuer(mint, assets).IssueAsync("https://mint.example", "private-quote", "sat", 7,
            true, null, _ => Task.CompletedTask, _ => Task.CompletedTask, default));
        Assert.Equal(0, mint.MintCalls);
    }
}
