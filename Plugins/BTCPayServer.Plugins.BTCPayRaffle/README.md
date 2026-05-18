# BTCPay Raffle — BTCPay Server Plugin

**Lightning-powered raffles / tombolas** for BTCPay Server stores: sell numbered tickets via standard checkout, run live prize draws (slot-machine UI or token-based presenter screen), and give buyers verifiable ticket pages.

## Requirements

- BTCPay Server ≥ 2.3.7 (targets .NET 10; see [Migrating to .NET 10](https://blog.btcpayserver.org/migrating-to-net10/))
- .NET 10 SDK (matching the plugin target)
- PostgreSQL (already used by BTCPay Server)

Release notes (versioned upgrades): [RELEASE_NOTES.md](RELEASE_NOTES.md).  
Greenfield / Satflux API: [docs/AGENT_API.md](docs/AGENT_API.md).

## Installation

### Option A: Install from `.btcpay` package (recommended)

1. Build the installable package:

   ```bash
   cd Plugins/BTCPayServer.Plugins.BTCPayRaffle
   ./build-plugin.sh
   ```

2. Output:

   ```
   packaged/BTCPayServer.Plugins.BTCPayRaffle/<version>/BTCPayServer.Plugins.BTCPayRaffle.btcpay
   ```

3. In BTCPay Server go to **Settings → Plugins** and upload the `.btcpay` file.

### Option B: Local development (`DEBUG_PLUGINS`)

Point `appsettings.dev.json` at `bin/Debug/net10.0/BTCPayServer.Plugins.BTCPayRaffle.dll` after `dotnet build`.

## Store operator workflow

1. **Store → Integrations → Raffle** — create a raffle (draft).
2. Set name, description, ticket price (SATS or fiat), optional max tickets.
3. **Open** sales — public page goes live at `/raffle/{raffleId}`.
4. **Close** sales when ready, then open the **Draw** page for live draws (or use a presenter token for a big screen).
5. **Complete** when all prizes are drawn.

### Manual tickets

On the raffle **Manage** page you can add complimentary or cash tickets without payment (while sales are open or closed, before the first draw):

| Field | Rule |
|-------|------|
| **Email** | Required — used for receipts and internal records; never shown in full on public lists or draw screens. |
| **Display name** | Optional — shown on the ticket list and during the draw (e.g. a nickname). |

Same rules apply on the public buy form and in the Greenfield API (`POST .../tickets/manual`).

## Public ticket purchase

Buyers open `/raffle/{raffleId}`, choose ticket count, and pay via the store’s normal BTCPay checkout (Lightning, Cashu, etc.).

| Field | Rule |
|-------|------|
| **Email** | Required (HTML + server validation). Ticket numbers are emailed after payment when the store has email configured. |
| **Display name** | Optional — appears on operator ticket lists and draw/presenter UIs; use a nickname if you do not want your real name shown. |

After payment, buyers get a receipt page and per-ticket verification URLs.

## Privacy & display (`RaffleBuyerDisplay`)

Ticket and draw responses **mask buyer email** (e.g. `a***@example.com`) in:

- Greenfield API (`GET .../tickets`, `GET .../drawings`)
- Store draw JSON and public **presenter** draw JSON

**Display name** is shown as entered, or **`—`** when empty — there is **no fallback to email** on list or draw UIs. Full addresses stay in the database and email receipts only.

Helper: `Services/RaffleBuyerDisplay.cs` (`MaskEmail`, `DisplayBuyerName`).

## Presenter mode (Satflux / event screen)

Mint a short-lived token via Greenfield:

`POST /api/v1/stores/{storeId}/raffle/{raffleId}/presenter-token`

Open `presenterUrl` on a projector — draws run through the presenter UI without a BTCPay login. See [docs/AGENT_API.md](docs/AGENT_API.md).

## Greenfield API

Base path: `/api/v1/stores/{storeId}/raffle` (API key with **Modify Store**).

- CRUD raffles, open/close sales, manual tickets, draw next prize, undo last draw, complete
- Presenter token + draw state (≥ 1.2.0.0)
- Ticket/draw payloads return masked `buyerEmail` / `winnerEmail` and display `buyerName` / `winnerName` per rules above

**Note:** `POST .../draw` on the Greenfield API requires a **BTCPay user session**, not only an API key — use the presenter UI or store Draw page for unattended screens. Details in [docs/AGENT_API.md](docs/AGENT_API.md).

## Database

- Isolated PostgreSQL schema: `BTCPayServer.Plugins.BTCPayRaffle`
- Migrations applied on startup via `PluginMigrationRunner`

## Build

```bash
cd Plugins/BTCPayServer.Plugins.BTCPayRaffle
dotnet build -c Release
./build-plugin.sh
```

Requires the BTCPay Server codebase (or `submodules/btcpayserver` in this repo) and **PluginPacker** from a sibling `BTCPayServerPluginsKukks` checkout — see `build-plugin.sh`.

## Cashu / mixed payment stores

Raffle checkout redirects to BTCPay with the store’s default payment method when available. On Cashu-enabled stores, use **CashuMelt ≥ 1.2.0.2** alongside raffle **≥ 1.0.0.3** (see [RELEASE_NOTES.md](RELEASE_NOTES.md)).
