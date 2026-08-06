# Satflux LN Address (BTCPayServer.Plugins.LnAddress)

Receive-only Lightning backend for BTCPay Server driven by nothing but a Lightning
address. Works with **any wallet whose LNURL server supports LUD-21 `verify`** -
Blitz Wallet, Flash and Coinos are curated (branding, tested), but unknown domains
work too: support is probed when the connection is saved.

## Connection strings

```
type=lnaddress;ln-address=you@yourwallet.com
```

Legacy strings from the superseded Blitz and Flash plugins keep working
(including bare usernames, which expand to the wallet's historical domain):

```
type=blitz;ln-address=you           -> you@blitzwalletapp.com
type=flash;ln-address=you           -> you@flashapp.me
```

## Upgrading from the Blitz / Flash plugins

**Uninstall both plugins before installing this one.** Running the old plugins side by
side causes non-deterministic connection-string dispatch, duplicate pollers and
settings churn. Tracked in-flight invoices are migrated automatically on first load
(read-only) from the legacy `Blitz.TrackedInvoices` / `Flash.TrackedInvoices` settings.
Store configuration needs no changes - the legacy `type=` values stay valid.

## How it works

- Invoices are minted via the wallet's LNURL-pay endpoint (LUD-16), so the merchant's
  phone does not need to be online to get paid.
- Settlement is detected by polling the LUD-21 `verify` URL (batched per host, backoff).
- Receive-only: no sending, balances or channel operations - payouts happen in the
  wallet app itself.
- All outbound HTTP is SSRF-guarded (https-only, public hosts, redirects disabled).
