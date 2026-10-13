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
