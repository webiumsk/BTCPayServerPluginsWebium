# SatfluxTickets 2.2.2: native BTCPay time zones

## Problem and authority

Tickets 2.2.1 injected `Europe/Bratislava` into its own picker and performed a throwing
lookup during static initialization. The BTCPay 2.4.5 native picker instead lists
`TimeZoneInfo.GetSystemTimeZones()`. A zone accepted as an operating-system alias
need not appear in that catalog. Catalog absence could strand edits; unavailable
alias lookup could also prevent the entire service from initializing.

The plugin now returns the same native catalog and resolves submitted identifiers
through `BTCPayServer.TimeZones.TryGet`, storing the returned canonical `zone.Id`.
Host-defined abbreviations and case handling follow BTCPay. No plugin alias is added
to the picker. The catalog is host-specific; the tested host pin remains
`5d0745cae6be5d8210459e38813f317673aa97b8` (BTCPay 2.4.5).

## Existing events and upgrades

IANA's [backward links](https://data.iana.org/time-zones/tzdb-2025b/backward) define
`Europe/Bratislava` as a link to `Europe/Prague`. Legacy stored Bratislava metadata is
presented as Prague only when that canonical identifier is available on the native
host. API responses and editable fields use the supported identifier. Read handling
does not silently mutate tracked entities. Unsupported non-null venue metadata is
reported explicitly rather than rendered using an invented venue offset. Ticket
emails retain delivery and show a clearly labeled UTC date plus the localized
missing/unsupported venue-zone warning when that metadata cannot be resolved.

The additive `20261025130000_NormalizeLegacyEventTimeZone` migration changes only
exact Bratislava metadata to Prague, gated on native Prague support. Start/end UTC
instants, reminders, orders and tickets remain unchanged. The previous migration is
immutable. Downgrade does not rewrite Prague rows because original Prague metadata
cannot be distinguished from normalized rows; 2.2.1 already understands Prague.

The native editor retains the raw `OriginalTimeZone` concurrency baseline separately
from the canonical editable field. A form loaded before a migration or concurrent
schedule change must still fail its stale-schedule guard. Metadata-only edits retain
the existing precise UTC instants; local wall-time conversion is performed only
when the corresponding schedule field actually changes.

No Satflux or WooCommerce consumer change is needed: their picker and physical-event
validation already depend on the provider catalog. The provider must return an
editable native identifier for legacy events as well as stop advertising the alias.

## Scope and evidence

This follow-up is version 2.2.2 and builds on merged PR #43. The original 2.2.1 source
manifest remains historical evidence for commit `5ba8244`; it is not a manifest of
this corrected version. OpenAPI descriptions now distinguish creation, updates and
response metadata; the timezone operation no longer repeats a server-URL parameter.
The separate hosted-checkout cancellation review item is outside this correction.

Source validation uses synthetic events and a disposable PostgreSQL database, exact
host verification, .NET regressions, actual client JavaScript and native-editor
browser checks. No production migration, deployment or binary publication is part
of source validation. Independent read-only security review accepted the bounded change after finding
and closing an unknown-zone email-delivery regression. Existing localization covers
EN/SK/ES/DE/CS, including explicit warnings. Fresh measured QA results follow below.

## Verified validation

Independent QA on the corrected source passed 200 .NET tests with zero failures or
skips: 178 non-database tests and 22 actual PostgreSQL tests. These include 45 pure
timezone cases and seven timezone database cases, plus ordering and sales database
regressions. The actual template/message builder runs in all five supported locales.
Native catalog equality, exact UTC precision, raw stale tokens, legacy API reads and
metadata-only upgrades with dependent records are covered.

The standalone Node 24 suite passes all 13 cases. Actual Chrome checks pass 22 native
editor assertions and 13 ticket-type ordering assertions. Runtime/client files were
unchanged between the browser run and final snapshot. The exact pinned host builds
with zero warnings/errors; the plugin has the same 11 compiler warnings as the
recovered baseline and zero errors; the fresh test build has zero warnings/errors.
No global tool or dependency-version change was required. Disposable database/network
fixtures are removed after validation; unrelated stacks are untouched.

Independent security review accepted the final runtime change and documentation.
The existing 2.2.1 recovery manifest, old migration/designer and schema snapshot remain
unchanged. Whitespace and OpenAPI/version consistency checks pass. Source-level tests
do not establish an installed server version or remove inherited host dependency
advisories and separate release/deployment gates.
