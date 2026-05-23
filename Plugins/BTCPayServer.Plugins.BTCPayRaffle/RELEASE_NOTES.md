# BTCPay Raffle plugin — release notes

## 1.3.2.0 (public buy page layout)

### Public raffle page (`/raffle/{id}`)

- **Buy tickets** heading centered.
- Ticket counter controls use **1px** borders (was 2px).
- **Total** price box without border; on md+ sits beside the ticket counter in one row.

### Buyer wallet (`/raffle/{id}/my`)

- While sales are **Open**, link **Buy more tickets** (↗) opens the public buy page in a **new tab** so users keep this overview open.

### Upgrade

1. Install **1.3.2.0** and restart BTCPay Server.

---

## 1.3.1.0 (event raffle bundle API)

### Satoshi Tickets integration

- `IRaffleEventBundleService` — validate open raffle + idempotent bundle allocation (`eventbundle:{orderId}:{email}`).
- Required on BTCPay when using **Satoshi Tickets ≥ 1.3.6.1** with `bundledRaffleId` / `bundledRaffleTicketsPerAdmission` on events.

### Receipt & presenter polish

- Receipt: wallet + **buy more** CTAs in one row on md+ (2/3 + 1/3), thinner borders, high-contrast buy-more button (incl. hover).
- Presenter: **Undo last draw** visible immediately after an AJAX draw (no full page reload).
- Ticket verify (`/raffle/ticket/{id}`): readable status colors, reveal delay aligned with live draw.

### Upgrade

1. Install **1.3.1.0** and restart BTCPay Server.
2. Ensure **Satoshi Tickets** fork with bundle migration is also installed.

---

## 1.3.0.2 (draw UI, wallet last number, i18n)

### Draw & presenter

- Shared draw layout (compact stats, smaller slot, inline draw + undo on desktop) for store **Draw** and public **Present** screens.
- Presenter and admin draw UI strings localized.

### Buyer wallet

- **Last drawn number** shown above the status banner (server + live `/my/state` JSON field `lastWinningTicketNumber`).
- Wallet page copy localized (en / sk / es) via embedded JSON resources.

### Ticket verification

- Distinct messages for **raffle in progress** (sales open) vs **draw in progress** (sales closed, draw not finished).
- Localized verify page (en / sk / es).

### Upgrade

1. Install **1.3.0.2** and restart BTCPay Server.
2. UI language: store **Checkout → Language** (`StoreBlob.DefaultLang`, e.g. `sk-SK`), then `?lang=`, then `Accept-Language` (BTCPay forces Invariant culture on requests).

---

## 1.3.0.1 (wallet security & draw sync)

### Buyer wallet

- Wallet auth via **HttpOnly cookie** after one-time `?token=` redemption; receipt and `/my/state` no longer expose the token in URLs.
- Draw results on the wallet page appear after the same **~6 s delay** as the presenter slot animation (no early spoiler for winners).
- Polling retries on transient errors (only `404` stops); cookie sent with `credentials: 'same-origin'`.

### Presenter

- **Undo last draw** on `/raffle/{id}/present?token=…` (same as store draw UI; requires presenter token).

### Security & robustness

- Token payload: buyer email **Base64url-encoded** (delimiter-safe; legacy tokens still work).
- Email link builder rejects `//` network-path URLs (`RafflePublicUrlHelper.BuildPath`).
- Buyer queries use normalized `BuyerEmail` column directly (index-friendly).
- Ticket emails: validated `http(s)` origins, HTML-encoded link attributes; masked email in failure logs.

### Upgrade

1. Install **1.3.0.1** and restart BTCPay Server.
2. Buyers with an old bookmarked wallet URL (`?token=…`) are redirected once and then use the cookie.

---

## 1.3.0.0 (buyer wallet — all tickets per email)

### Buyer wallet

- **`GET /raffle/{raffleId}/my?token=…`** — one page with every ticket for the buyer’s email on that raffle (all purchases combined).
- **`GET /raffle/{raffleId}/my/state?token=…`** — JSON for live updates during the draw (highlights wins, confetti on new prizes).
- Signed token (90-day default) in confirmation emails; same email on later purchases uses the same wallet link.
- Receipt page links to the wallet; per-invoice receipt still available.

### Email & buy form

- **Manual tickets** (store UI and Greenfield API) send the same confirmation email as paid purchases (wallet link + ticket numbers).
- Public buy form: **Pay** stays disabled until a valid email is entered (`required` + client-side check).
- Shared `RaffleTicketEmailService` for invoice, manual, and wallet links.

### Data

- Migration `20260519000000_BuyerEmailIndex` — index on `(RaffleId, BuyerEmail)`.
- New ticket rows store normalized buyer email.

### Upgrade

1. Back up PostgreSQL.
2. Install **1.3.0.0** and restart BTCPay Server.
3. Existing tickets remain queryable (case-insensitive email match).

---

## 1.2.0.2 (buyer email required, privacy on display)

### Public purchase & manual tickets

- **Email required** on `/raffle/{id}` buy form and store **Add manual tickets** (HTML + server validation).
- **Display name optional** — shown on ticket list and during draw (nickname); help text on both forms.
- Failed buy validation **repopulates** the form (`BuyForm` + `asp-for`).

### API & draw UIs

- `buyerEmail` / `winnerEmail` **masked** in Greenfield API and draw/presenter JSON (`RaffleBuyerDisplay.MaskEmail`).
- `buyerName` / `winnerName` show **`—`** when empty (no fallback to email).
- Manual tickets API: `buyerEmail` required.

### Documentation

- Plugin [README.md](README.md); root repo README lists Raffle; [docs/AGENT_API.md](docs/AGENT_API.md) updated.

### Upgrade

1. Back up PostgreSQL.
2. Install **1.2.0.2** and restart BTCPay Server.
3. No new database migration.

---

## 1.2.0.1 (presenter view path fix)

- Fix presenter pages not resolving in the plugin host: explicit paths to `Present.cshtml` and `PresentUnavailable.cshtml`.
- No API or database changes from 1.2.0.0.

---

## 1.2.0.0 (Satflux: presenter token, draft-only API PUT, draw-state)

### Integrator API (Greenfield)

- **POST** `.../raffle/{raffleId}/presenter-token` → `{ token, expiresAt, presenterUrl }` — live draw screen without BTCPay login (default 4 h TTL).
- **GET** `.../raffle/{raffleId}/draw-state` — `eligibleTicketsRemaining`, `canDraw`, `canUndoLastDraw`, etc.
- **PUT** `.../raffle/{raffleId}` — **Draft only** (Satflux/API); BTCPay UI keeps broader edit rules.
- Public **GET** `/raffle/{raffleId}/present?token=…` — presenter UI; draws via `POST .../present/draw` (token + antiforgery), not anonymous Greenfield draw.

### Documentation

- Full agent notes: [docs/AGENT_API.md](docs/AGENT_API.md) — explicitly documents that `/stores/{storeId}/plugins/raffle/{id}/draw` requires a **BTCPay user session** and is **not** for Satflux.

### Upgrade

1. Back up PostgreSQL.
2. Install **1.2.0.0** and restart BTCPay Server.
3. Satflux: plugin **≥ 1.2.0.0**; Cashu stores: **CashuMelt ≥ 1.2.0.2**.

---

## 1.1.0.0 (admin API, fiat pricing, manual tickets)

### Edit, delete, and pricing

- **PUT** `/api/v1/stores/{storeId}/raffle/{raffleId}` — update while Draft (see [docs/AGENT_API.md](docs/AGENT_API.md); **1.2.0.0+** restricts Greenfield PUT to Draft only).
- **DELETE** raffle — allowed only in **Draft** or **Completed** status.
- Ticket price can be set in **fiat** (EUR, USD, store default, …) or **SATS**; checkout invoices use the configured currency.
- Legacy field `ticketPriceSats` still accepted on create for SATS-only integrations.

### Manual tickets and draw undo

- **POST** `.../tickets/manual` — add tickets without payment (Open or Closed, before any draw).
- **DELETE** `.../drawings/last` — undo the latest prize draw while status is **Drawing** (winner becomes eligible again).

### Database

- Migration `20260518000000_RafflePricingAndManualTickets` adds `TicketCurrency`, `TicketPrice`, `RaffleTickets.IsManual`.
- Schema creator updated for idempotent installs.

### Upgrade

1. Back up PostgreSQL.
2. Install **1.1.0.0** and restart BTCPay Server (migration runs on startup).
3. Satflux / external panels: use plugin **≥ 1.1.0.0** and updated API notes in `docs/AGENT_API.md`.

---

## 1.0.0.3 (checkout default payment method, CashuMelt stores)

### Checkout after ticket purchase

- Redirect to BTCPay checkout now includes the store's **default payment method** when that method is available on the invoice (`paymentMethodId` query parameter).
- Works with LN-only stores, Cashu-only stores, and mixed setups — raffle does not force a specific payment method.
- Pair with **CashuMelt 1.2.0.2+** on stores that use Cashu: avoids checkout crashes when buying raffle tickets with Cashu enabled.

### Upgrade

1. Back up the PostgreSQL database.
2. Install plugin version `1.0.0.3` via **Settings → Plugins**.
3. Restart BTCPay Server — no new database migration.
4. If the store uses CashuMelt, upgrade CashuMelt to **1.2.0.2** or newer as well.

---

## 1.0.0.2 (UI theme compatibility)

- Public raffle page, ticket verification, store **Manage**, and live **Draw** views now use BTCPay CSS variables (`--btcpay-body-text`, `--btcpay-bg-tile`, borders, success/warning colors) so layouts stay readable in light and dark themes.
- Ticket verification shows prize rank labels (1st / 2nd / 3rd) when a ticket has won.

### Upgrade

1. Back up the PostgreSQL database.
2. Install plugin version `1.0.0.2` via **Settings → Plugins**.
3. Restart BTCPay Server — no new database migration.

---

## 1.0.0.1 (release notes, packaging)

- Added this `RELEASE_NOTES.md` file for versioned upgrades.
- Re-packaged installable `.btcpay` artifact at version `1.0.0.1`.

### Upgrade

1. Back up the PostgreSQL database.
2. Install plugin version `1.0.0.1` via **Settings → Plugins** (or replace the previous `1.0.0.0` build).
3. Restart BTCPay Server — no new database migration in this patch.

---

## 1.0.0.0 (initial release)

Lightning-powered raffle / tombola plugin for BTCPay Server.

### Store operator UI

- Create and manage raffles per store (draft → open → closed → drawing → completed).
- Set ticket price in satoshis and optional max ticket cap.
- Live prize draw page with animated slot-machine UI.
- Store navigation entry under integrations.

### Public checkout

- Public raffle page at `/raffle/{raffleId}` with QR code for sharing.
- Ticket purchase via standard BTCPay Lightning invoice (`POST …/buy`).
- Receipt and ticket verification pages for buyers after payment.

### Ticket allocation

- `RaffleInvoiceWatcher` listens for `InvoiceEvent.Confirmed` / `Completed`.
- Allocates sequential ticket numbers idempotently per invoice (safe on duplicate events).
- Optional buyer email with ticket receipt link when store email is configured.

### Prize draws

- Cryptographically secure random winner selection (`RandomNumberGenerator`).
- Multiple draws per raffle; already-won tickets excluded.
- Draw order stored in `RaffleDrawings` with winning ticket reference.

### Greenfield API

Base path: `/api/v1/stores/{storeId}/raffle` (API key with **Modify Store** permission).

- List, create, get, update raffles
- Open / close sales, draw next prize, complete raffle
- List tickets and drawings

### Database

- Isolated PostgreSQL schema `BTCPayServer.Plugins.BTCPayRaffle`
- EF migration `20260517000000_InitialCreate` applied on startup via `PluginMigrationRunner`

### Requirements

- BTCPay Server ≥ 2.3.7
- .NET 10 runtime (BTCPay Server host)
- PostgreSQL
