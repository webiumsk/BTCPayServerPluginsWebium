# BTCPay Server Plugins

This repository contains BTCPay Server plugins.

## Plugins

### [Cashu Melt](Plugins/BTCPayServer.Plugins.CashuMelt/README.md)

Cashu Melt is a BTCPay Server payment-method plugin that adds a Cashu-assisted Lightning checkout path for your store. When a customer chooses this method, the plugin asks the configured Cashu mint for a mint quote and shows the resulting BOLT11 invoice. The customer pays that invoice from a Cashu-capable wallet or any Lightning wallet that can pay it. The server then polls the mint, and once the quote is paid/issued it mints Cashu proofs, immediately melts them toward the merchant’s Lightning address (resolved via LNURL-pay), and only after a successful melt records the payment in BTCPay so the invoice can settle. If minting succeeds but BTCPay accounting fails, the plugin keeps a MELT_COMPLETE state so retries can finish bookkeeping without re-spending tokens. Transient mint errors (for example 429 and some 5xx responses) are handled with server-side backoff and optional retryAfterSeconds hints so checkout polling does not hammer the mint. For stores that also expose native Lightning, checkout can prefer BTC-LN when the URL did not pin a payment method, so regular Lightning users are not forced onto Cashu by default.

### [BTCPay Raffle](Plugins/BTCPayServer.Plugins.BTCPayRaffle/README.md)

Raffle / tombola plugin: sell numbered tickets through BTCPay checkout, email ticket numbers to buyers, add manual tickets from the store UI, and run live prize draws (store draw page or token-based presenter screen for events). Buyer **email is required** on purchase and manual tickets; an optional **display name** (nickname) appears on ticket lists and during draws. API and draw UIs expose **masked** email addresses only; missing display names show as **—** (no fallback to email).

## Build

Requires the BTCPay Server codebase cloned as a sibling directory (`BTCPayServerPluginsKukks`) or `submodules/btcpayserver` in this repo.

```bash
# Cashu Melt
cd Plugins/BTCPayServer.Plugins.CashuMelt
./build-plugin.sh

# BTCPay Raffle
cd Plugins/BTCPayServer.Plugins.BTCPayRaffle
./build-plugin.sh
```

Output (version from each plugin `.csproj`):

- `packaged/BTCPayServer.Plugins.CashuMelt/<version>/BTCPayServer.Plugins.CashuMelt.btcpay`
- `packaged/BTCPayServer.Plugins.BTCPayRaffle/<version>/BTCPayServer.Plugins.BTCPayRaffle.btcpay`
