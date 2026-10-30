# SEPA isolation integrated with BTCPay 2.4.7

The owner authorized combining prepared SEPA isolation `8afed1a` with reviewed compatibility `ed250c2`, preparing a local installable candidate and publishing the owned provider branch. This preserves both source histories and does not authorize a GitHub Release, merge, installation, bank operation or deployment.

## Versions and consumer contract

Exact host: BTCPay 2.4.7 `2868deedd9d911b715fec7306ea9cada36d86bff`. Repaired SEPA plugin: **0.8.2.0**, minimum host **2.4.7**, confirmation contract **v2**. EF Design stays at 10.0.14. The earlier 0.8.1.1 compatibility-only archive is preserved and does not contain this repair. Other maintained plugin identities and compatibility behavior from `ed250c2` remain unchanged.

Companion Satflux source is `fix/sepa-confirmation-isolation-247` at `5eb7b1e0`, based on merged compatibility `4ff5c32a`. New consumer plus old plugin refuses automatic reporting without v2 capability. Old reports to the new plugin cannot bypass missing verified-origin or receiving-account evidence. Install the repaired provider first, verify actual settings capability, then deploy the reviewed consumer.

Automatic confirmation binds the route store and original invoice beneficiary/account, recognized verified bank source, observed positive credit, currency and reference. Invalid evidence cannot consume the dedup identity; corrected trusted evidence can retry. Preserve the original invoice account after settings changes. NOP recovery uses retained notifications and bounded same-provider pagination, not notification polling time as transaction-creation identity. Incomplete processing does not advance the recovery checkpoint.

The independent security reviewer found an inherited medium issue where nonpositive NOP amounts could pass a high amount tolerance. The automatic evidence path now rejects them before dedup/recording; three PostgreSQL regression cases cover zero/negative NOP parsing, no reservation or recorder call, corrected positive same-event retry and unchanged explicit manual zero-due behavior. Positive-credit tolerance is unchanged.

## Completed source and runtime qualification

All eight maintained plugins and all eight test projects build against the exact clean host with existing SDK 10.0.403 and .NET/ASP.NET runtime 10.0.14. Host build and guard regression pass. Full provider execution passes **907 tests, zero failures and zero skips** across eight projects on disposable PostgreSQL 17. CI-matching Node 24 passes all **13** Tickets client cases. Independent QA separately executes **165 SEPA tests**, zero failures/skips, and verifies the new negative-credit cases. SEPA retains two pre-existing obsolete Razor API warnings; no warning/audit threshold or dependency was weakened.

Full companion consumer checks pass 2,253 PHP tests / 10,706 assertions, 1,094 frontend tests, static/style/API/locale/build gates, unchanged 48-warning lint and 120-error typecheck baselines. An initial native Node 22 runner failed before Tickets cases; the successful authoritative run uses the existing CI-matching Node 24 container. Initial consumer memory/worker failures and corrected runs are recorded separately in its integration evidence.

[Security](qualification/sepa-247/security.md), [independent source/runtime QA](qualification/sepa-247/qa-source-and-runtime.md), [reviewed source manifest](qualification/sepa-247/reviewed-source.json) and [machine-readable test totals](qualification/sepa-247/test-summary.json) distinguish source acceptance and executed runtime checks. The security source manifest binds 22 provider and 26 companion consumer files (SHA-256 `9d227c2ad3a9ed433f60e344d61f6131b13954fc2dabb6855ebc2d9d095f6942`). Final archive bytes and a packaged-DLL regression are later artifact gates; fresh GitHub CI must match the published head.

## Preserved operational boundaries

The plugin reservation transaction is not atomic with BTCPay payment persistence. Keep stable-reference retry/reconciliation and matched plugin-database/Data Protection-key backups. Existing anonymous checkout-manual confirmation stays merchant opt-in/default-off and is not verified bank evidence. Native MTA origin attestation is an operator configuration prerequisite, not a property of Mailgun delivery authentication. Unknown or ambiguous bank proof remains unconfirmed; no universal bank-coverage or latency guarantee is claimed.

Raffle width/history and core account/store inventory gates from compatibility still apply. The retained exact-host checkout's temporary metadata was restored into durable private storage without upstream source modification or guard bypass; old generated checkout contents remain privately preserved. No global trust/credentials, production services, live wallets, merchant data or real funds were used. Existing local archives are not overwritten by this source integration.
