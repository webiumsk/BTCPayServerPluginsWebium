# CashuMelt plugin — release notes

## 1.2.0.4 (quieter mint poll logs for network errors)

### Checkout poll when the mint is unreachable

- `GET /plugins/cashumelt/poll/{quoteId}` already treated `HttpRequestException` (e.g. *Network is unreachable*) as **transient** with `retryAfterSeconds` — behavior unchanged.
- Logs no longer attach the full exception stack for these expected failures; one line with `cashumelt_mint_poll_transient`, `mintHost`, `quote`, and `reason` (e.g. `socket_NetworkUnreachable`).
- Broader catch covers timeouts, I/O errors, and invalid JSON from the mint on poll.

### Upgrade

Install **1.2.0.4**. If polls keep failing, verify connectivity from the BTCPay host to your configured `mintUrl` (e.g. `curl https://your-mint/v1/info`).

---

## 1.2.0.3 (EF migration baseline for existing databases)

### Startup warning: `relation "CashuMeltStoreSettings" already exists`

- Databases created earlier by the idempotent SQL schema creator had tables but no EF `__EFMigrationsHistory` rows. On upgrade, `MigrateAsync` tried to run `InitialCreate` again and logged a warning before falling back to the schema creator.
- **`CashuMeltEfMigrationBaseliner`** inspects the live schema and stamps satisfied migrations into history before `MigrateAsync`, so only missing changes are applied.
- If a migration still hits `42P07` / `42701`, history is baselined again and migration is retried once.

### Upgrade

Install **1.2.0.3** (includes 1.2.0.2 checkout and schema fixes). The `42P07` warning on startup should disappear; behavior is unchanged if you already relied on the schema creator fallback.

---

## 1.2.0.2 (Raffle checkout, SATS invoices, payout address validation)

### Checkout crash with BTCPay Raffle (and other apps)

- `CashuMeltCheckoutModelExtension` no longer throws when the CASHU payment prompt is missing or incomplete (failed mint quote at invoice creation). Checkout falls back to BTCPay's default UI instead of triggering plugin disable.
- **SATS-denominated invoices** (e.g. raffle tickets): mint amount is taken from `invoice.Price` in satoshis instead of incorrectly multiplying BTC-denominated `due` by 10⁸.
- New helper `CashuMeltAmountCalculator` (unit-tested).

### Merchant Lightning address (payout)

- Saving enabled settings now **probes LNURL-pay** on the configured Lightning address so misconfiguration is caught in the CashuMelt settings UI / API, not only after a PoS or settlement failure.
- Settings help text clarifies this address is for **post-payment melt payout**, not the store's customer-facing Lightning checkout.

### BTCPay Raffle plugin

- After ticket purchase, checkout redirect includes the store's **default payment method** when supported on the invoice (LN-only, Cashu-only, or mixed stores).

### Upgrade

1. Install **1.2.0.2** (includes 1.2.0.1 schema fixes).
2. Re-enable CashuMelt / Raffle in **Settings → Plugins** if they were disabled.
3. Re-save CashuMelt settings to validate the merchant payout Lightning address.

---

## 1.2.0.1 (fix plugin disabled after upgrade)

**Symptom:** BTCPay disables CashuMelt after installing 1.2.0.0; logs may only show `Skipping disabled plugin` on restart, or a one-line `Configuration error` without a stack trace.

**Cause:** Installations that rely on the idempotent SQL schema creator (common when EF migrations did not run) were missing the new `RetryCount`, `NeedsManualReview`, and `FailureReasonCode` columns. The first query against payment rows (settings UI, API, or background reconciliation) threw a PostgreSQL `42703` error; BTCPay then disabled the plugin.

**Fix:**

- `CashuMeltSchemaCreator` now adds the 1.2.0 retry-tracking columns and partial index.
- EF migrations include `[Migration(...)]` attributes so `MigrateAsync` discovers them reliably.
- `PluginMigrationRunner` falls back to the schema creator on any migration failure (not only `42P01` / `42703`).

### Upgrade from 1.2.0.0

1. Remove `BTCPayServer.Plugins.CashuMelt` from `plugins/disabled` if present (or re-enable via **Settings → Plugins**).
2. Install **1.2.0.1** and restart BTCPay Server.
3. Optional SQL check:

```sql
SELECT column_name FROM information_schema.columns
WHERE table_schema = 'BTCPayServer.Plugins.CashuMelt'
  AND table_name = 'CashuMeltPaymentRequests'
  AND column_name IN ('RetryCount', 'NeedsManualReview', 'FailureReasonCode');
```

---

## 1.2.0.0 (retry tracking, keyset safety, failure reason codes)

Inspired by a comparison with the [BTCNutServer](https://github.com/cashubtc/BTCNutServer) plugin.

### Retry count + manual review escalation

- Every automatic retry attempt by `CashuMeltReconciliationHostedService` increments `RetryCount` on the payment row.
- After **20 consecutive failed attempts** the row is flagged `NeedsManualReview = true` and skipped by background reconciliation.
- The UI shows a red **Manual Review** badge and a **Manual retry** button; clicking it resets `NeedsManualReview = false` and `RetryCount = 0` so automatic retries can resume if the underlying issue is resolved.
- API (`GET /payments`, `GET /payments/{quoteId}`) returns three new fields: `retryCount`, `needsManualReview`, `failureReasonCode`.
- CSV export adds columns `retry_count`, `needs_manual_review`, `failure_reason_code`.

### Machine-readable failure reason codes

New class `CashuMeltFailureReasons` with string constants:

| Code | Meaning |
|---|---|
| `mint_poll_error` | Mint returned a permanent error while polling quote state |
| `trusted_mint_violation` | Configured mint URL is not in the trusted mint list |
| `mint_proof_failed` | Failed to obtain proof tokens from the mint |
| `keyset_conflict` | Mint returned a proof with an unexpected keyset ID |
| `ln_address_unresolvable` | LNURL resolution of the merchant Lightning address failed |
| `melt_quote_failed` | Melt quote request failed |
| `fee_too_high` | Lightning routing fee reserve exceeds the configured cap |
| `melt_failed` | Mint did not confirm Lightning payment |
| `amount_too_small` | Minted amount too small to cover the routing fee buffer |
| `max_retries_exceeded` | Exceeded 20 automatic retry attempts — manual review required |

### Keyset conflict detection

- After `MintTokensAsync`, every returned `BlindSignature.Id` is checked against the `keyset.Id` obtained from `GetKeysAsync`.
- A mismatching keyset ID immediately aborts minting and records a `keyset_conflict` failure, preventing proof loss from cross-mint collisions.
- Grep: `cashumelt_keyset_conflict invoice=… quote=… expectedKeysetId=… actualKeysetId=…`

### Exception hierarchy

- `Errors/CashuMeltException` — abstract base
- `CashuMeltUserException` — message is safe to surface to the customer
- `CashuMeltSystemException` — internal error; customer sees a generic message

### Database migration

New EF migration `AddRetryTracking` (`20260517000000`):
- `CashuMeltPaymentRequests.RetryCount` — `integer NOT NULL DEFAULT 0`
- `CashuMeltPaymentRequests.NeedsManualReview` — `boolean NOT NULL DEFAULT false`
- `CashuMeltPaymentRequests.FailureReasonCode` — `varchar(100) NULL`
- Partial index on `NeedsManualReview = true` for efficient operator queries

### Upgrade

1. Back up the PostgreSQL database.
2. Install plugin version `1.2.0.0`.
3. Restart BTCPay Server — the migration runs automatically on startup.
4. Verify: existing rows have `RetryCount = 0`, `NeedsManualReview = false`.

---

## 1.1.0.0 (merchant controls, reconciliation, ops UX)

- **Non-custodial by design:** proofs remain only for the short mint→melt bridge; no customer ecash wallet.
- **Trusted mint URLs** (optional): multiline HTTPS allow-list; primary mint must match when the list is non-empty.
- **Melt fee reserve caps** (optional): max sat reserve and/or max percent of minted amount; rejects melt if the mint quotes excessive LN routing reserve.
- **Background reconciliation** (`CashuMeltReconciliationHostedService`): retries `MELT_COMPLETE` BTCPay accounting, polls stale `PENDING`, and occasionally retries `FAILED` rows that still have stored proofs.
- **Settings UI:** filters on recent payments, CSV export (includes NUT-23 poll URL), invoice link, mint poll + `lightning:` link.
- **API:** settings DTO adds `trustedMintUrls`, `maxMeltFeeReserveSats`, `maxMeltFeeReservePercentOfMinted`; payment list/detail add `mintQuotePollUrl`.
- **DB:** EF migration `AddMerchantRiskControls` + schema creator `ALTER` for new store columns; index on `SettlementState`.
- **Transaction link provider** for `CASHU` (no public explorer URL; avoids missing provider issues).

## 1.0.0.3 (settlement ordering + ops, observability, integrators)

### BTCPay invoice vs plugin settlement (no more “invoice Settled without forward” on new flow)

- **BTCPay `InvoiceStatus.Settled`**: set only after `AddPayment` + `ReceivedPayment` run **after** a successful mint **melt** (Lightning forward to the merchant LN address).
- **Plugin `SettlementState`**:
  - `PENDING` — waiting on mint quote, mint proofs, transient forward errors, or mint HTTP backoff.
  - `MELT_COMPLETE` — forward succeeded; BTCPay row write is retried on poll or `POST .../payments/{quoteId}/retry`.
  - `SETTLED` — forward + BTCPay accounting complete.
  - `FAILED` — terminal error; `SettlementError` set (grep `cashumelt_settlement_failed`).

### Database migration

- **No new EF migration** for `1.0.0.4`: `SettlementState` remains a string column; `MELT_COMPLETE` is a new value only.
- **No automatic backfill job** in-plugin for legacy rows. See below.

### Production upgrade (smoke)

1. **Backup** PostgreSQL (BTCPay DB).
2. Install plugin version **1.0.0.3** (settlement ordering, structured log tokens, poll try/catch, mint **HTTP 500** as transient on quote poll, docs in `RELEASE_NOTES.md` / `docs/AGENT_API.md`).
3. Restart BTCPay Server; confirm startup migrations complete without errors.
4. **Smoke**: one small test invoice — in logs, for a successful payment, expect in order (same `invoice` + `quote`):
   - `cashumelt_mint_proof_ok`
   - `cashumelt_forward_ok`
   - `cashumelt_btcpay_recorded`
   - `cashumelt_settlement_complete`
5. **Rate limit**: trigger or simulate mint **429** — `GET /plugins/cashumelt/poll/{quoteId}` must return **200** with JSON (no 500); optional `retryAfterSeconds`.

### Stuck / legacy rows (pre–settlement-order fix)

**Symptom (old bug):** BTCPay invoice already **Settled**, merchant did not receive Lightning, plugin row stuck `PENDING` or inconsistent.

**New version does not auto-reverse BTCPay** or re-melt without proofs.

**Recovery options:**

1. **If `MintedProofsJson` still present** (`FAILED` or `PENDING` with proofs): call  
   `POST /api/v1/stores/{storeId}/plugins/cashumelt/payments/{quoteId}/retry`  
   (Greenfield + `CanModifyStoreSettings`) or fix config and let checkout poll continue.
2. **If proofs were cleared / melt unknown / invoice already Settled:** treat as **incident**: use BTCPay + mint tooling; you need **`QuoteId`** (and mint URL from store settings) to reconcile with the mint operator. Plugin cannot recreate spent proofs.
3. **`MELT_COMPLETE` after upgrade:** poll or **retry** endpoint retries **BTCPay only** (forward already done).

### Observability (grep)

| Token | Meaning |
|--------|---------|
| `cashumelt_settlement_complete` | End-to-end success (after BTCPay recorded). |
| `cashumelt_settlement_failed` | Terminal failure (`FAILED` + message). |
| `cashumelt_forward_ok` | Melt paid merchant invoice (pre-BTCPay record). |
| `cashumelt_btcpay_recorded` | Payment row + event published. |
| `cashumelt_mint_proof_ok` | Proofs minted and stored. |
| `cashumelt_mint_poll_transient` | Mint returned 429/500/502/503/504 (logged from `CashuMeltMintClient`). |
| `cashumelt_forward_retry` | Transient forward/melt; poll again. |
| `cashumelt_btcpay_accounting_retry` | `MELT_COMPLETE` or BTCPay write still pending. |

Structured fields on many lines: `invoice=…`, `quote=…`, `phase=…`, `amountSat=…`.

### Integrators (Satflux / agents)

See [docs/AGENT_API.md](docs/AGENT_API.md) for **Poll** and **retry** JSON changes (`retryAfterSeconds`).
