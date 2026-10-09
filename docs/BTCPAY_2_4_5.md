# BTCPay Server 2.4.5 packages

Build against the pinned host commit `5d0745cae6be5d8210459e38813f317673aa97b8` with .NET 10. Run `git submodule update --init submodules/btcpayserver` before building. The package scripts now use this repository's host and PluginPacker rather than a sibling Kukks checkout. CI checks the exact host and runs the existing plugin suites.

| Plugin | New project version |
|---|---|
| BTCPay Raffle | 1.3.2.3 |
| CashuMelt | 1.3.1.1 |
| SEPA Instant QR | 0.8.1 |
| Satflux Tickets | 2.0.1 |
| LnAddress Connect | 1.0.2 |
| Blitz | 1.0.1 |
| Flash | 1.0.1 |

These packages require BTCPay Server 2.4.5. Npgsql is aligned to 10.0.3, QRCoder to 1.8.0 where referenced, EF/Identity to 10.0.12, and the conditional Roslyn family to 5.9.0. Test-only framework and host dependencies match the same host. Migration IDs, schemas, migration runners, and history keys are unchanged, including Tickets' historical `BTCPayServer.Plugins.SimpleTicketSale` history identity.

Address connections may add the actual metadata origin: `type=lnaddress;ln-address=merchant@coinos.io;server=https://coinos.io;`. Blitz and Flash follow the same rule for their own address domains. The optional `server` is checked against the address's HTTPS origin; omitted values preserve legacy parsing. An unrelated server is rejected before making a request. Existing DNS-pinned LNURL transport, HTTPS callback checks, and redirect refusal remain in place.

CashuMelt mint, LightningAddressResolver, and D21 fallback clients use BTCPay's DNS-pinned SSRF transport, 30-second timeouts, and disabled redirects. Credentials in URL authority and URL fragments are rejected. HTTPS is the default. An existing internal HTTP D21 service needs an exact entry in `BTCPAY_PLUGIN_HTTP_ALLOWED_HTTP_ORIGINS` (semicolon-separated origins, such as `http://panel.internal:8080`) plus an exact host/IP and port in `BTCPAY_SSRFEXCEPTIONS`. Hostname exceptions intentionally trust their resolved addresses; authorize only operator-controlled names. Do not globally disable SSRF protection.

For staging, back up host/plugin databases and configuration, pause wallet edits and invoice creation during the host/package restart, install these versions with the 2.4.5 host, and then enable the compatible Satflux deployment. Verify existing migrations, pending invoices, Cashu mint/melt, SEPA settlement, Tickets/Raffle fulfillment, and restart recovery using staging wallets. No production plugin installation or database rewrite is performed by these changes.

Rollback requires the previous host/package set and a coordinated database backup if host migration rollback is needed. Keep Tickets' fork identity and migration history; never substitute an upstream ticket package. Preserve callbacks and their signing secrets. The local source builds and tests do not establish funded settlement readiness.
