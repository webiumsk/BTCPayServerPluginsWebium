# CR45 Raffle/Tickets implementation handoff

Author: `cr45_content`, provider specialist. Repository: Webium plugins, branch `fix/btcpay-247-compatibility`, base `e3734788`. Main owns Git publication, shared state and final artifacts. No source commits, pushes, external comments, package delivery or deployment by this author.

## Findings and behavior

- **4429153144, valid:** the exact host adds image markup and can expand an 8,000-character input beyond the storage/form bound. `RaffleDescriptionHtml.SanitizeForStorage` validates the sanitized result; all three service write paths use it. Native create/edit catches this argument validation against the Description field rather than returning a database or unhandled error. Read sanitization remains separate and continues to safely render overlong legacy content without mutation. Regression cases assert actual host sanitizer expansion, no create/update persistence, exact-boundary idempotency and safe legacy reads. Raffle identity is now **1.3.2.5**, retaining the earlier width repair and host minimum.
- **4429153148, valid:** native event updates permit an invalid legacy redirect only when byte-identical to the current stored value. Only that field's own redirect-attribute error is removed; additional redirect binding errors still block. New invalid values, creates and new checkout guards remain unchanged. Cases cover direct/prefixed model-state keys, remaining-error preservation and changed invalid destinations; a PostgreSQL case performs an actual native title-only controller update after attribute validation, retaining the old redirect.
- **4429153160, false positive on exact host:** `BTCPayServer/Hosting/Startup.cs` lines 174-180 configures `InvalidModelStateResponseFactory` to `UnprocessableEntityObjectResult(context.ModelState.ToGreenfieldValidationError())`. Therefore automatic attribute failures already produce Greenfield **422**, not framework-default 400. API attributes are retained; no speculative API behavior change or source-mirroring test was added. This is an exact-host source trace, not an HTTP-pipeline execution claim.
- **4429153166, valid:** the public event summary now renders ticket-type content through a dedicated compiled Razor partial using the same `Safe.RawRichText` policy as event content. An actual compiled-partial test executes the host sanitizer and confirms visible formatting/media while stripping legacy scripts, event attributes, data attributes and unsafe iframe permissions. Tickets identity is now **2.2.3.1**; the standalone identity, original migrations and optional Raffle integration remain intact.

Owned changelogs and the current Tickets README version/behavior were updated. Historical fork-maintenance identity/version prose was not rewritten; actual standalone project identity is preserved.

## Executed validation

Using the coordinator-provided existing SDK 10.0.403 / runtime 10.0.14 containers and an explicitly isolated PostgreSQL namespace:

- Both changed plugins and both matching test projects build successfully, sequentially with `BuildProjectReferences=false`.
- Raffle suite: **32 passed, 0 failed, 0 skipped**.
- Tickets suite: **226 passed, 0 failed, 0 skipped**.
- `git diff --check`: passes.

The initial Tickets run passed 225 cases and failed only the new direct-Razor fixture: manual page activation lacked its strongly typed ViewData property. Setting that fixture property resolved the issue; no production-code correction or weakened assertion was used. The final full 226-case run passes. Raffle's three existing EF1002 controlled-schema warnings and existing Tickets obsolete/nullable warnings remain; no warning threshold or dependency was changed.

The parent granted a single exclusive shared compilation/database window; no competing build/test was started by this author. The window has been released. No JS source changed, so no duplicate client run was needed; the parent previously executed all 13 cases in the CI-matching Node 24 image.

## Provenance and limits

`source-manifest.json` captures all owned changed/new files; `test-summary.json` records final TRX counters. The exact host is coordinator-restored and guarded; provider Graphify is absent, so the earlier primary query and targeted reads were used without graph installation/rebuild. No real store, bank, wallet, secret or production database was used.

These are build/unit/database/compiled-partial results. They do not establish full installed-host HTTP behavior, native-browser form submission, final package byte identity or production rollout. Main and independent reviewers own those final source/artifact gates. No shared coordination state or other worker's source was modified.
