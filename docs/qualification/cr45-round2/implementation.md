# PR45 second content/form review corrections

Author: cr45_round2_plugins. Repository: webiumsk/BTCPayServerPluginsWebium.
Base: c1912bfcdcbc7237b79d9ddc6f057cf519c1dcba. Frozen ownership is the 12 relative
paths and SHA-256 digests in owned-source.json. No Git publication performed.

## Corrected findings

- Fixed the qualification handoff's manifest link to the existing content-source.json.
- Added the native Description validation message to the shared Raffle form fields.
  Existing create/edit controllers and validation policy remain unchanged.
- Encoded bracketed email-like tokens in legacy ticket-type description text before
  the unchanged exact-host Safe.RawRichText sanitizer. Contact addresses and following
  instructions survive without permitting unsafe HTML. Originals are not rewritten.
- Applied pre-wrap only to the plain-text ticket-type wrapper. Existing rich HTML
  retains its prior layout and sanitizer behavior.
- Rephrased the unverified documentation wording nitpick using plain English.
- Updated current source versions/matrix and release notes: Raffle 1.3.2.6 and
  Satflux Tickets 2.2.3.2. SEPA 0.8.2.0 and all historical qualification manifests,
  archived versions and external host source are unchanged.

## Executed evidence

Existing SDK 10.0.403 container / .NET10 runtime, exact host
2868deedd9d911b715fec7306ea9cada36d86bff; host guard passed before and after builds.
No dependency install, bank/wallet/API traffic or production database was used.
The scope does not require PostgreSQL: synthetic MVC services use ephemeral Data
Protection and an empty file provider; rich-text policy is the exact host factory.

1. Tickets: dotnet test Plugins/BTCPayServer.Plugins.SatfluxTickets.Tests/BTCPayServer.Plugins.SatfluxTickets.Tests.csproj -c Release --no-restore -m:1 --filter FullyQualifiedName~TicketTypeRichTextRenderingTests
   - 3 passed, zero failures/skips. Tests execute the actual compiled Razor partial.
   - Plain bracketed contact plus following text/newline/indent survives; formatted
     contacts remain formatted; unsafe attributes/script/URLs/iframe policies remain
     sanitized. Plain and rich wrappers are distinguished in actual rendered markup.
2. Raffle: dotnet test Plugins/BTCPayServer.Plugins.BTCPayRaffle.Tests/BTCPayServer.Plugins.BTCPayRaffle.Tests.csproj -c Release --no-restore -m:1 -p:BuildProjectReferences=false --filter 'FullyQualifiedName~HostRichTextPolicyTests|FullyQualifiedName~RaffleFormValidationRenderingTests'
   - 10 passed, zero failures/skips. Unchanged host/plugin binaries were built by the
     earlier complete project build; only the final test setup needed recompilation.
   - Actual compiled shared form renders ModelState Description errors for both
     create/edit models. Existing media, unsafe legacy content, iframe sandbox,
     sanitized-length and unchanged-storage regressions pass.
3. git diff --check passes. Existing EF1002/nullable warnings were not suppressed.

Total focused evidence: 13 passed, zero failures/skips. No full-provider, installed
host, final archive or browser-computed-layout qualification is claimed here.
The dedicated CSS rule preserves whitespace; compiled Razor tests verify its
plain-only class and retained line breaks, not a browser layout measurement.

An initial Tickets rendered regression caught adjacent @plainClass being emitted
literally by Razor. Explicit interpolation fixed the markup; the complete final
three-case suite passed without weakening assertions or the sanitizer. The first
Raffle two-case run passed; the final ten-case run also includes explicit ephemeral
Data Protection setup and all existing host-rich-text cases.

## Boundaries and next gates

No changes to payment, SEPA matching, database schema, redirects, host source or
source guard. No commit/push/merge/tag/package publication/deployment. Main owns
independent security/QA acceptance, final commit, packaging and archive/DLL hashes.
Preserve delivered older archive bytes and publish these patch identities separately.
Graphify was queried from the primary graph and returned unrelated Satflux nodes;
the provider has no graph. Targeted reads were used without install/rebuild.
