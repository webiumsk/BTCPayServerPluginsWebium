namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>
/// Grep-friendly markers for logs (support / metrics pipelines). Include in message text so plain-text grep works.
/// </summary>
internal static class CashuMeltObservability
{
    public const string TagMintPollTransient = "cashumelt_mint_poll_transient";
    public const string TagMintProofOk = "cashumelt_mint_proof_ok";
    public const string TagForwardOk = "cashumelt_forward_ok";
    public const string TagBtcpayRecorded = "cashumelt_btcpay_recorded";
    public const string TagSettlementComplete = "cashumelt_settlement_complete";
    public const string TagSettlementFailed = "cashumelt_settlement_failed";
    public const string TagMeltRetry = "cashumelt_forward_retry";
    public const string TagBtcpayRetry = "cashumelt_btcpay_accounting_retry";
    public const string TagSkippedOtherPayment = "cashumelt_skipped_invoice_finalized_elsewhere";
    public const string TagChangeStored = "cashumelt_change_stored";
    public const string TagChangeSwept = "cashumelt_change_swept";

    public const string PhaseMintPoll = "mint_poll";
    public const string PhaseMintProof = "mint_proof";
    public const string PhaseForward = "forward";
    public const string PhaseBtcpay = "btcpay_record";
}
