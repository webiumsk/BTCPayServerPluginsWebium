# LN Address Connect - release notes

## 1.0.1

- **Fix: payments failed for sat-denominated wallets (Blink).** BTCPay prices Lightning
  invoices in millisatoshis, so a fiat-priced invoice is usually a sub-satoshi amount.
  Blink's LNURL server refuses those (`amount must be a whole sat amount`), so the BOLT11
  could not be minted and the LNURL fallback failed for every wallet (Phoenix, Blink, ...).
  Amounts are now rounded up to the next whole satoshi both when minting the invoice
  (BTCPay reconciles the difference via its tweak fee) and in the LNURL-pay bounds BTCPay
  serves, so wallets request an amount the LNURL server will mint.
- Fix: BTCPay's LNURL-pay callback passes its serialized LNURL metadata as the invoice
  "description"; it is no longer forwarded to the wallet's LNURL server as a LUD-12 comment.
- Blink (`user@blink.sv`) added to the curated wallet branding.
- README: documented that the Blink app pays a Blink address intraledger when using the
  LNURL option, bypassing the BTCPay invoice (BOLT11 QR is unaffected).

## 1.0.0

First release. Generalizes the Blitz (1.0.0) and Flash (1.0.0) plugins into one
wallet-agnostic LN-address backend:

- `type=lnaddress;ln-address=user@domain` - any LUD-21-capable Lightning address.
- Legacy `type=blitz` / `type=flash` connection strings keep working unchanged,
  including bare-username expansion to their historical default domains.
- Curated wallet branding (display name) for Blitz, Flash and Coinos; unknown
  domains show as "LN Address (domain)".
- LUD-21 support is probed at save time (Validate) with a >=1 sat clamped probe.
- Tracked in-flight invoices are migrated read-only from the legacy
  `Blitz.TrackedInvoices` and `Flash.TrackedInvoices` settings on first load.

### Upgrade

1. Uninstall the Blitz and Flash plugins. While a legacy plugin is still
   installed this plugin does not claim its `type=` value (safety guard), but
   the duplicate setup-tab entry and settings churn remain until removal.
2. Install this plugin and restart BTCPay Server.
3. No store changes needed - existing connection strings stay valid.
