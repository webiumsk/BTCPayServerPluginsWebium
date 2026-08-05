# Release Notes

## 1.0.0

Initial release.

- Use a Blitz Wallet Lightning address (`you@blitzwalletapp.com`) as a receive-only store Lightning
  backend: `type=blitz;ln-address=you`.
- Invoices minted by Blitz's LNURL server (Spark) — the merchant's phone does not need to be online.
- Settlement detection via LUD-21 `verify` polling (single shared poller, capped backoff), with
  tracked-invoice persistence across BTCPay restarts.
- Mirrors Blitz's LNURL metadata on BTCPay's own LNURL-pay endpoint so strict wallets (Phoenix, Blitz
  itself) accept payments; narrows advertised min/max sendable and comment length to Blitz's limits.
- Config-time validation probes the address for LUD-21 verify support.
