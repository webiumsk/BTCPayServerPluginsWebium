# Independent recovery validation

Scope: owned Webium plugins, `restore/satflux-tickets-2.2.1`, based on `3728ae6`.
Source and historical fingerprints are described in `satflux-tickets-2.2.1.md`.
This report concerns source recovery and Git publication, not deployment approval.

## Fresh checks

Independent reviewer `tickets_recovery_qa` verified the full provenance bridge and
froze the 156-file plugin/test tree before compilation. The final snapshot and
historical 2.2.1 aggregate remained unchanged after all tests. The exact host
`5d0745cae6be5d8210459e38813f317673aa97b8` remained clean throughout.

| Check | Fresh result |
| --- | --- |
| Restore | Eight projects restored offline from existing cache, without dependency-version changes |
| Exact host Release build | Zero warnings/errors |
| Restored plugin Release build | Zero errors; 11 existing compiler warnings |
| Recovered test project build | Zero warnings/errors |
| .NET suite | **190 passed, zero failed/skipped** |
| Actual PostgreSQL subset | 21 of those cases: ordering 8, venue time zones 6, public sales/recovery 7 |
| Pure client tests | **13 passed** under Node 24 |
| Native ordering UI | **13 assertions passed** using existing Chrome/Playwright |
| Host guard | Exact-pin check and its regression script passed |
| Source integrity | All 156 source-manifest entries and before/after snapshots match |

The PostgreSQL fixture used its own project, tmpfs storage, an internal network and
no published ports. The SDK shared only that fixture's network namespace so the
original test guard still required localhost and a `satflux_tickets_ordering_`
database. No test was pointed at production, a real wallet or an email provider.

The existing SDK container was .NET SDK 10.0.103/runtime 10.0.3. Its missing
NETStandard.Library.Ref 2.1.0 pack was reused read-only from an existing SDK 8 image;
no global SDK/tool installation or package-version change occurred. An initial
offline restore failed on that missing pack, then passed after the isolated reuse.
An initial Node invocation mixed a standalone Playwright script into `node --test`;
correct framework-specific runs passed without changing product source.

The historical 26 compiled-HTML/Open Graph browser checks were not rerun as a new
matrix. Fresh evidence includes the actual Razor build, 18 metadata helper cases
within the .NET suite and native ordering browser checks. Historical results remain
clearly attributed. A rebuilt DLL is not claimed byte-identical to the historical
or currently installed binary: compiler, paths and Git context can affect output.

## Independent security and publication review

Reviewer `tickets_recovery_security`, independent of extraction authors, accepted
the recovered source/provenance and scoped publication. Review covered merchant
authorization, store/event/session/transaction binding, native antiforgery, pricing
and recipient authority, lookup-only unknown-invoice recovery, ordering locks/CAS,
public metadata privacy and output encoding. No WooCommerce journal or newer
unpublished provider test candidate was mixed into this baseline.

The private transformation runner was reviewed and corrected before final recovery:
an I/O-capable relative path was replaced with a pure path, closing a boundary escape.
Independent negative probes rejected traversal, process import and object introspection;
auxiliary history inputs stayed memory-only. No escaped write was attempted. This
private recovery tool and raw historical records are not part of the published source.

The publication scan found no credential material; a credential-bearing URL appears
only as a deliberate synthetic rejection test. One original extra EOF blank line in
`TicketSalesInvoiceRecoveryTests.cs` is retained to preserve the historical source
fingerprint. Default `git diff --check` reports that formatting item; all other
whitespace checks pass with only `blank-at-eof` excluded for this verification.
No global whitespace configuration changed.

## Remaining boundaries

CI checks the published head separately, including the existing maintained-plugin
matrix, all recovered .NET cases using a dedicated database and pure client tests.
See the PR for exact run/head results. Inherited inventory-reservation limits and
pinned-host NuGet advisories remain explicit release gates. Installed-server binary
comparison, external delivery and production migration are outside this recovery.
No merge, release, installation or production deployment has been performed.
