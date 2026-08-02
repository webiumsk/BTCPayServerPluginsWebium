# 2.0.0 (standalone rename)

- The plugin is now **Satflux Tickets** with the standalone identifier
  `BTCPayServer.Plugins.SatfluxTickets` - all namespaces, classes, views
  and routes renamed; no remaining ties to the upstream identity.
- Data survives: the DB schema is renamed in place at startup
  (`ALTER SCHEMA ... RENAME`, idempotent) with the EF migration history
  moving along; migration IDs, the history table name and the per-store
  check-in settings key deliberately keep their historical values.
- Legacy `satoshi-tickets` routes stay as aliases (public ticket links
  already delivered by email/QR + existing satflux/WordPress
  integrations); `satflux-tickets` is canonical.
- BTCPay Raffle 1.3.2.2 bridge accepts all three historical assembly
  identities and both bridge type names.

Production cutover:

1. Upload BTCPay Raffle 1.3.2.2 (.btcpay).
2. Uninstall "Satflux Tickets" 1.4.0 (`SatoshiTicketsWebium`) - plugin
   files only; data stays in Postgres.
3. Upload `BTCPayServer.Plugins.SatfluxTickets.btcpay` 2.0.0 and
   restart - the schema renames itself on first start.
4. Verify events/tickets and the satflux / WordPress integrations
   (they keep using the legacy alias until updated).

# 1.4.0 (identity cutover)

- The fork ships under its own BTCPay plugin identifier
  `BTCPayServer.Plugins.SatfluxTicketsWebium` (assembly rename only -
  namespaces, views, Greenfield routes and the DB schema literal
  `BTCPayServer.Plugins.SatfluxTickets` are unchanged, so all data and
  migration history survive). BTCPay matches update offers by identifier,
  so upstream TChukwuleta releases are never offered as updates again.
- BTCPay Raffle 1.3.2.1 accepts both identities in its integration
  bridge - deploy order does not matter.

Production cutover:

1. Upload BTCPay Raffle 1.3.2.1 (.btcpay).
2. Uninstall the old "Satflux Tickets" plugin in BTCPay (removes plugin
   files only; the plugin schema and data stay in Postgres). Remove a
   stale line from `<plugins dir>/disabled` if present.
3. Upload `BTCPayServer.Plugins.SatfluxTicketsWebium.btcpay` and restart.
4. Verify events/tickets are intact (same DB schema) and satflux /
   WordPress keep working (Greenfield routes unchanged).

# Changelog — Webium fork (Satflux Tickets)

Upstream autor: [TChukwuleta/BTCPayServerPlugins](https://github.com/TChukwuleta/BTCPayServerPlugins).  
Údržba fork: [webiumsk/BTCPayServerPluginsWebium](https://github.com/webiumsk/BTCPayServerPluginsWebium).

Postup pri každom release: [FORK_MAINTENANCE.md](./FORK_MAINTENANCE.md).

---

## [Unreleased]

### Pending
- **Upstream merge** — autor `upstream/main` ~1.3.61; integračná vetva `integrate/upstream-2026-06` (plán 2026-06-20).

---

## [1.3.8.0] — 2026-05-23

### Added
- **BTCPay admin UI** — raffle bundle polia na ticket type (Create/Edit tier): `BundledRaffleTicketsPerAdmission`, výber open raffle z BTCPay Raffle pluginu (reflection, bez compile-time závislosti).
- Zoznam tierov zobrazuje stĺpec Raffle bundle.

### Changed
- Deploy repozitár: [webiumsk/BTCPayServerPluginsWebium](https://github.com/webiumsk/BTCPayServerPluginsWebium) (spolu s CashuMelt a BTCPay Raffle).

---

## [1.3.7.0] — 2026-05-21

### Changed (breaking)
- **Raffle bundle per ticket type** — `bundledRaffleId` a `bundledRaffleTicketsPerAdmission` presunuté z Event API na Ticket Type API (`POST/PUT .../ticket-types`). Migrácia skopíruje existujúce event bundle na všetky ticket types daného eventu.
- Alokácia tombolov po `InvoiceSettled` podľa ticket type; viac tombol na objednávku cez composite `eventOrderId` (`{orderId}:{raffleId}`).

### Requires
- Satflux s podporou bundle na ticket type (vetva `feature/ticket-type-raffle-bundle`).

---

## [1.3.6.4] — 2026-05-21

### Fixed
- **Event raffle bundle** — validácia raffle cez reflection: `ValueTuple` má `Item1`/`Item2` ako **polia**, nie properties → vždy „Invalid raffle validation response“ (aj pri platnom raffle).

---

## [1.3.6.3] — 2026-05-21

### Fixed
- **Event raffle bundle** — `ReflectionRaffleEventBundleClient` čítal `Result` z netypovaného `Task` → `NullReferenceException` pri validácii bundlu; opravené cez `InvokeAsync`.

---

## [1.3.6.2] — 2026-05-21

### Fixed
- Pridaný chýbajúci EF migrácia `20260520120000_EventRaffleBundle.Designer.cs` (oprava `column BundledRaffleId does not exist`).
- `PluginMigrationRunner` loguje pending migrácie.
- Jasnejšia chybová správa pri bundle bez nainštalovaného BTCPay Raffle.

---

## [1.3.6.1] — 2026-05-21

### Fixed
- Plugin sa načíta aj **bez** nainštalovaného BTCPay Raffle (runtime resolver, žiadny compile-time ref na Raffle DLL).

---

## [1.3.6.0] — 2026-05-21

### Fork
- **Greenfield purchase API**, **`create-tickets-offline`**, všetky stavy eventov v API, **event raffle bundle**, fork runbook.

### Merged from upstream
- Base pred fork release: lokálny stav po rebase na `upstream/main` (net10).

---

## Template (po release doplniť)

```markdown
## [X.Y.Z-webium] — YYYY-MM-DD

### Fork
- …

### Merged from upstream
- Base upstream version: …
```
