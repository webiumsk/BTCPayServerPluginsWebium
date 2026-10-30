# BTCPay Server Plugins

This repository contains BTCPay Server plugins.

## Current compatibility line

The maintained compatibility branch pins BTCPay **2.4.7** exactly and all eight new artifacts require at least 2.4.7. Use the existing host submodule and its source guard; the legacy module list and sibling-build notes below do not replace that pin. See [the current version, test and package matrix](docs/BTCPAY_2_4_7_COMPATIBILITY.md), including SEPA prerequisites and Raffle schema-history boundaries.

## Plugins

### [Cashu Melt](Plugins/BTCPayServer.Plugins.CashuMelt/README.md)

Cashu Melt is a BTCPay Server payment-method plugin that adds a Cashu-assisted Lightning checkout path for your store. When a customer chooses this method, the plugin asks the configured Cashu mint for a mint quote and shows the resulting BOLT11 invoice. The customer pays that invoice from a Cashu-capable wallet or any Lightning wallet that can pay it. The server then polls the mint, and once the quote is paid/issued it mints Cashu proofs, immediately melts them toward the merchant’s Lightning address (resolved via LNURL-pay), and only after a successful melt records the payment in BTCPay so the invoice can settle. If minting succeeds but BTCPay accounting fails, the plugin keeps a MELT_COMPLETE state so retries can finish bookkeeping without re-spending tokens. Transient mint errors (for example 429 and some 5xx responses) are handled with server-side backoff and optional retryAfterSeconds hints so checkout polling does not hammer the mint. For stores that also expose native Lightning, checkout can prefer BTC-LN when the URL did not pin a payment method, so regular Lightning users are not forced onto Cashu by default.

### [BTCPay Raffle](Plugins/BTCPayServer.Plugins.BTCPayRaffle/README.md)

Raffle / tombola plugin: sell numbered tickets through BTCPay checkout, email ticket numbers to buyers, add manual tickets from the store UI, and run live prize draws (store draw page or token-based presenter screen for events). Buyer **email is required** on purchase and manual tickets; an optional **display name** (nickname) appears on ticket lists and during draws. **Buyer wallet** (≥ 1.3.0.0) aggregates all tickets for one email on a raffle with live draw highlights. API and draw UIs expose **masked** email addresses only; missing display names show as **—** (no fallback to email).

### [Satoshi Tickets](Plugins/BTCPayServer.Plugins.SatoshiTickets/README.md)

Bitcoin-powered event ticketing for BTCPay Server: events, ticket tiers, checkout, QR check-in, Greenfield API. Webium fork adds **Greenfield purchase**, **offline ticket creation**, **all event states in API**, and **raffle bundle per ticket type** (fulfillment via BTCPay Raffle after invoice settle). Admin UI for raffle bundle on ticket tiers requires **1.3.8.0+** and BTCPay Raffle plugin installed.

## Build

Requires the BTCPay Server source for `ProjectReference` builds. Use either layout:

- **Sibling checkout** (typical): clone this repo and BTCPay Server next to each other, e.g. `../btcpayserver` or `../BTCPayServer` (directory name must match the path in each plugin `.csproj` / `build-plugin.sh`).
- **Submodule in this repo**: `submodules/btcpayserver` (already wired in plugin projects here).

`BTCPayServerPluginsKukks` is an older wrapper repo name some setups still use; it should contain the same BTCPay Server tree. **PluginPacker** for `.btcpay` packages may live under that wrapper’s `submodules/btcpayserver` — see each plugin’s `build-plugin.sh`.

```bash
# Cashu Melt
cd Plugins/BTCPayServer.Plugins.CashuMelt
./build-plugin.sh

# BTCPay Raffle
cd Plugins/BTCPayServer.Plugins.BTCPayRaffle
./build-plugin.sh

# Satoshi Tickets
cd Plugins/BTCPayServer.Plugins.SatoshiTickets
./build-plugin.sh
```

Output (version from each plugin `.csproj`):

- `packaged/BTCPayServer.Plugins.CashuMelt/<version>/BTCPayServer.Plugins.CashuMelt.btcpay`
- `packaged/BTCPayServer.Plugins.BTCPayRaffle/<version>/BTCPayServer.Plugins.BTCPayRaffle.btcpay`
- `packaged/BTCPayServer.Plugins.SatoshiTickets/<version>/BTCPayServer.Plugins.SatoshiTickets.btcpay`
