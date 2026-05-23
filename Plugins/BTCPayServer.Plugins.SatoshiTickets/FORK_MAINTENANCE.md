# Satoshi Tickets — Webium fork (runbook)

**Rozhodnutie (máj 2026):** Nepreberáme upstream TChukwuleta ako „zdroj pravdy“ na produkcii. Udržiavame **vlastný fork** v `webiumsk/BTCPayServerPlugins` s jasným versioningom a pravidelným merge z autora.

**Ďalšia plánovaná kontrola:** **20. jún 2026** (mesačný merge + release checklist nižšie).

---

## Repozitáre a remote

| Remote | URL | Účel |
|--------|-----|------|
| `origin` | `https://github.com/webiumsk/BTCPayServerPluginsWebium.git` | Náš fork — deploy, tagy, `.btcpay` (CashuMelt, BTCPay Raffle, Satoshi Tickets) |
| `upstream` | `https://github.com/TChukwuleta/BTCPayServerPlugins.git` | Autor — len merge, nie priama produkcia |

**Deployateľná vetva:** `main` (po zlúčení featúr musí vždy buildovať Release).

**Nikdy na produkciu:** čistý upstream `.btcpay` bez fork API (Satflux + WordPress na tom padnú).

---

## Fork-only funkcie (pri merge „owned regions“)

Tieto súbory / endpointy **neprepisovať slepo** pri `git merge upstream/main`:

| Funkcia | Kde | Spotrebiteľ |
|---------|-----|-------------|
| Offline / manuálne vstupenky | `POST .../satoshi-tickets/events/{eventId}/create-tickets-offline` v `Controllers/GreenfieldSatoshiTicketsController.cs` | WordPress plugin |
| Greenfield purchase | `CreatePurchase` v tom istom controlleri | WordPress / integrácie |
| Všetky stavy eventov v API | `Controllers/GreenfieldSatoshiTicketsEventsController.cs` — bez `EventState == Active` v `GetEvents`/`GetEvent` | Satflux (posiela `includeInactive` + `include_inactive`; plugin vracia všetky stavy) |
| Ticket type raffle bundle | `TicketType.BundledRaffleId`, `BundledRaffleTicketsPerAdmission`, `Controllers/GreenfieldSatoshiTicketTypesController.cs`, `Services/SimpleTicketSalesHostedService.cs`, migrácia `20260521120000_TicketTypeRaffleBundle` | Satflux + **BTCPay Raffle** plugin |

Zmeny v tomto zozname zapisuj do `CHANGELOG-FORK.md` (sekcia príslušnej verzie).

---

## Vetvy (model)

```
upstream/main  ──merge──►  integrate/upstream-YYYY-MM  ──►  main  ──►  tag + .btcpay
                                ▲
feature/* (krátkožijúce) ───────┘
```

| Vetva | Stav / poznámka (máj 2026) |
|-------|----------------------------|
| `main` | **1.3.6.4** — purchase, offline tickets, všetky event stavy, raffle bundle + reflection fixes |
| `feature/greenfield-purchase-api` | Zlúčené do `main` |
| `feature/return-disabled-events` | Zlúčené (predkom greenfield) |
| `feature/event-raffle-bundle` | Zlúčené do `main` |

---

## Čo urobiť teraz (pred prvým mesačným cyklom)

Skontroluj checklist a odškrtni, keď je hotové:

- [x] Commitnúť `feature/event-raffle-bundle` (migrácia `20260520120000_EventRaffleBundle`, validator, API, hosted service)
- [x] Merge do `main` (`feature/event-raffle-bundle` obsahuje greenfield + disabled events)
- [ ] Vetva `integrate/upstream-2026-06`: `git fetch upstream` + merge `upstream/main` (autor ~**1.3.61**), vyriešiť konflikty v owned súboroch
- [x] Verzia fork **1.3.6.4** v `.csproj`
- [x] Aktualizovať `CHANGELOG-FORK.md`
- [ ] Build `.btcpay` z `main`, nasadiť na BTCPay, otestovať Satflux + WordPress offline
- [ ] V [satflux/docs/SATOSHI_TICKETS.md](../../../satflux/docs/SATOSHI_TICKETS.md) doplniť **min. verziu** pluginu

---

## Mesačný postup (napr. 20. každého mesiaca)

### 1. Stav

```bash
cd BTCPayServerPluginsTChukwuleta   # alebo váš clone webiumsk fork
git fetch upstream origin
git rev-list --left-right --count origin/main...upstream/main
```

Pozri upstream verziu:

```bash
git show upstream/main:Plugins/BTCPayServer.Plugins.SatoshiTickets/BTCPayServer.Plugins.SatoshiTickets.csproj | grep Version
```

### 2. Integračná vetva

```bash
git checkout main
git pull origin main
git checkout -b integrate/upstream-$(date +%Y-%m)
git merge upstream/main
# Konflikty: priorita fork v owned regions (tabuľka vyššie)
dotnet build -c Release Plugins/BTCPayServer.Plugins.SatoshiTickets/BTCPayServer.Plugins.SatoshiTickets.csproj
```

### 3. Merge do main a release

```bash
git checkout main
git merge integrate/upstream-YYYY-MM
# Bump <Version> v .csproj, CHANGELOG-FORK.md
git tag satoshi-tickets-vX.Y.Z-webium   # podľa vášho semver
# build-plugin.sh / build-plugin.ps1 → .btcpay
```

### 4. Smoke test

- [ ] Satflux: zoznam eventov (aj disabled), create/update event, ticket types
- [ ] WordPress: `create-tickets-offline` alebo purchase flow
- [ ] Ak nasadený Raffle: event s bundle → zaplatená objednávka → raffle lístky + e-mail
- [ ] Satflux `docker compose exec php php artisan test --filter=Ticket` (ak meníte proxy)

### 5. Kompatibilita (zápis do CHANGELOG / satflux doc)

| Komponent | Min. verzia (doplň po release) |
|-----------|--------------------------------|
| Satoshi Tickets (fork) | |
| BTCPay Raffle plugin | |
| Satflux | |

---

## Verziovanie

- Fork verzia **≥** upstream po merge (aby bolo jasné, že build obsahuje autorove opravy + naše featury).
- Voliteľne v `.csproj`: `<Product>Satoshi Tickets (Webium)</Product>` — v BTCPay UI rozlišiteľné od upstream.
- **Nepoužívať** upstream číslo verzie bez kontroly — autor môže mať iné API (napr. bez offline endpointov).

---

## Hotfix: `column BundledRaffleId does not exist`

Ak log ukazuje `42703: column e.BundledRaffleId does not exist`, plugin sa **vypne** a API vracia 404/502.

**Príčina:** nový kód (1.3.6+) bez EF migrácie `20260520120000_EventRaffleBundle` v DB (často chýbal súbor `*.Designer.cs`).

**Rýchly SQL fix** (PostgreSQL, potom re-enable plugin v BTCPay):

```sql
ALTER TABLE "BTCPayServer.Plugins.SatoshiTickets"."Events"
  ADD COLUMN IF NOT EXISTS "BundledRaffleId" uuid NULL,
  ADD COLUMN IF NOT EXISTS "BundledRaffleTicketsPerAdmission" integer NOT NULL DEFAULT 0;

INSERT INTO "BTCPayServer.Plugins.SatoshiTickets"."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260520120000_EventRaffleBundle', '8.0.11')
ON CONFLICT ("MigrationId") DO NOTHING;
```

**Trvalé:** nasaď build **≥ 1.3.6.2** (s `EventRaffleBundle.Designer.cs`), reštart BTCPay. Pre bundle validáciu **≥ 1.3.6.4**.

---

## Hotfix: `NullReferenceException` v `ValidateBundledRaffleAsync` (riadok ~89)

Pri vytváraní eventu s raffle bundlom plugin spadne, BTCPay ho **vypne** (`Skipping disabled plugin BTCPayServer.Plugins.SatoshiTickets`).

**Príčina (1.3.6.0–1.3.6.2):** reflection čítal `Result` z `Task` (non-generic) namiesto z `Task<(bool, string?)>` → `Result` je `null`.

**Fix:** nasaď **≥ 1.3.6.3**, v BTCPay **Account → Plugins** znova povoli Satoshi Tickets (alebo nahraď `.btcpay` a reštart). Raffle musí bežať **≥ 1.3.1.0**. Pre „Invalid raffle validation response“ použij **≥ 1.3.6.4**.

---

## Hotfix: `Invalid raffle validation response` pri `bundledRaffleId`

**Príčina (1.3.6.3):** reflection čítal `Item1`/`Item2` cez `GetProperty`, ale `ValueTuple` ich má ako **polia** → vždy 422 s touto správou (aj pri platnom raffle).

**Fix:** nasaď **≥ 1.3.6.4**.

---

## Čo nerobiť

- Produkcia s upstream `.btcpay` pri Satflux / WordPress integrácii.
- Veľké featury priamo na `integrate/*` vetve — najprv `feature/*`, potom merge do `main`, potom upstream merge.
- Event raffle bundle cez Satflux webhook — fulfillment ostáva v `SimpleTicketSalesHostedService` (BTCPay proces).

---

## Súvisiace projekty

- **Satflux:** proxy + UI — [satflux/docs/SATOSHI_TICKETS.md](../../../satflux/docs/SATOSHI_TICKETS.md)
- **BTCPay Raffle:** bundle alokácia — `BTCPayServerPlugins/Plugins/BTCPayServer.Plugins.BTCPayRaffle`
- Plán z Cursor: `.cursor/plans/satoshi_tickets_fork_cd72a5ee.plan.md` (ak existuje v workspace)

---

## História rozhodnutia

- Hybrid Satflux-only webhook pre bundle odmietnutý (krehkosť, duplicita).
- Companion plugin namiesto jedného forku zatiaľ nie — príliš veľa duplicity oproti hosted service v Satoshi Tickets.
- Upstream PR možný len pre **generické** zmeny (napr. `includeInactive`), nie blocker pre náš release.
