# LN Address Connect - release notes

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
