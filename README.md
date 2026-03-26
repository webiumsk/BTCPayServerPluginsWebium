# BTCPay Server Plugins

This repository contains BTCPay Server plugins.

## Plugins

### [Cashu Melt](Plugins/BTCPayServer.Plugins.CashuMelt/README.md)

Accept Cashu ecash or Lightning payments at checkout. The plugin automatically melts received tokens to the merchant's Lightning address.

## Build

Requires the BTCPay Server codebase cloned as a sibling directory (`BTCPayServerPluginsKukks`).

```bash
cd Plugins/BTCPayServer.Plugins.CashuMelt
./build-plugin.sh
```

Output: `packaged/BTCPayServer.Plugins.CashuMelt/1.0.0/BTCPayServer.Plugins.CashuMelt.btcpay`
