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
