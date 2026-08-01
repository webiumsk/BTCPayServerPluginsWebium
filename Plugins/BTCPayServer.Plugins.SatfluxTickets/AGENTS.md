# Agent: Satflux Tickets (Webium fork)

Si agent pre **BTCPay Server plugin Satflux Tickets** v repozitári **webiumsk/BTCPayServerPluginsWebium** (lokálne `BTCPayServerPlugins/Plugins/BTCPayServer.Plugins.SatfluxTickets`). Toto **nie je** čistý upstream od autora TChukwuleta — ide o **udržiavaný produkčný fork**.

Pred každou väčšou zmenou si prečítaj:
- `FORK_MAINTENANCE.md` — vetvy, merge upstream, mesačný checklist (ďalšia kontrola: **2026-06-20**)
- `CHANGELOG-FORK.md` — čo je fork-only vs merged z upstream

---

## Repozitáre

| Remote | Účel |
|--------|------|
| `origin` | webiumsk/BTCPayServerPluginsWebium — deploy, tagy, `.btcpay` |
| `upstream` | TChukwuleta — len `git merge`, nie produkčný build |

**Deployateľná vetva:** `main`. Nové featury: `feature/*` → review → merge do `main`.

**Nikdy:** nasadiť upstream `.btcpay` na inštancie so Satflux alebo WordPress — chýbajú fork endpointy.

---

## Fork-only (chrániť pri merge upstream)

| Funkcia | Kľúčové súbory |
|---------|----------------|
| Offline / manuálne vstupenky | `Controllers/GreenfieldSatfluxTicketsController.cs` — `create-tickets-offline` |
| Greenfield purchase | ten istý controller — `CreatePurchase` |
| Všetky stavy eventov v API | `Controllers/GreenfieldSatfluxTicketsEventsController.cs` — `GET /events` bez filtra Active; Satflux query `includeInactive` / `include_inactive` je kompatibilný, parameter sa nevyžaduje |
| Ticket type raffle bundle | `Data/Entities/TicketType.cs`, `Controllers/GreenfieldSatfluxTicketTypesController.cs`, `Controllers/UITicketTypeController.cs`, `Views/UITicketType/ViewTicketType.cshtml`, `Services/SimpleTicketSalesHostedService.cs`, `Services/EventRaffleBundleRequestValidator.cs`, migrácia `20260521120000_TicketTypeRaffleBundle`, `Services/Integration/RaffleEventBundleClientResolver.cs`, `Services/Integration/RaffleListClientResolver.cs` (voliteľný runtime bridge — **žiadny** compile-time ref na Raffle) |

Po zmene fork featury aktualizuj `CHANGELOG-FORK.md` a bump `<Version>` v `BTCPayServer.Plugins.SatfluxTickets.csproj`.

---

## Architektúra pluginu

- **Greenfield API:** `Controllers/GreenfieldSatfluxTickets*.cs` — route prefix `~/api/v1/stores/{storeId}/satflux-tickets/`
- **UI (Razor):** `Controllers/UITicketSales*.cs`, `Views/`
- **Platby / lístky:** `Services/SimpleTicketSalesHostedService.cs` — reaguje na `InvoiceEvent`, tag `Ticket_Sales_{txnId}`
- **DB:** schema `BTCPayServer.Plugins.SatfluxTickets`, `SimpleTicketSalesDbContextFactory`, migrácie v `Data/Migrations/`
- **Swagger:** `Resources/swagger.json` — pri API zmenách aktualizuj

Build (z koreňa pluginu alebo monorepa):

```bash
dotnet build -c Release Plugins/BTCPayServer.Plugins.SatfluxTickets/BTCPayServer.Plugins.SatfluxTickets.csproj
```

---

## Spotrebitelia mimo pluginu

| Projekt | Úloha |
|---------|--------|
| **Satflux** | Proxy + UI — `satflux/app/Http/Controllers/TicketController.php`, `app/Services/BtcPay/TicketService.php`, `resources/js/pages/stores/TicketsShow.vue`. Dok: `satflux/docs/SATOSHI_TICKETS.md` |
| **WordPress** | `create-tickets-offline`, `CreatePurchase` |
| **BTCPay Raffle** | `IRaffleEventBundleService` — bundle po `InvoiceSettled`, idempotencia `eventbundle:{orderId}:{email}` |

**Raffle bundle:** fulfillment **iba** v `SimpleTicketSalesHostedService` po úspešnom settle — **nie** cez Satflux webhook.

**Bundle pravidlá:** `počet_vstupeniek_s_rovnakým_emailom × bundledRaffleTicketsPerAdmission`; rôzne e-maily na objednávke = samostatná alokácia + e-mail.

---

## Pravidlá implementácie

1. **Minimálny diff** — nerefaktoruj upstream kód bez dôvodu.
2. **Konvencie BTCPay** — Greenfield validation errors, `CreateAPIError`, `EnsureStoreOwnership` cez store context.
3. **Migrácie** — nové stĺpce cez EF migráciu + `SimpleTicketSalesDbContextModelSnapshot.cs`.
4. **Voliteľná závislosť na Raffle** — `IRaffleEventBundleClient` sa rieši cez reflection len ak je Raffle plugin nainštalovaný; bez Raffle musí plugin načítať (žiadny `ProjectReference` na Raffle DLL). Validácia pri bundle poliach musí zlyhať zrozumiteľne, ak Raffle chýba.
5. **Commity** — len na explicitnú žiadosť používateľa.
6. Pri úprave API, ktoré volá Satflux, skontroluj camelCase payload (`bundledRaffleId`, `startDate`, …) a prípadne test v `satflux/tests/Feature/Ticket*.php`.

---

## Čo nerobiť

- Presúvať ticket/raffle fulfillment do Satflux Laravel webhookov.
- Mazat alebo prepisovať fork endpointy pri merge upstream bez kontroly owned regions.
- Pridávať `btcpay_store_id` do frontendu Satflux (satflux používa lokálne UUID store).

---

## Otvorené úlohy (stav apríl 2026 — over v gite)

- [x] Commit + merge `feature/event-raffle-bundle` do `main`
- [x] Merge `feature/greenfield-purchase-api` (vrátane disabled events) do `main`
- [ ] `integrate/upstream-2026-06` → merge TChukwuleta ~1.3.61 (mesačný cyklus)
- [ ] Release `.btcpay` nasadený; doplniť min. verzie v `satflux/docs/SATOSHI_TICKETS.md`

Keď používateľ pýta „mesačný merge“ alebo „čo robiť s Tickets pluginom“, otvor `FORK_MAINTENANCE.md` a postupuj podľa checklistu.
