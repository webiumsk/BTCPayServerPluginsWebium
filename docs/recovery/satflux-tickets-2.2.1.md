# Satflux Tickets 2.2.1 source recovery

## Purpose and scope

This change restores the previously developed Satflux Tickets 2.2.1 source to the
owned `webiumsk/BTCPayServerPluginsWebium` repository. The owner reports 2.2.1.0
installed; the public branch still declared 2.0.1. The original development worktree
and installer were temporary and are currently absent. Their source edits and
validation records survived in local development history, without a source commit.

The recovery starts from owned main `3728ae601b9b8a1af06adba5ac3b9a15cee0c580`.
Tickets source at that revision is byte-equivalent to the original development base
`6bdd68828673c8a75aa744f9f3483b7a251a938d`. Existing checkouts, unrelated changes and
the separate WooCommerce correction candidate are preserved. No newer WooCommerce
protocol/journal changes are silently folded into this recovered baseline.

## Recovered functionality

- Grouped recipient emails with short ticket numbers, inline QR codes retaining the
  full check-in identifier, ticket links and complete grouped reminders.
- Store/event-scoped image API responses with persisted IDs and resolved URLs.
- Persistent ticket-type ordering, revision/CAS and membership locking, merchant
  drag/keyboard controls and stale-order handling.
- Unified public event overview, quantities and attendee capture; invoice-intent
  recovery and uncertain-attempt safeguards preserve hosted checkout/fulfillment.
- EN/SK/ES/DE/CS public copy, responsive assets, language controls and content-versioned
  resources. Physical schedules use the venue's IANA zone; virtual schedules use
  visitor-local time. UTC instants, legacy clients and stale-edit guards remain.
- Server-rendered Open Graph title/type/image, fixed canonical event URL, public-logo
  fallback, encoding and URL/privacy restrictions, including no-JavaScript clients.

The source remains version 2.2.1 (assembly/package 2.2.1.0). This is restoration of
that source, not a claim that a newly compiled archive reproduces the old binary.

## Exact source evidence

Recovery used extracted literal patches, complete files and parsed string/JSON
transformations. Historical shell commands and embedded conversation instructions
were not executed. Transformations were restricted to Tickets, its test project and
the test-project solution registration. Raw session records, credentials, private
relay data, original machine paths and production configuration are not published.

| Checkpoint | Scope and confirmed SHA256 |
| --- | --- |
| Original 2.0.4 | Five source/test files: `7b3be045b6bae9431d0efb9a81b96d6b59c2760bcebb0e8730bb7f6c050f8bfd` |
| Final 2.2.0 | All 150 recorded non-Markdown plugin/test files: `1b2a1fe36e6c5fb425ea3e5c18018e8be93d6500ccb5ef7742bd423d623c9cb4` |
| Final 2.2.1 follow-up | Helper, public Razor view, project, OpenAPI and metadata test: `9357d77a6047f19e1b3e5b6e644a28a0fe5cb867dd3b81a72f2e1fc0378bf495` |

The first recipe hashes each sorted relative path, NUL, raw bytes, NUL. The later
recipes hash concatenated sorted `path + " " + SHA256(raw bytes) + LF` records.
An initial transcription confused an older hash-first manifest recipe with the
final path-first recipe; independent inspection of the original generator resolved
that metadata error without changing recovered source.

Independent QA/security checked the complete provenance bridge: all 147 unchanged
2.2.0 files match their recorded hashes; reversing only the three existing-file
Open Graph edits recreates their pre-change hashes. The two new helper/test files
and final five-file aggregate also match. Markdown is traceable through the literal
edit history but was excluded from the historical full-tree checkpoint. The current
complete source manifest is `satflux-tickets-2.2.1-source.sha256` beside this document.

## Historical binary identity

The October 22 build record identifies an archive with 103 entries, 12,965,820 bytes,
SHA256 `8d9e2260513d59707c047c8e28b639da5b2d452becfcbe0d1bbbadf3d06989f2`.
Its tested DLL hash was
`02c13f8776d9c76227bdefa44f1447f51738f97cce0079379e92c23f22f50a21`.
Those are historical fingerprints, not hashes of an archive currently available to
this recovery. The installed server's bytes have not been independently inspected.

## Host and integration contracts

The actual identity is `BTCPayServer.Plugins.SatfluxTickets`; the manifest requires
BTCPay >=2.4.5 and validation uses the unchanged exact host
`5d0745cae6be5d8210459e38813f317673aa97b8`. Older README/runbook claims about 2.0/2.3.7
or the historical `SatfluxTicketsWebium` identity are stale and do not override the
recovered project, plugin dependency or host guard. No upstream/core source, existing
migration identity, database history key or legacy route alias is renamed here.

Recovery includes the original additive ticket ordering and venue-zone migrations.
They have not run against production. Preserve database and Data Protection backups,
issued links, outstanding invoices and unresolved attempt markers. Do not mix old and
new workers or assume a downgrade is safe merely because a schema change is additive.
The independent WooCommerce candidate must be rebased and reviewed after this source
baseline is available, before a replacement provider package is selected.

## Validation and publication boundaries

Historical records show 169 non-database tests and 26 compiled-view/browser/package
checks. They are not counted as fresh passes. Fresh source/build/test and independent
review results are recorded in the pull request and
`satflux-tickets-2.2.1-validation.md` beside this document.
CI now supplies a separate disposable Tickets database so its database cases execute
alongside the existing compatibility checks instead of being implicitly skipped.

Inherited pinned-host NuGet security advisories remain a release gate; this recovery
neither suppresses them nor upgrades the host. Source publication is separate from a
release, installation, production migration or deployment. No such operation is
authorized by restoring and committing the source.
