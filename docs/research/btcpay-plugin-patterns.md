# BTCPay plugin patterns for a custom payment method - research notes

Grounded in this monorepo (verified 2026-07-30). The reference implementation
for a plugin-provided payment method is **CashuMelt**
(`Plugins/BTCPayServer.Plugins.CashuMelt`); DB/migration/build patterns are
shared with BTCPayRaffle and SatoshiTickets. Official docs:
<https://docs.btcpayserver.org/Development/Plugins/>,
template <https://github.com/btcpayserver/btcpayserver-plugin-template>.

## Registration (Plugin.cs)

`CashuMeltPlugin : BaseBTCPayServerPlugin` (`Plugins/BTCPayServer.Plugins.CashuMelt/Plugin.cs`):

- `PaymentMethodId` static (`new("CASHU")`); dependency floor
  `new() { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }`.
- DB: `AddSingleton<{X}DbContextFactory>` + `AddDbContext<{X}DbContext>`
  (transient, `factory.ConfigureBuilder(o)`) + `AddStartupTask<PluginMigrationRunner>`.
- Payment method: `AddSingleton<IPaymentMethodHandler, ...>`,
  `AddSingleton<ICheckoutModelExtension, ...>`,
  `AddTransactionLinkProvider(pmid, ...)`, `AddDefaultPrettyName(pmid, ...)`.
- UI slots: `AddUIExtension("store-wallets-nav" | "checkout-payment" |
  "checkout-end" | "store-invoices-payments", "<PluginName>/<ViewName>")`
  → views live in `Views/Shared/<PluginName>/<ViewName>.cshtml`.
- Background work: `AddHostedService<...>` (CashuMeltReconciliationHostedService);
  HTTP: `AddHttpClient<TypedClient>()`.

## Payment method handler contract

`PaymentHandler/CashuMeltPaymentMethodHandler.cs`:

- `BeforeFetchingRates(PaymentMethodContext)`: set
  `context.Prompt.Currency` + `Divisibility`; setting `context.State = null`
  (or throwing `PaymentMethodUnavailableException` later) removes the method
  from the invoice - this is where "EUR invoices only + store configured"
  gating belongs.
- `ConfigurePrompt(PaymentMethodContext)`: fill `context.Prompt.Destination`,
  `context.Prompt.Details = JObject.FromObject(promptDetails, Serializer)`,
  add to `context.TrackedDestinations`, persist plugin-DB state.
- Companion types: `{X}PaymentMethodConfig` (stored per store via
  `store.SetPaymentMethodConfig(handler, config)` - see
  `PluginMigrationRunner.EnsurePaymentMethodRegisteredAsync` pattern),
  `{X}PromptDetails`, `{X}PaymentData` (serialized into BTCPay's payment
  blob).

## Settling an invoice from a plugin (the ManualConfirm core)

`Services/CashuMeltPaymentService.TryRecordPaymentInBtcPayAsync`
(`Plugins/BTCPayServer.Plugins.CashuMelt/Services/CashuMeltPaymentService.cs`,
"Records the CashuMelt payment in BTCPay"):

```csharp
var payment = new PaymentData {
    Id = <unique payment id>, InvoiceDataId = invoiceId,
    Currency = "...", Amount = <decimal in prompt currency>,
    Status = PaymentStatus.Settled, Created = DateTimeOffset.UtcNow };
payment.Set(invoiceEntity, handler, customPaymentData);
var paymentEntity = await _paymentService.AddPayment(payment, [searchTerm]);
// fallback: re-read invoice and find the payment if AddPayment returned null
var updated = await _invoiceRepository.GetInvoice(invoiceId);
_eventAggregator.Publish(new InvoiceEvent(updated, InvoiceEvent.ReceivedPayment) { Payment = paymentEntity });
```

`BTCPayServer.Services.Invoices.PaymentService.AddPayment` +
`InvoiceEvent.ReceivedPayment` is the complete integration point: BTCPay's
InvoiceWatcher transitions the invoice (processing/settled) and fires
webhooks; the POS checkout flips to paid. Because our prompt currency equals
the invoice currency (EUR), a settled payment covering the due amount
settles the invoice with rate 1:1.

## Checkout UI

BTCPay does NOT render a QR for plugin payment methods. CashuMelt's
`ICheckoutModelExtension` sets
`context.Model.CheckoutBodyComponentName = "CashuMeltCheckout"` and the
`checkout-end` UI extension (`Views/Shared/CashuMelt/CashuMeltCheckoutExtension.cshtml`)
registers a Vue component of that name which renders
`<qrcode :value="..."/>` using BTCPay's bundled
`~/vendor/vue-qrcode/vue-qrcode.min.js` and polls a plugin endpoint every
2 s. The prompt details travel to the model via
`context.Model.AdditionalData`. Copy this wholesale; the QR value is the
SEPA payload string. (Server-side QRCoder 1.7.0 exists in SatoshiTickets as
a fallback pattern, not needed here.)

## DB + migrations

- `Data/{X}DbContext` with `modelBuilder.HasDefaultSchema("<plugin schema>")`;
  `Data/{X}DbContextFactory : BaseDbContextFactory<{X}DbContext>` (+ a
  `DesignTimeDbContextFactory` for `dotnet ef`).
- `PluginMigrationRunner : IStartupTask` logs pending migrations and calls
  `Database.MigrateAsync()`; CashuMelt adds an idempotent raw-SQL
  SchemaCreator fallback and `EnsurePaymentMethodRegisteredAsync`.
- Never touch core tables; plugin schema only.

## Settings UI conventions

`UICashuMeltController`: `[Route("plugins/{storeId}/cashumelt")]`,
`[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes =
AuthenticationSchemes.Cookie)]`, `[AutoValidateAntiforgeryToken]` (see also
SatoshiTickets controllers), explicit ViewModels (no mass assignment),
`TempData.SetStatusMessageModel(...)`; nav entry via the
`store-wallets-nav` UI extension reading
`@inject IScopeProvider ScopeProvider` / `GetCurrentStoreId()`.

## Secrets at rest

BTCPay core protects store secrets with ASP.NET Data Protection:
`IDataProtectionProvider.CreateProtector("ConfigProtector")`
(`submodules/btcpayserver/BTCPayServer/Controllers/UIStoresController.cs`).
The plugin registers its own protector purpose (e.g. `"SepaInstantQr"`) for
backend credentials and uploaded certificates; secrets never logged, only a
`*_set` boolean exposed to views.

## csproj + packaging

- csproj: `Microsoft.NET.Sdk.Razor`, `net10.0`, `AddRazorSupportForMvc`,
  `GenerateEmbeddedFilesManifest`, `CopyLocalLockFileAssemblies`, plus the
  suppression block (`StaticWebAssetsEnabled=false`, `Private=false`,
  `ExcludeAssets=runtime;native;build;buildTransitive;contentFiles`) and the
  3-way conditional `ProjectReference` to BTCPayServer
  (`..\..\submodules\btcpayserver\...` → `..\..\..\btcpayserver\...` →
  `..\..\..\BTCPayServerPluginsKukks\submodules\btcpayserver\...`).
- Repo `global.json`: SDK 10.0.100, rollForward latestFeature.
- Build/pack: per-plugin `build-plugin.sh` → `dotnet publish -c Release` +
  `BTCPayServer.PluginPacker` → `packaged/<name>/<version>/<name>.btcpay`.
- Dev loop: `DEBUG_PLUGINS` in
  `submodules/btcpayserver/BTCPayServer/appsettings.dev.json` pointing at
  `bin/Debug/net10.0/<plugin>.dll` (see repo CLAUDE.md and
  BTCPayRaffle/README.md "Option B").
- Tests: xunit net10.0 projects (`BTCPayServer.Plugins.CashuMelt.Tests`,
  `...BTCPayRaffle.Tests` - EFCore.InMemory for DB-adjacent tests); added to
  `BTCPayServerPlugins.sln`.

## Confirmation-backend seam planned for this plugin

`IPaymentConfirmationSource` (manual | fio | nop-mqtt | nop-rest |
gocardless): backends only produce `ConfirmedPayment(reference, amount,
currency, raw, dedupKey)` records; a shared `SepaMatchingService` matches
(exact reference + amount + EUR) and hands to a single `SepaPaymentRecorder`
implementing the settle pattern above. Mismatches → MANUAL_REVIEW state,
never auto-settle.

> Historical note: the original phase plan listed Fio (poller) → NOP →
> GoCardless. Superseded by the operator's coverage-first decision - Fio
> was skipped, NOP (MQTT + NOP Lite REST) shipped as the universal instant
> SK path, and the aggregator phase is deferred (see below).

### Verified at the NOP phase (2026-07-30)

- MQTTnet: pinned **5.2.0.1603** (net10.0-compatible; MQTT 3.1.1 via
  `MqttClientOptionsBuilder.WithProtocolVersion(MqttProtocolVersion.V311)`,
  client certificates via `WithTlsOptions(o => o.WithClientCertificates(...))`,
  client from `MqttClientFactory`).
- Backend order re-decided by the operator: coverage over simplicity →
  Fio backend skipped entirely; NOP shipped as the universal instant SK
  path (see `nop.md`).

## Aggregators (all-EU PSD2 AIS) - status 2026-07-30

- **GoCardless Bank Account Data (ex-Nordigen) is closed to new signups
  since July 2025** - official notice at
  <https://bankaccountdata.gocardless.com/new-signups-disabled>; existing
  accounts keep working but community projects (e.g. Actual Budget,
  actualbudget/actual#5505) are migrating away. The free-tier era
  (50 connections, ~4 syncs/day/account, 90-day consent) is over for
  newcomers.
- **Enable Banking** (<https://enablebanking.com>) is the commonly named
  successor: sandbox free; "Restricted Production" free **only for
  accounts the account holder links themselves**; multi-merchant
  production use requires a **paid operator contract** (custom-quoted by
  AIS call volume, under their AISP licence or the customer's own).
- Other licensed aggregators (Tink, Salt Edge, Yapily, Powens) are
  commercial as well. Direct bank XS2A APIs are gated on being a regulated
  TPP: AISP registration/authorisation with the national competent
  authority (see the EBA payment-institutions register,
  <https://euclid.eba.europa.eu/register/>) plus eIDAS role certificates
  (QWAC/QSealC) for identification towards banks - exact obligations vary
  by jurisdiction and arrangement. Third-party cost estimates put QWACs at
  roughly EUR 3-8k/year (unverified market quote). Either way this is not
  viable for a merchant-installed plugin without an operator behind it.
- Conclusion: an aggregator confirmation backend stays **deferred** until
  the operator signs an aggregator contract; it will plug into the
  existing `IPaymentConfirmationSource` + `SepaPollingHostedService` seam.
  PSD2 unattended-refresh limits (~4/day/account) make it a
  delayed-confirmation (e-commerce) backend either way - never a POS one.
