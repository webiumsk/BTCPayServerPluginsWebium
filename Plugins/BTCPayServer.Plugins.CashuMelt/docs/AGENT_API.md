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

## Settings endpoints

`GET` / `PUT` `/settings` — no intentional breaking changes; same validation as before (`MintUrl` HTTPS, `LightningAddress` with `@`, `unit` `sat`|`usd`).

## Recommended checkout polling (Satflux / custom frontends)

- **Default**: 2–5 s between polls is reasonable; **if `retryAfterSeconds` is returned**, wait at least that many seconds before the next poll.
- Server also enforces **per-quote backoff** on mint HTTP 429/500/502/503/504 so rapid polls do not hit the mint every 2 s.
