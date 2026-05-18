# BTCPay Raffle plugin — release notes

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
