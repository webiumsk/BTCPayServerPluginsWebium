# LN Address Connect (BTCPayServer.Plugins.LnAddressConnect)

Receive-only Lightning backend for BTCPay Server driven by nothing but a Lightning
address. Works with **any wallet whose LNURL server supports LUD-21 `verify`** -
Blitz Wallet, Blink, Flash and Coinos are curated (branding, tested), but unknown
domains work too: support is probed when the connection is saved.

## Connection strings

```text
type=lnaddress;ln-address=you@yourwallet.com
```

Legacy strings from the superseded Blitz and Flash plugins keep working
(including bare usernames, which expand to the wallet's historical domain):

```text
type=blitz;ln-address=you           -> you@blitzwalletapp.com
type=flash;ln-address=you           -> you@flashapp.me
```

## Upgrading from the Blitz / Flash plugins

**Uninstall both plugins before installing this one.** As a safety net, while a
legacy plugin is still installed this plugin does not claim its `type=` value (the
old plugin keeps handling it), so dispatch stays deterministic - but the duplicate
setup-tab entry and settings churn remain until the old plugin is removed. Tracked in-flight invoices are migrated automatically on first load
(read-only) from the legacy `Blitz.TrackedInvoices` / `Flash.TrackedInvoices` settings.
Store configuration needs no changes - the legacy `type=` values stay valid.

## How it works

- Invoices are minted via the wallet's LNURL-pay endpoint (LUD-16), so the merchant's
  phone does not need to be online to get paid.
- Settlement is detected by polling the LUD-21 `verify` URL (batched per host, backoff).
- Receive-only: no sending, balances or channel operations - payouts happen in the
  wallet app itself.
- Amounts are rounded **up to whole satoshis** before the LNURL callback (and the
  LNURL-pay bounds BTCPay serves are rounded the same way). BTCPay prices Lightning
  invoices in millisatoshis, and sat-denominated LNURL servers such as Blink refuse
  sub-satoshi amounts (`amount must be a whole sat amount`). BTCPay reconciles the
  sub-satoshi difference automatically.
- All outbound HTTP is SSRF-guarded (https-only, public hosts, redirects disabled).

## Known limitations

- **Blink app paying a Blink address via LNURL.** The served LNURL metadata must mirror
  the wallet's own (it is committed by the invoice's description hash), and for
  `user@blink.sv` it contains a `text/identifier` the Blink app recognises as its own.
  The Blink app then pays that address **intraledger**, outside the BTCPay invoice, so
  the merchant is paid but the invoice never settles. This only affects the LNURL-pay
  option (and the store's BTCPay Lightning address); the regular BOLT11 invoice QR is
  unaffected - a Blink app paying it settles the invoice normally.
