For 1.4.0.1 wallet changes, first read the authenticated `GET .../cashumelt/safety` endpoint and require `safeToChangeWallet === true`. See [settlement and recovery requirements](SETTLEMENT_SAFETY.md). Mint quote poll URLs are intentionally absent for new records.

# CashuMelt — agent / Satflux API notes

Base path (Greenfield + cookie, `CanModifyStoreSettings`):

`/api/v1/stores/{storeId}/plugins/cashumelt`

## Versioning

- **Plugin assembly version** is shipped in the `.btcpay` package (e.g. `1.0.0.3`).
- **REST paths** for settings/payments are unchanged from 1.0.0.x.
- **Response shape** changes are listed below (integrators should tolerate unknown JSON properties).

## Breaking / additive JSON changes

### Checkout poll (browser, not Greenfield)

`GET /plugins/cashumelt/poll/{quoteId}`

| Version | Response |
|---------|----------|
| ≤ 1.0.0.2 | `{ "paid": bool, "error": string \| null }` |
| ≥ 1.0.0.3 | `{ "paid": bool, "error": string \| null, "retryAfterSeconds": number \| null }` |

- **`retryAfterSeconds`**: optional hint when the mint rate-limits or the server applies backoff. Clients **should** respect it to reduce 429s (recommended minimum poll interval = `max(2000, retryAfterSeconds * 1000)` ms when the field is present).
- **Errors**: On unexpected server failure, the endpoint returns **200 OK** with `paid: false`, `error: null`, `retryAfterSeconds: 5` (defensive; avoids checkout 500 spam). Monitor logs for `cashumelt_poll_unhandled`.

### Retry payment (Greenfield)

`POST /api/v1/stores/{storeId}/plugins/cashumelt/payments/{quoteId}/retry`

| Version | Response |
|---------|----------|
| Earlier | `{ "settled": bool, "error": string \| null }` |
| ≥ 1.0.0.3 | `{ "settled": bool, "error": string \| null, "retryAfterSeconds": number \| null }` |

- **`PENDING`**: safe to call — runs the same path as checkout poll (mint status → mint proofs if needed → melt → BTCPay).
- **`FAILED`** with stored proofs: resets to `PENDING` and re-runs settlement (melt retry without re-minting).
- **`FAILED`** without proofs: **400** — cannot retry automatically.
- **`MELT_COMPLETE`**: does **not** reset to `PENDING`; only retries BTCPay accounting (forward already done).

### Payment list / detail

- `SettlementState` may be **`MELT_COMPLETE`** (new). Satflux UIs should display it as “forward done, finalizing invoice” or similar.
- `settlementError` is set when `FAILED`.
- **≥ 1.1.0.0**: each payment item includes optional **`mintQuotePollUrl`** (NUT-23 GET URL for the mint quote) for support tooling.

## Settings endpoints

`GET` / `PUT` `/settings` — same base validation (`MintUrl` HTTPS, `LightningAddress` with `@`, `unit` `sat`|`usd`).

**≥ 1.1.0.0** — additive JSON on settings:

| Field | Meaning |
|-------|--------|
| `trustedMintUrls` | string \| null — optional multiline text; when set, primary `mintUrl` must match one listed HTTPS origin (after trim/normalize). |
| `maxMeltFeeReserveSats` | number \| null — reject melt if mint `feeReserve` exceeds this (empty/null = no cap). |
| `maxMeltFeeReservePercentOfMinted` | number \| null — max `feeReserve` as % of minted total (0–100; null = no cap). |

A background service also retries **`MELT_COMPLETE`** and stale **`PENDING`** rows so clients do not need to keep checkout open.

On **`PUT /settings`**, use **camelCase** JSON keys (`mintUrl`, `lightningAddress`, `unit`, `enabled`, optional `trustedMintUrls`, `maxMeltFeeReserveSats`, `maxMeltFeeReservePercentOfMinted`). **Omit** an optional key to leave the stored value unchanged; send **`null`** for `trustedMintUrls` / fee fields to clear them.

## Recommended checkout polling (Satflux / custom frontends)

- **Default**: 2–5 s between polls is reasonable; **if `retryAfterSeconds` is returned**, wait at least that many seconds before the next poll.
- Server also enforces **per-quote backoff** on mint HTTP 429/500/502/503/504 so rapid polls do not hit the mint every 2 s.
- **Mint unreachable** (DNS, firewall, `Network is unreachable`): checkout poll stays **200 OK** with `paid: false` and `retryAfterSeconds` (typically 5). Logs use `cashumelt_mint_poll_transient` without a full exception stack (≥ 1.2.0.4).


## Merchant recovery API (1.4.0.2)

Payment list/detail responses include `originalSettingsRequired`. It is true only for
`MANUAL_REVIEW` rows without their original settings snapshot. Other manual review
reasons still need their own evidence and must not be treated as legacy settings recovery.

`POST /api/v1/stores/{storeId}/plugins/cashumelt/payments/{quoteId}/confirm-original`
requires `Content-Type: application/json`, uses the existing `CanModifyStoreSettings` authorization and accepts:

```json
{"mintUrl":"https://original-mint.example/Bitcoin","lightningAddress":"merchant@example.com","confirmedOriginal":true}
```

The merchant must explicitly confirm the original values. Current store settings
are only suggestions, not historical evidence. The plugin verifies the exact funding
quote ID, BOLT11 invoice, amount and unit at the supplied mint before freezing recovery
settings for that payment. This endpoint does not change store settings or pay a new invoice.
A successful response is `{"confirmed":true}`. The snapshotted row remains in
`MANUAL_REVIEW`, so checkout polling and background reconciliation cannot pay it yet.
Then call the existing `/retry` endpoint;
if retry is interrupted, retry the same payment, without submitting original settings again.
Repeated confirmation of an already snapshotted or settled payment is rejected.
A quote belonging to another store returns 404. Invalid or conflicting settings return 400;
provider HTTP/JSON failure returns 503, timeout 504. Caller cancellation is preserved.
No proofs, blinding data or private mint quote IDs are returned by the new endpoint.


## Merchant change recovery API (1.4.0.4)

These separate routes accept **only Greenfield API-key authentication**, with the
existing store-scoped `CanModifyStoreSettings` permission. Cookie authentication
is not a fallback. All responses, including authorization/validation errors, are
private and non-cacheable. Integrators must never log exported tokens, request or
response bodies, or transport exception details. Never automatically retry recovery
POSTs after an uncertain result; require fresh merchant authorization instead.

`GET /api/v1/stores/{storeId}/plugins/cashumelt/change` returns schema version 1:

```json
{
  "version": 1,
  "pendingSat": "5",
  "sweptSat": "0",
  "minimumSweepSat": "100",
  "unresolvedRecords": 2,
  "availableRecords": 2,
  "exportedRecords": 0,
  "sweepingRecords": 0,
  "manualReviewRecords": 0,
  "unsupportedUnitRecords": 0,
  "otherUnresolvedRecords": 0
}
```

Amounts are canonical nonnegative signed-64-bit decimal **strings**, preserving
precision in JavaScript. Pending and swept amounts match the native sat-only totals;
the automatic sweep threshold is unchanged. The four named state counters are also
sat-only. Unsupported-unit counts include unresolved non-sat records. Other counts
include unresolved sat records in unknown states and terminal records retaining a
sweep intent. These counters partition unresolved records; they are not amounts.
A failed/invalid aggregate returns an unavailable error, never fabricated zero.
No mint URLs, identifiers, proofs or recovery intents appear in the summary.

`POST .../change/export` and `POST .../change/verify` require
`Content-Type: application/json` with exactly `{"confirmed":true}`. False, string
or numeric confirmations, duplicate properties, unknown properties and form data
are rejected. Export returns `{"tokens":["cashuA..."]}`, including an explicit empty
array when nothing is exportable. Tokens are bearer assets: download privately and
import into the merchant's own compatible Cashu wallet. Export is repeatable and
**retains wallet-change holds**. It neither discards small balances nor pays Lightning.

Verify returns `{"verified":true}` only after the existing recovery service confirms
exported proofs are consumed and preserves any uncertain original sweep. Unspent,
pending, unknown or incomplete mint evidence cannot release the hold. Verification
covers exported change only: unsupported/unknown records or unresolved payments may
still block a wallet change. Refresh the existing `/safety` endpoint separately and
require a fresh safety check on the actual settings write.

Error responses contain fixed `code` and `error` values: `400`
`cashu_change_confirmation_required`, `409` `cashu_change_export_blocked` or
`cashu_change_verification_blocked`, and `503` `cashu_change_unavailable`. No caught
exception details or raw mint responses are included. Existing native recovery,
Data Protection keys, database evidence and advisory locks remain unchanged.

Install the provider before enabling consumer controls. Older plugins lack these
routes; consumers must display unsupported capability instead of zero balances.
Back up the plugin database and Data Protection keys together. Do not downgrade or
mix plugin worker versions during recovery. There is no new database migration.


## Payment recovery and blocker diagnostics (1.4.0.5)

Existing `/safety` adds `failedPayments`, `otherNonTerminalPayments` and
`terminalRecoveryPayments`. These disjoint nonnegative counts sum to
`unresolvedPayments`; two FAILED rows do not necessarily explain every obligation.
Terminal rows can retain recovery material and still block a wallet change.

Existing payment list/detail adds:

- `blocksWalletChange`: the unchanged server wallet-change predicate.
- `walletChangeBlockReasons`: unique strings drawn from `non_terminal_settlement`,
  `retained_minted_proofs`, `retained_issuance_intent`, `retained_change_outputs`,
  `retained_payout_quote`. The last condition applies only while not SETTLED.
- `recoveryPhase`: one of `complete`, `expired_unpaid`, `terminal_recovery_review`,
  `original_settings_required`, `accounting_pending`, `payout_reconciliation`,
  `proofs_retained`, `issuance_restore`, `recovery_evidence_review`,
  `funding_verification`.

These structural classifications never authorize recovery or prove a balance exists.
Only presence booleans are projected internally; no new identifiers, proofs, private
quote IDs or intent contents are exposed. Older plugin metadata is unknown, not an
unblocked claim.

A funding status HTTP 400/404 is not evidence of unpaid, paid or spent state. Retry
can bypass obsolete funding lookup only with matching original store/mint/unit,
signed original invoice, encrypted issuance intent bound to the original funding
quote and exact amount, and complete proofs matching that intent's outputs.
Restoration reuses the existing intent in restore-only mode; empty/partial/invalid
NUT-09 restoration cannot issue replacement tokens. Existing payout recovery checks
the exact saved payout and never replaces an unknown/pending/expired intent. Without
an existing payout, complete fresh UNSPENT evidence is required before using the
original snapshot destination. Settled/invalid original invoices require review.

Missing snapshots, corrupt/mismatched evidence, older proof-only records lacking a
valid issuance binding, or payout/change-output evidence without usable proofs stay
held for manual investigation. Without usable durable evidence, normal funding
verification and strict expired-unpaid criteria remain unchanged. If original mint
history remains unavailable, this update cannot certify those obligations clear.
The change-token API does not export or release payment-held proofs.

Automatic recovery now counts/checks/dispatches under the quote advisory lease,
retains the existing 20-attempt escalation, and cannot reset itself. Asset-bearing
PENDING work uses the same cap; unfunded funding polls remain uncounted. Explicit
authorized manual retry resets attempts. Accounting-only work preserves its existing
cap and never reissues/melts. No schema, threshold, route or wallet-safety weakening.
