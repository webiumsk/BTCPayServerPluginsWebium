namespace BTCPayServer.Plugins.CashuMelt.Services;

public static class CashuMeltFailureReasons
{
    public const string MintPollError = "mint_poll_error";
    public const string TrustedMintViolation = "trusted_mint_violation";
    public const string MintProofFailed = "mint_proof_failed";
    public const string KeysetConflict = "keyset_conflict";
    public const string LightningAddressUnresolvable = "ln_address_unresolvable";
    public const string MeltQuoteFailed = "melt_quote_failed";
    public const string FeeTooHigh = "fee_too_high";
    public const string MeltFailed = "melt_failed";
    public const string AmountTooSmall = "amount_too_small";
    public const string MaxRetriesExceeded = "max_retries_exceeded";

    public static string Describe(string? code) => code switch
    {
        MintPollError => "Mint returned a permanent error while polling quote state",
        TrustedMintViolation => "Configured mint URL is not in the trusted mint list",
        MintProofFailed => "Failed to obtain proof tokens from mint",
        KeysetConflict => "Mint returned proofs with unexpected keyset ID (possible keyset collision)",
        LightningAddressUnresolvable => "Could not resolve merchant Lightning address via LNURL",
        MeltQuoteFailed => "Melt quote request failed",
        FeeTooHigh => "Lightning routing fee reserve exceeds configured cap",
        MeltFailed => "Mint did not confirm Lightning payment",
        AmountTooSmall => "Minted amount too small to cover routing fee buffer",
        MaxRetriesExceeded => "Exceeded 20 automatic retry attempts — manual review required",
        _ => code ?? "Unknown error"
    };
}
