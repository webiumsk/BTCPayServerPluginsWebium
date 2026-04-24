# Cashu Melt — BTCPay Server Plugin

**Cashu-assisted Lightning checkout:** the customer pays a **mint quote (BOLT11)**; the plugin **mints proofs only to melt them immediately** to the merchant **Lightning address** (LNURL-pay), then records BTCPay after a successful melt. The plugin does **not** operate a customer ecash wallet. **1.1+** adds optional **trusted mint URLs**, **melt fee reserve caps**, **background reconciliation** for `MELT_COMPLETE` / stale `PENDING`, and **CSV export** plus NUT-23 poll links on the settings page.

## Requirements

- BTCPay Server ≥ 2.3.7 (targets .NET 10; see [Migrating to .NET 10](https://blog.btcpayserver.org/migrating-to-net10/))
- .NET 10 (SDK matching the plugin target)
- PostgreSQL (already used by BTCPay Server)

Release notes (upgrade, stuck payments, integrators): [RELEASE_NOTES.md](RELEASE_NOTES.md).  
Agent / Satflux JSON details: [docs/AGENT_API.md](docs/AGENT_API.md).

## Installation

### Option A: Install from .btcpay package (recommended)

1. Build the installable package:
   ```bash
   cd Plugins/BTCPayServer.Plugins.CashuMelt
   ./build-plugin.sh
   ```

2. The script creates a versioned package at:
   ```
   packaged/BTCPayServer.Plugins.CashuMelt/<version>/BTCPayServer.Plugins.CashuMelt.btcpay
   ```

3. In BTCPay Server go to **Settings → Plugins** and upload the `.btcpay` file.

### Option B: Build from source

```bash
cd Plugins/BTCPayServer.Plugins.CashuMelt
dotnet publish -c Release -o bin/publish/BTCPayServer.Plugins.CashuMelt
# Then run PluginPacker — see build-plugin.sh for the full command
```

## Configuration

1. In BTCPay Server go to **Store → Wallets → Cashu Melt**.

2. Fill in:
   - **Cashu Mint URL** — Base URL of your mint (e.g. `https://mint.minibits.cash/Bitcoin`)
   - **Lightning address** — Merchant payout destination (e.g. `you@getalby.com`)

3. Click **Save** and toggle **Enable Cashu Melt payments** on.

4. **Stuck payments:** the same page lists recent CashuMelt rows. If the customer paid but settlement did not finish, use **Retry settlement** on rows that are not `SETTLED` (for example `PENDING`, `MELT_COMPLETE`, or `FAILED` when proofs are still stored). This matches the Greenfield endpoint `POST .../payments/{quoteId}/retry`.

## Payment Flow

1. Customer opens checkout — a QR code with the mint Lightning invoice is shown.
2. Customer pays from their Cashu wallet (scan QR or `lightning:` URI).
3. Plugin polls the mint; on **429** / transient errors it returns a controlled poll response (no exception) and applies server-side backoff so the mint is not hammered every 2 seconds.
4. When the quote is **PAID/ISSUED**, the plugin mints tokens (NUT-05), then melts (NUT-14) to the merchant Lightning address. **Only after a successful melt** does it record the payment in BTCPay and mark the plugin row **SETTLED** (so the invoice is not settled before the forward completes).
5. If BTCPay recording fails after a successful melt, the row is **MELT_COMPLETE** and polls / the retry API will retry accounting only (tokens are already spent).

### Settlement states (`SettlementState`)

| Value | Meaning |
|-------|---------|
| `PENDING` | Quote not finished, or melt not done, or transient melt/mint errors (retry). |
| `MELT_COMPLETE` | Melt to merchant succeeded; waiting for BTCPay `AddPayment` + `ReceivedPayment` (retry safe). |
| `SETTLED` | Melt OK and invoice recorded in BTCPay. |
| `FAILED` | Hard failure (see `SettlementError`); may be retriable via API if proofs still stored. |

### BTCPay invoice `Settled` vs CashuMelt settlement (explicit)

| Moment | What happens |
|--------|----------------|
| Customer pays mint LN invoice | Mint quote → `PAID` / `ISSUED`; plugin persists `PaidAt` / quote state. |
| Before melt completes | BTCPay invoice is **not** settled by this plugin; checkout poll returns `paid: false` (customer may still see “waiting”). |
| After successful **melt** | Mint pays the merchant’s BOLT11 (Lightning forward). |
| Immediately after that | Plugin calls `AddPayment` + publishes `ReceivedPayment` → BTCPay moves invoice toward **Settled**. |
| Plugin row `SETTLED` | Same successful path: forward + BTCPay row are done. |

**Exception:** if the BTCPay invoice was already **Settled** or **Invalid** via another payment method, the plugin skips melt and marks its row `SETTLED` (log tag `cashumelt_skipped_invoice_finalized_elsewhere`).

### Polling, mint rate limits, checkout UX

- The server treats mint **429, 500, 502, 503, 504** on quote poll as **transient** (no exception thrown to ASP.NET); poll returns **HTTP 200** with optional `retryAfterSeconds`.
- **Per-quote backoff** is applied server-side so a 2 s client loop does not call the mint on every tick while throttled.
- **Recommended client behavior:** use an interval of **3–5 s** by default, or **`max(2 s, retryAfterSeconds)`** when the poll JSON includes `retryAfterSeconds`.

### Logs (support)

Successful path (same `invoice` / `quote`), grep order:

`cashumelt_mint_proof_ok` → `cashumelt_forward_ok` → `cashumelt_btcpay_recorded` → `cashumelt_settlement_complete`.

Failures: `cashumelt_settlement_failed` (with `phase` and `msg` / structured detail).

## Tests

```bash
dotnet test Plugins/BTCPayServer.Plugins.CashuMelt.Tests/BTCPayServer.Plugins.CashuMelt.Tests.csproj
```

## Cashu NUT API used

| NUT | Endpoint | Purpose |
|-----|----------|---------|
| NUT-04 | `POST /v1/mint/quote/bolt11`, `POST /v1/mint/bolt11` | Create Lightning quote, mint tokens |
| NUT-05 | `POST /v1/melt/quote/bolt11`, `POST /v1/melt/bolt11` | Melt tokens via Lightning |
| NUT-23 | `GET /v1/mint/quote/bolt11/{quote_id}` | Poll quote state |

Reference: https://github.com/cashubtc/nuts

## Project Structure

```
BTCPayServer.Plugins.CashuMelt/
├── Plugin.cs                          # DI registration, UI extensions
├── PluginMigrationRunner.cs           # DB migrations + schema rename from legacy "Cashu"
├── build-plugin.sh                    # Builds installable .btcpay package
├── Controllers/
│   ├── UICashuMeltController.cs       # Admin settings UI
│   ├── CashuMeltCheckoutController.cs # Checkout polling endpoint (/plugins/cashumelt/poll)
│   └── CashuMeltApiController.cs      # REST API for external agents (e.g. Satflux)
├── Services/
│   ├── CashuMeltPaymentService.cs     # Core payment logic (mint → melt)
│   ├── CashuMeltMintClient.cs         # HTTP client for Cashu NUT API
│   ├── CashuMeltConfigService.cs      # Per-store configuration
│   └── LightningAddressResolver.cs    # LNURL-pay resolution
├── PaymentHandler/
│   ├── CashuMeltPaymentMethodHandler.cs
│   └── CashuMeltCheckoutModelExtension.cs
├── Data/
│   ├── CashuMeltDbContext.cs
│   └── Entities/
│       ├── CashuMeltStoreSettings.cs
│       └── CashuMeltPaymentRequest.cs
└── Views/
    ├── Shared/CashuMelt/              # Checkout Vue component, nav extension
    └── UICashuMelt/                   # Admin settings page
```

## Database

PostgreSQL schema: `BTCPayServer.Plugins.CashuMelt`

| Table | Contents |
|-------|----------|
| `CashuMeltStoreSettings` | Mint URL and Lightning address per store |
| `CashuMeltPaymentRequests` | Quote ID, invoice ID, minted proofs, settlement state |

Migrations run automatically on startup. If upgrading from the legacy `BTCPayServer.Plugins.Cashu` plugin, the schema and tables are renamed automatically.

## API

External agents (e.g. Satflux) can manage settings via REST:

```
GET  /api/v1/stores/{storeId}/plugins/cashumelt/settings
PUT  /api/v1/stores/{storeId}/plugins/cashumelt/settings
GET  /api/v1/stores/{storeId}/plugins/cashumelt/payments
GET  /api/v1/stores/{storeId}/plugins/cashumelt/payments/{quoteId}
POST /api/v1/stores/{storeId}/plugins/cashumelt/payments/{quoteId}/retry
```

Poll JSON and retry response extensions: [docs/AGENT_API.md](docs/AGENT_API.md).
