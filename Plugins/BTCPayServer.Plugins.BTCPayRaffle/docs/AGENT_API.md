# BTCPay Raffle — agent / Satflux API notes

Integrators (e.g. **satflux.io**) manage raffles **only via Greenfield** with a store API key that has **`CanModifyStoreSettings`**. Merchants typically **do not** log into the BTCPay Server UI.

Buyers use **public** routes under `/raffle/{raffleId}` (no API key).

## Versioning

| Component | Minimum |
|-----------|---------|
| BTCPay Raffle plugin | **≥ 1.2.0.0** for presenter tokens, draft-only `PUT`, `draw-state` |
| CashuMelt (stores with Cashu checkout) | **≥ 1.2.0.2** |

- **REST paths** for existing Greenfield resources are unchanged from 1.0.x / 1.1.x.
- Responses use **camelCase** JSON. Tolerate unknown properties on read.

## Authentication

| Surface | Auth |
|---------|------|
| Greenfield `/api/v1/stores/{storeId}/raffle/...` | `Authorization: token …` (API key with `CanModifyStoreSettings`) |
| Public `/raffle/{id}`, `/raffle/{id}/buy`, receipts | None |
| Presenter `/raffle/{id}/present?token=…` | Short-lived **presenter token** from Greenfield (see below) |

## ⚠️ Not for Satflux / external integrators

The BTCPay **store UI** draw screen requires a **logged-in BTCPay user** (cookie session), not an API key:

| Method | Path | Auth |
|--------|------|------|
| `GET` | `/stores/{storeId}/plugins/raffle/{raffleId}/draw` | BTCPay user session |
| `POST` | `/stores/{storeId}/plugins/raffle/{raffleId}/draw` | BTCPay user session + antiforgery |

**Do not** call these from Satflux or headless integrations. Use Greenfield instead:

- `POST /api/v1/stores/{storeId}/raffle/{raffleId}/draw` — programmatic draw (JSON)
- `POST .../presenter-token` + `GET /raffle/{raffleId}/present?token=…` — live event screen

The public presenter page **does not** expose unauthenticated access to Greenfield `POST .../draw`. Draws from the presenter UI go to `POST /raffle/{raffleId}/present/draw` with **presenter token + antiforgery** only.

---

## Greenfield base path

```
/api/v1/stores/{storeId}/raffle
```

All endpoints below are relative to this base unless noted.

### Raffles

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/` | List raffles for the store |
| `POST` | `/` | Create raffle (starts **Draft**) |
| `GET` | `/{raffleId}` | Get raffle |
| `PUT` | `/{raffleId}` | Update raffle — **Draft only** (≥ 1.2.0.0) |
| `DELETE` | `/{raffleId}` | Delete — **Draft** or **Completed** only |

### Lifecycle

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/{raffleId}/open` | Draft → **Open** (ticket sales) |
| `POST` | `/{raffleId}/close` | Open → **Closed** (stop sales) |
| `POST` | `/{raffleId}/draw` | Draw next prize (Closed or Drawing) |
| `DELETE` | `/{raffleId}/drawings/last` | Undo last draw (status **Drawing** only) |
| `POST` | `/{raffleId}/complete` | Mark **Completed** after all draws |

### Tickets & drawings

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/{raffleId}/tickets` | List tickets |
| `POST` | `/{raffleId}/tickets/manual` | Add manual tickets (Open/Closed, before first draw) |
| `GET` | `/{raffleId}/drawings` | List draws |

### Presenter & draw state (≥ 1.2.0.0)

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/{raffleId}/presenter-token` | Mint presenter token + URL |
| `GET` | `/{raffleId}/draw-state` | JSON draw readiness |

---

## Request / response shapes

### Create raffle — `POST /`

```json
{
  "name": "Summer raffle",
  "description": "optional",
  "ticketCurrency": "EUR",
  "ticketPrice": 5.0,
  "maxTickets": 500
}
```

**SATS (legacy-friendly):**

```json
{
  "name": "Sat raffle",
  "ticketPriceSats": 21000,
  "maxTickets": null
}
```

Provide **`ticketPrice` + `ticketCurrency`**, or **`ticketPriceSats`** (implies SATS). Omitting both → **400**.

### Update raffle — `PUT /{raffleId}` (≥ 1.2.0.0)

Same body as create (`name` required). **Only allowed while `status` is `Draft`.**  
If the raffle is Open/Closed/Drawing/Completed → **400** with message *"Only Draft raffles can be updated via the API"*.

The BTCPay store UI may still edit non-draft raffles under stricter rules; that is **not** exposed on Greenfield.

### Raffle object (list / get / create / update)

```json
{
  "id": "uuid",
  "name": "string",
  "description": "string|null",
  "storeId": "string",
  "ticketCurrency": "SATS|EUR|…",
  "ticketPrice": 21000.0,
  "ticketPriceSats": 21000,
  "maxTickets": 500,
  "status": "Draft|Open|Closed|Drawing|Completed",
  "ticketsSold": 0,
  "createdAt": "2026-05-17T12:00:00Z",
  "openedAt": null,
  "closedAt": null,
  "completedAt": null
}
```

- `ticketPriceSats` is set for SATS-priced raffles; otherwise `null`.

### Ticket object — `GET .../tickets`

```json
{
  "ticketNumber": 1,
  "buyerName": "string|null",
  "buyerEmail": "string|null",
  "allocatedAt": "2026-05-17T12:00:00Z",
  "isManual": false,
  "receiptUrl": "/raffle/receipt/{invoiceId}"
}
```

- `receiptUrl` is **`null`** for manual tickets.

### Draw result — `POST .../draw`, `GET .../drawings`

```json
{
  "drawOrder": 1,
  "winningTicketNumber": 42,
  "winnerName": "string|null",
  "winnerEmail": "string|null",
  "drawnAt": "2026-05-17T20:00:00Z"
}
```

Presenter UI `POST /raffle/{id}/present/draw` returns a compatible shape with `success: true` and `ticketNumber` (same semantics as UI draw).

### Presenter token — `POST .../presenter-token`

```json
{
  "token": "protected-string",
  "expiresAt": "2026-05-17T24:00:00Z",
  "presenterUrl": "https://btcpay.example.com/raffle/{raffleId}/present?token=..."
}
```

Default lifetime: **4 hours**. Re-issue before expiry for long events.

### Draw state — `GET .../draw-state`

```json
{
  "status": "Closed",
  "totalTickets": 100,
  "eligibleTicketsRemaining": 100,
  "drawingsCount": 0,
  "canDraw": true,
  "canUndoLastDraw": false
}
```

- `canDraw`: status is **Closed** or **Drawing** and at least one eligible ticket remains.
- `canUndoLastDraw`: status **Drawing** and at least one drawing exists.

Token-authenticated mirror: `GET /raffle/{raffleId}/present/draw-state?token=...`

---

## Public URLs (buyers & events)

| URL | Purpose |
|-----|---------|
| `GET /raffle/{raffleId}` | Raffle landing + QR |
| `GET /raffle/{raffleId}/buy` | Buy tickets form |
| `POST /raffle/{raffleId}/buy` | Create BTCPay invoice → redirect to checkout |
| `GET /raffle/receipt/{invoiceId}` | Ticket receipt after payment |
| `GET /raffle/ticket/{ticketId}` | Verify ticket |
| `GET /raffle/{raffleId}/present?token=…` | **Presenter** draw screen (≥ 1.2.0.0) |

Presenter page is shown when status is **Closed**, **Drawing**, or **Completed**. For **Draft** / **Open**, a short “not available yet” page is shown — close sales first (`POST .../close`).

---

## Recommended Satflux flow

1. `POST` create raffle (Draft) → optional `PUT` while Draft.
2. `POST .../open` → share `https://{host}/raffle/{id}`.
3. Poll tickets or webhooks; `POST .../close` when sales end.
4. `POST .../presenter-token` → open `presenterUrl` on a projector / iframe.
5. Draw via presenter UI **or** `POST .../draw` from your backend.
6. Optional: `DELETE .../drawings/last` to undo (API only while Drawing).
7. `POST .../complete` when finished.

Use `GET .../draw-state` to drive UI buttons (“Draw next”, “Undo”, etc.).

---

## Errors

| HTTP | When |
|------|------|
| **400** | Invalid body, wrong status transition, draw with no eligible tickets, PUT on non-Draft, delete not Draft/Completed |
| **401** | Missing/invalid API key |
| **403** | API key without `CanModifyStoreSettings` |
| **404** | Unknown `raffleId` or raffle not owned by `storeId` |

Greenfield error bodies are typically `{ "message": "…" }` or ASP.NET validation details for model errors.

Presenter routes with invalid/expired token → **401** `{ "error": "Invalid or expired presenter token" }`.

---

## Status lifecycle

```
Draft ──open──► Open ──close──► Closed ──draw──► Drawing ──complete──► Completed
  │                                                                              │
  └──────────────────────── delete (Draft)              delete (Completed only) ─┘
```

---

## Cashu / Lightning checkout

For stores using **CashuMelt**, install **CashuMelt ≥ 1.2.0.2** alongside this plugin so raffle ticket invoices can use Cashu checkout without server errors. Raffle invoices use the store’s configured `ticketCurrency` / `ticketPrice` (not hard-coded sats).

See [CashuMelt agent API](../../BTCPayServer.Plugins.CashuMelt/docs/AGENT_API.md) for checkout polling and settings.
