# Cashu Melt — BTCPay Server Plugin

Accept **Cashu ecash** or **Lightning** payments at checkout. The plugin mints tokens from the customer's payment and automatically melts them to the merchant's Lightning address.

## Requirements

- BTCPay Server ≥ 2.3.0
- .NET 8
- PostgreSQL (already used by BTCPay Server)

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

## Payment Flow

1. Customer opens checkout — a QR code with the mint Lightning invoice is shown.
2. Customer pays from their Cashu wallet (scan QR or `lightning:` URI).
3. Plugin detects the paid quote and immediately records the payment in BTCPay (prevents invoice expiry).
4. Plugin mints Cashu tokens (NUT-04) and melts them (NUT-05/NUT-14) to the merchant's Lightning address via LNURL-pay.
5. BTCPay invoice is marked **Settled**.

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
