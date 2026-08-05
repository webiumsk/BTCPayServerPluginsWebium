# BTCPay Server — Blitz Wallet Plugin

Use your self-custodial [Blitz Wallet](https://blitz-wallet.com/) Lightning address as a **receive-only**
Lightning backend for a BTCPay Server store. No node, no API keys, no changes to the Blitz app — BTCPay
never holds your keys.

## How it works

1. Blitz gives every user a Lightning address (`you@blitzwalletapp.com`) served by Blitz's LNURL server.
2. When a checkout invoice is created, the plugin requests a BOLT11 invoice from that LNURL-pay endpoint.
   Invoices are minted server-side on Spark, so **your phone does not need to be online** to get paid.
3. Settlement is detected by polling the [LUD-21](https://github.com/lnurl/luds/blob/luds/21.md)
   `verify` URL returned with each invoice (every ~3 s, with backoff). Tracked invoices are persisted, so
   payments made while BTCPay is restarting are still detected.

## Setup

In your store: **Settings → Lightning → Use custom node**, then enter:

```
type=blitz;ln-address=you
```

or with a full address (non-default domains are allowed as long as their LNURL server supports LUD-21 verify):

```
type=blitz;ln-address=you@blitzwalletapp.com
```

Saving the connection runs a validation probe that creates one minimal test invoice to confirm the server
supports LUD-21 verify (the test invoice can be ignored; it simply expires).

## Limitations

- **Receive-only.** Sending, refunds, balance and channel operations are not supported — the keys stay in
  your Blitz app. Payouts/refunds need a different wallet.
- **Amount-only invoices.** Amountless (top-up) invoices are not supported by LNURL-pay; BTCPay's LNURL
  flow covers that case.
- **Payer sees Blitz identity.** Because the invoice's description hash is committed by Blitz's server,
  the plugin mirrors Blitz's LNURL metadata on BTCPay's own LNURL endpoint (required for strict wallets
  like Phoenix or Blitz itself to pay). Payer wallets therefore show "Pay to you@blitzwalletapp.com"
  instead of your store description.
- **Mainnet.** Blitz addresses are mainnet-only.

## Build

```bash
cd Plugins/BTCPayServer.Plugins.Blitz
dotnet build -c Release      # requires BTCPay Server ≥ 2.3.7 sources (see csproj reference paths)
./build-plugin.sh            # packages ../../packaged/BTCPayServer.Plugins.Blitz/<version>/*.btcpay
```

Tests: `Plugins/BTCPayServer.Plugins.Blitz.Tests/`.
