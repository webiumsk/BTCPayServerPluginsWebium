# Independent QA: PR45 second content/form corrections

Accepted for the bounded 12-file source patch and compiled Razor regression scope. No blocking defect found. Repository `webiumsk/BTCPayServerPluginsWebium`, base `c1912bfcdcbc7237b79d9ddc6f057cf519c1dcba` plus the frozen 12-file diff. Independent reviewer: `sepa247_qa`. All 12 SHA-256 values were independently recalculated before and after tests and match the author freeze; `qa-source-manifest.json` also captures the two test DLL and two tested plugin DLL hashes. Source, host, shared state and Git were read-only; no install/restore/build/publication occurred.

## Executed checks

Existing SDK **10.0.403** Docker environment, `--network none`, exact pinned BTCPay **2.4.7** source `2868deedd9d911b715fec7306ea9cada36d86bff`. The existing QA wrapper was copied only into the private reviewer fixture, switched to no network, and given a reviewer-results mount. These filters use synthetic MVC services/ephemeral Data Protection and do not require a database. No PostgreSQL access was available or used.

- `dotnet test Plugins/BTCPayServer.Plugins.SatfluxTickets.Tests/BTCPayServer.Plugins.SatfluxTickets.Tests.csproj -c Release --no-build --no-restore -m:1 --filter FullyQualifiedName~TicketTypeRichTextRenderingTests --logger 'trx;LogFileName=qa-round2-tickets.trx' --results-directory /qa/results`: **3 passed, 0 failed, 0 skipped**.
- `dotnet test Plugins/BTCPayServer.Plugins.BTCPayRaffle.Tests/BTCPayServer.Plugins.BTCPayRaffle.Tests.csproj -c Release --no-build --no-restore -m:1 --filter 'FullyQualifiedName~HostRichTextPolicyTests|FullyQualifiedName~RaffleFormValidationRenderingTests' --logger 'trx;LogFileName=qa-round2-raffle.trx' --results-directory /qa/results`: **10 passed, 0 failed, 0 skipped**.
- `bash scripts/verify-btcpay-host.sh`: pass; exact pin and strict host-source cleanliness verified independently.
- `git diff --check`: pass.

Total **13 passed**, zero skips. Raw TRX counters are recorded in `test-summary.json`. These are independent executions of existing compiled binaries, not inferred results from the author's run.

## Verified behavior and scope

Tickets' actual compiled public Razor partial preserves bracketed contact addresses and following instructions/newlines/indentation. HTML remains subject to the exact host sanitizer: formatting/media survive while unsafe event handlers, script, unsafe URLs and excessive iframe capabilities do not. Plain descriptions acquire the dedicated whitespace-preserving class; rich HTML does not. The new helper only encodes bracketed email-like tokens and classifies plain markup; it never grants raw-HTML trust or rewrites stored originals. The new CSS rule is limited to that plain class.

Raffle's actual compiled shared fields render ModelState Description errors with the native validation span for both create and edit models. Existing host sanitizer/media/legacy/sanitized-length/storage regressions remain passing. Controller validation policy is unchanged. Version bumps are Raffle **1.3.2.6** and Tickets **2.2.3.2**, minimum host remains **2.4.7**; standalone identity and optional Raffle bridge remain unchanged. The qualification document link now targets existing content-source.json. No SEPA, redirect, payment, schema or fulfillment code is in the 12-file patch.

Graphify primary query had returned unrelated Satflux nodes and the provider has no graph; targeted source reads were used without installation/rebuild. Existing Tickets agent instructions contain historical upstream-merge wording; current fork maintenance clarifies manual porting. No upstream merge was attempted.

## Limits

This proves compiled partial output and unchanged sanitizer integration, not browser-computed whitespace/layout, full installed-host form submission, payment/bundle execution or an installed runtime. No live bank, wallet, funds, production data or external service was used. Final committed-source archive metadata, DLL byte identity and tests against shipped DLLs remain a separate gate after main's commit/build. No final archive currently qualifies through this source report; previous delivered archives remain historical evidence.
