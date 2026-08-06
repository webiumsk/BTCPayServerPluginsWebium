# BTCPay Server — Flash Plugin

Use your [Flash](https://getflash.io/) (lnflash) Lightning address as a **receive-only** Lightning
backend for a BTCPay Server store. No node, no API keys, no changes to the Flash app.

Flash is a custodial Galoy-based wallet built for the Caribbean; its Lightning addresses are served
by IBEX Hub. This plugin only stores your Lightning address — BTCPay never holds Flash account
credentials, and received funds land directly in your Flash account (custody is with Flash/IBEX,
as with any Flash payment).

## How it works

1. Flash gives every user a Lightning address (`you@flashapp.me`) served by Flash's LNURL server.
2. When a checkout invoice is created, the plugin requests a BOLT11 invoice from that LNURL-pay
   endpoint. Invoices are minted server-side, so **your phone does not need to be online** to get paid.
3. Settlement is detected by polling the [LUD-21](https://github.com/lnurl/luds/blob/luds/21.md)
   `verify` URL returned with each invoice (every ~3 s, with backoff). Tracked invoices are persisted, so
   payments made while BTCPay is restarting are still detected.

## Setup

In your store: **Settings → Lightning → Use custom node**, then enter:

```
type=flash;ln-address=you
```

or with a full address (non-default domains are allowed as long as their LNURL server supports LUD-21 verify):

```
type=flash;ln-address=you@flashapp.me
```

Saving the connection runs a validation probe that creates one minimal test invoice (≥ 1 sat) to
confirm the server supports LUD-21 verify (the test invoice can be ignored; it simply expires).

## Limitations

- **Receive-only.** Sending, refunds, balance and channel operations are not supported — BTCPay has
  no Flash account credentials. Payouts/refunds need a different wallet.
- **Amount-only invoices.** Amountless (top-up) invoices are not supported by LNURL-pay; BTCPay's LNURL
  flow covers that case.
- **Payer sees the Flash identity.** Because the invoice's description hash is committed by Flash's
  LNURL server, the plugin mirrors its metadata on BTCPay's own LNURL endpoint (required for strict
  wallets to pay). Payer wallets therefore show the LNURL-server identity instead of your store
  description.
- **Mainnet.** Flash addresses are mainnet-only.

## Build

```bash
cd Plugins/BTCPayServer.Plugins.Flash
dotnet build -c Release      # requires BTCPay Server ≥ 2.3.7 sources (see csproj reference paths)
./build-plugin.sh            # packages ../../packaged/BTCPayServer.Plugins.Flash/<version>/*.btcpay
```

Packaging uses the `BTCPayServer.PluginPacker` tool, expected by default in a sibling
`BTCPayServerPluginsKukks/submodules/btcpayserver/` checkout; set the `PLUGIN_PACKER` environment
variable to point at any other checkout containing the tool.

Tests: `Plugins/BTCPayServer.Plugins.Flash.Tests/`.
