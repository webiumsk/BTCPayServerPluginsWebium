# Release Notes

## 1.0.0

Initial release.

- Use a Flash (lnflash) Lightning address (`you@flashapp.me`) as a receive-only store Lightning
  backend: `type=flash;ln-address=you`.
- Invoices minted by Flash's LNURL server (IBEX Hub) — the merchant's phone does not need to be online.
- Settlement detection via LUD-21 `verify` polling (single shared poller, capped backoff), with
  tracked-invoice persistence across BTCPay restarts.
- Mirrors the LNURL server's metadata on BTCPay's own LNURL-pay endpoint so strict wallets accept
  payments; narrows advertised min/max sendable and comment length to the server's limits.
- Config-time validation probes the address for LUD-21 verify support (≥ 1 sat probe — Flash
  advertises a 1 msat minimum that Lightning backends may reject).
- SSRF hardening on all outbound requests: public-https-only URL policy, no redirects, per-connect
  DNS filtering of private/reserved ranges (DNS-rebinding safe).
