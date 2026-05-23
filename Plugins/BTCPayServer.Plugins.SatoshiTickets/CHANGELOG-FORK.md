# Changelog — Webium fork (Satoshi Tickets)

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
