# BTCPay 2.4.7 compatibility candidate

The compatibility branch targets exact host commit `2868deedd9d911b715fec7306ea9cada36d86bff` (tag `v2.4.7`). The host gitlink, source guard and CI workflow are updated together. Guard checks reject a different revision, tracked source changes, all ignored or untracked host entries outside normal bin/obj outputs directly beneath tracked project directories. This includes response/config/resource/Razor inputs and source links, without an extension allowlist; arbitrary nested bin/obj folders are not exempt. Keep developer-only notes/configuration outside this pinned dependency when qualifying a build. No upstream host source change is required.

## Candidate versions

| Plugin | Version | Minimum host |
| --- | --- | --- |
| CashuMelt | 1.4.0.9 | 2.4.7 |
| LNAddressConnect | 1.0.4.1 | 2.4.7 |
| Satflux Tickets | 2.2.3.2 | 2.4.7 |
| BTCPay Raffle | 1.3.2.6 | 2.4.7 |
| SEPA Instant QR | 0.8.2.0 | 2.4.7 |
| Blitz | 1.0.2 | 2.4.7 |
| Flash | 1.0.2 | 2.4.7 |
| EmailConfirm | 1.0.2 | 2.4.7 |

These are locally built installable candidates, not package releases or production installations. Older 2.4.5 artifacts remain independent controls; new minimum-host declarations intentionally reject older hosts. The original `ed250c2` compatibility archive identities were qualified separately. These rows identify current source candidates; final archive hashes and packaged-DLL evidence must identify the delivered versions.

SEPA 0.8.2.0 integrates the prepared `8afed1a` store/account-isolation repair and confirmation contract v2 with exact 2.4.7 compatibility. The earlier delivered 0.8.1.1 archive remains compatibility-only historical evidence and must not be selected as the repaired provider. See [the integration record](SEPA_247_INTEGRATION.md) for source/runtime and final artifact qualification boundaries.

## Changed behavior

- Tickets public descriptions and Raffle public/admin descriptions use the host bounded merchant-rich-text output policy, including legacy stored content without a resave. Raffle writes use the same policy. No stored originals are bulk rewritten.
- Ticket-type descriptions preserve bracketed contact addresses and plain-text whitespace. The shared native Raffle create/edit fields display description validation errors.
- Native event, ticket-type and Raffle description editors opt into the host textarea helper, which sanitizes content before Summernote initialization. Supported images/video/inline formatting remain; forms, data attributes, style blocks, unsafe URLs/CSS and script attributes remain disallowed. Iframes retain host sandbox/trusted-origin distinctions.
- Optional event and purchase redirects accept only absolute HTTP(S), at most 2,048 characters. Omitted/empty fields retain their existing API meaning. Invalid new saved values fail validation; legacy defaults are not silently rewritten. New purchases validate their effective redirect before persisting an order. Existing attempted public checkouts remain lookup-only recovery even if event settings later change.
- Direct dependency downgrades revealed by the new host are aligned: QRCoder 1.8.1, MailKit/MimeKit test dependencies 4.18.2, Lightning.Common test dependency 1.7.5, NBXplorer.Client test dependency 5.0.12 and applicable Microsoft packages 10.0.14. Those dependency alignments do not alter payment/recovery/fulfillment algorithms or historical migration identities. Separate SEPA recovery/isolation changes are recorded in the integration document; the bounded legacy width alignment is described below.

## Qualification boundaries

Use `bash scripts/tests/verify-btcpay-host.sh`, `bash scripts/verify-btcpay-host.sh` and `.github/workflows/btcpay-247.yml` for reproducible build/test entry points. Tests require disposable synthetic PostgreSQL targets. Missing optional PostgreSQL fixtures must not be represented as tested cases.

The rich-text regressions instantiate the exact host's internal DI sanitizer factory through reflection rather than using bare HtmlSanitizer defaults. Native-editor tests run the actual target-host textarea helper. Database cases cover redirect rejection before writes and preservation of unrelated legacy values.

A provider worktree nested under a parent JavaScript package with `type=module` changes Node's interpretation of the existing CommonJS test imports. Use a byte-identical temporary provider layout outside that parent package scope for those tests; do not mistake package-scope inheritance for a payment or host regression. Normal CI runs at the provider repository root.

Build/test success is distinct from installed package identity, startup/migration/restart, native browser rendering, outstanding payment recovery and matched host/plugin/database/Data Protection-key restore. Independent source security/QA and final immutable-artifact evidence remain required before any release/deployment decision. No package release, merge or deployment is implied by source publication.

## Historical compatibility source verification (ed250c2, 2026-10-30)

At the original `ed250c2` compatibility checkpoint, all eight maintained plugins built against the exact unmodified host and the following suites passed with disposable PostgreSQL, without skipped cases. These historical counts and original archive identities do not establish the later integrated source or current patch-version archive identity:

| Suite | Passed | Skipped |
| --- | ---: | ---: |
| Raffle | 30 | 0 |
| Blitz | 85 | 0 |
| CashuMelt | 165 | 0 |
| EmailConfirm | 4 | 0 |
| Flash | 96 | 0 |
| LNAddressConnect | 142 | 0 |
| Satflux Tickets | 220 | 0 |
| SEPA Instant QR (compatibility baseline) | 111 | 0 |
| Total | 853 | 0 |

The source guard and its injected-source/build-input regression cases pass. The unchanged Tickets client suite passes 13/13 using byte-identical isolated files outside the ancestor ESM scope. Host and plugin builds retain existing SourceLink, nullable/obsolete and controlled schema-SQL analyzer warnings; no warning policy was weakened.

Initial builds used SDK 10.0.400 and shared runtime 10.0.11. The complete plugin test set was then executed on verified Microsoft.NETCore.App and Microsoft.AspNetCore.App 10.0.14 in official SDK image 10.0.403 (digest `sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b`). Final Raffle passes 30/30 including eight real PostgreSQL width cases; independent QA adds 72 assertions over six schema shapes. Native SDK build evidence is not relabelled as an official production-image build.

Normal package installation, all eight loader versions/assembly hashes and restart were verified on the patched runtime. Actual native/public browser qualification passes 93 assertions, the checked-in editor regression passes 30, and the same-fixture post-restart editor/data checks pass 30/3. The ticket-type editor's missing Summernote assets were found by real browser execution and fixed before the final archives. Actual core HTTP contracts pass 28 checks. Separate real EF migration/restore and synthetic key-recovery qualification passes 54 assertions.

Independent QA and security accept the bounded source and final Raffle artifact. Satflux source is pushed at `7bb1677dd51a971e4337b6e7318c1f5d97dd1a16`; no provider source push, package release or deployment is claimed. The owner-requested local archives and checksums are under this registered repository's primary `packaged/` directory; `BTCPAY_2_4_7_CANDIDATES.md` records exact versions, hashes and prerequisites.

## Known legacy Raffle column widths

The unchanged raw fallback historically left Description as varchar(2000) and InvoiceId as varchar(50) on some older databases, although current accepted values can be longer. The compatibility helper now conditionally widens only these known, plain-varchar columns: Description below 8,000 characters becomes the raw fresh-schema text type; InvoiceId below 100 becomes varchar(100). Already adequate bounded columns, text, unbounded/wider varchar, unknown types and custom domains are left unchanged. Custom domains/constraints remain operator-specific compatibility gates rather than being removed.

The helper checks shape before taking a table lock, rechecks under the lock and explicitly retains the catalog collation. Defaults, nullability and existing raffle/ticket/drawing records remain intact. No history rows are stamped, no historical migration file or runner is changed, and no new pricing/backfill behavior is introduced. The existing EF duplicate-column warning/fallback remains observable and is not represented as resolved.

A required width ALTER takes an ACCESS EXCLUSIVE table lock. Repeated startups skip the new width ALTER once aligned; existing schema-creation/add-column/backfill statements retain their prior behavior. This is not a zero-downtime guarantee. Qualify the first alignment on representative data under an appropriate maintenance/restore plan.
