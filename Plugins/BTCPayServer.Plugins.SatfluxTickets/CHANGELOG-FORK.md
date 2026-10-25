# 2.2.2 (native BTCPay time-zone catalog)

- Use the host's native time-zone catalog and resolver rather than injecting a Bratislava alias that is absent from the native picker.
- Normalize historical Bratislava metadata to supported Prague metadata without changing UTC instants, reminders, ticket/order data or native stale-edit tokens. Preserve the original migration and add a metadata-only normalization step.
- Keep existing Greenfield/legacy UTC input and public/native schedule behavior; unsupported zones remain explicit rather than silently selecting a different offset.
- Correct the time-zone endpoint's OpenAPI path parameters and create/update/response field descriptions. The recovered 2.2.1 source remains immutable at its recorded commit.

# 2.2.1 (event share previews)

- Add server-rendered Open Graph title, website type, canonical event URL and the public event image with alt text. Use the public store logo only when there is no event image.
- Resolve absolute HTTP(S) image URLs with PathBase support, reject unsafe schemes/credentials/control characters and keep recipient, session, checkout and virtual-location data out of metadata.
- Keep existing CSP, robots, payment, timezone and fulfillment behavior. No core, API or schema change; prepare a separately tested local installer.

# 2.2.0 (physical event venue time zones)

- Persist an explicit IANA venue timezone while preserving UTC event instants. The metadata-only migration assigns Europe/Bratislava to existing physical events, as confirmed by the Product Owner; virtual events remain unset.
- Render physical event schedules in the venue zone for every visitor, including server-rendered/no-JavaScript output. Virtual event schedules continue to use the visitor's zone.
- Extend existing native and Satflux authoring with venue civil times and provider-authoritative validation; preserve exact instants and reminders on zone-only edits. Reject nonexistent and ambiguous civil times rather than silently choose an offset.
- Keep older UTC API clients compatible, validate capability before new consumer writes and preserve payment/fulfillment policies. Local tests and independent review are recorded before delivery; no production deployment is implied.

- Reject stale native schedule forms before conversion or save; preserve the draft and require reload when original UTC start/end baselines no longer match.

# 2.1.2 (header language selection and reliable sales assets)

- Move language selection into the public header and update the existing page immediately, preserving quantities, recipient drafts and checkout intent.
- Give embedded sales assets content-versioned URLs and explicit cache behaviour so upgrades do not reuse older presentation scripts.
- Preserve UTC event instants, browser-zone/DST formatting, labelled UTC fallback, CSP, backend authorization and payment/fulfillment handling.
- Exact-host build, 115 non-database .NET cases, 10 JS cases, independent security review, 26 actual shared-layout/CSP/resource browser cases and installer integrity pass. This is a local candidate; production cache provenance remains unverified. No core or third-party plugin change.

# 2.1.1 (public sales language and local event times)

- Resolve public sales language independently of the host's invariant UI culture, using supported explicit choice and browser language preferences (EN/SK/ES/DE/CS).
- Present UTC event instants in the visitor's browser time zone with an explicit zone label and daylight-saving conversion; retain labelled UTC when local formatting is unavailable.
- Preserve stored event dates, Greenfield payloads, checkout intent, payment handling and fulfillment. No database migration, BTCPay core or third-party plugin change.
- Exact-host build, 108 non-database .NET tests (23 new presentation cases), 7 JS tests, independent security review, 39 focused/32 existing browser checks and final six-scenario embedded contrast/smoke validation pass. The previous 15 PostgreSQL cases remain earlier milestone evidence, not a new run.
- Correct the language action's focused text contrast with plugin-scoped colors while retaining keyboard focus and 44px targets. This is a local candidate, not a published release.

# 2.1.0 (unified public sales and persistent ticket-type ordering)

- Persist event ticket-type display order with an additive migration and deterministic name/ID backfill. New types append; disabled types retain positions.
- Add scoped ordering snapshot and compare-and-swap APIs with legacy route aliases, atomic full-permutation validation and shared membership locks.
- Extend the existing native ticket-type list with inline drag and keyboard ordering, Save/Cancel and revision-conflict handling.
- Combine public event overview, quantities and attendee contacts at existing URLs. Render configured physical/virtual event data, full rich description, optional image, UTC times and effective currency.
- Preserve hosted BTCPay checkout, canonical server price/quantity validation, recipient grouping, email QR delivery, raffle fulfillment and issued ticket links.
- Provide EN/SK/ES/DE/CS sales copy, accessible controls and responsive merchant-themed layout. No BTCPay core or third-party plugin source changes.
- Recover invoices with exact immutable-intent checks and a durable pre-creation attempt marker. Uncertain outcomes are lookup-only; a plugin-private reconciliation queue uses the existing fulfillment handler under a shared checkout lock.
- Keep virtual joining locations out of anonymous HTML/data; preserve them for the existing confirmation/ticket flow. Fully sold-out active events remain viewable with new purchases disabled.
- Frozen build and 100 tests pass, including 15 disposable PostgreSQL cases. Independent QA passes 32 compiled-Razor/embedded-asset checks and 9 visual/contrast scenarios; independent security accepts the bounded source.
- Do not mix older/newer plugin workers or reset uncertain attempt markers. Plugin rollback needs pending-intent reconciliation and compatibility review, even though the schema change is additive.
- This is a local feature candidate, not a published release. Final test and independent review evidence is recorded in the Satflux implementation handoff.

# 2.0.4 (event image API contract)

- Return the persisted event logo file ID and resolved storage URL in every Greenfield event response, including upload and deletion. Never infer a public URL from an opaque file ID.
- Keep image operations scoped to the authorized store and event, with JPEG/PNG/GIF/WebP validation and an explicit 2 MiB upload limit.
- Preserve the 2.0.3 grouped ticket email and inline QR behavior. No database migration or BTCPay core change is required.

# 2.0.3 (short ticket numbers in email)

- Display only the final ticket-number segment in HTML and plain-text emails; keep the complete identifier in the QR code for check-in.
- Send one email per recipient with all of their ticket numbers, inline QR codes and ticket links from the order; keep different recipients separate.
- Include all settled tickets in grouped event reminders instead of dropping additional tickets for the same email address.

# 2.0.2 (ticket email improvements)

- Show each ticket number centered and bold in ticket emails, with an inline QR code and a plain-text ticket link fallback.
- Reuse the same ticket-number QR payload in the public ticket page and email.

# 2.0.1 (BTCPay 2.4.5)

- Align Npgsql, QRCoder, Identity/EF, and conditional Roslyn dependencies to the exact 2.4.5 host; require host 2.4.5.
- Build and pack with the repository's pinned host. No migration IDs, schema/history identities, legacy routes, or fulfillment behavior changed.
- Verified new-database startup migrations and restart on an isolated 2.4.5 host. Existing production-data upgrade and paid ticket/raffle fulfillment remain staging gates.

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
