# CashuMelt plugin — release notes

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
