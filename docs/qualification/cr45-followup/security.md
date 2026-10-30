# Independent security review: provider PR45 follow-up

Reviewer: `sepa247_security` (agent identifier), independent of the SEPA/content/guard authors.
Reviewed provider base: `e373478` plus the frozen 22-file source/config/test/changelog manifest. Companion consumer is `4b607bb2`; all 26 files from the prior consumer security manifest were independently byte-compared and remain unchanged. Shared publication documents are reviewed for attribution separately from this source manifest.

Disposition: source accepted for the bounded follow-up after the mitigations below. No unresolved blocking source finding remains. Final package/assembly identity, complete runtime qualification and exact-head CI remain coordinator/QA delivery gates. This is not approval for merge, release, installation or deployment.

Frozen manifest SHA-256: `8dffe4933205490a313e98f748c3fb8dc6725773893c12ce8741e6e917896615`.

## Confirmed host-source qualification gaps, now closed

Severity: Medium, affecting the claim that packaging uses the exact unmodified host source. These are qualification bypasses, not evidence that a delivered archive was actually contaminated.

The initial guard excluded Git-ignored files, so ignored response/config files could affect a build. Case-sensitive filename matching missed NuGet variants. Independent review additionally demonstrated that an extensionless untracked directory symlink is reported by Git only as the link name while actual MSBuild source discovery includes its C# children. A blanket exemption for any nested bin/obj directory also admitted source selected by MSBuild. Finally, an extension list omitted Razor component source: actual Web SDK input resolution selected an ignored `.razor` file accepted by the earlier guard.

Final mitigation in `scripts/verify-btcpay-host.sh`: scan all untracked entries, including ignored entries, with NUL-safe paths and fail-closed Git commands. Reject every entry outside genuine bin/obj output roots directly under tracked project directories, without relying on filename extensions. An output root must be an actual directory rather than a symlink. This rejects unknown resource/static/config files, hidden source links and collapsed nested repositories too. It does not delete or rewrite any host input; temporary notes belong outside the pinned packaging host.

Independent verification: 14 disposable assertions pass, covering clean/genuine-output acceptance and rejection of ignored response files, lowercase NuGet config, Razor/resource/unknown input, runtime JSON, a newline filename, ordinary/ignored source links, arbitrary nested bin/obj source and a nested repository. The committed `bash scripts/tests/verify-btcpay-host.sh` regression passes, and native `bash scripts/verify-btcpay-host.sh` passes against exact `2868deedd9d911b715fec7306ea9cada36d86bff`.

The earlier native MSBuild experiments performed input evaluation only, not package restore or compilation. They are historical one-off evidence retained in the reviewer's disposable workspace, not repository commands. The committed guard regression is the durable reproduction entry point. These checks are a source-tree qualification rule, not hermetic SDK/environment/generated-output attestation.

## SEPA recovery changes

The matcher/recorder now separates operator-review prerequisites from retryable uncertainty. Missing original invoice, invoice-store mismatch and missing SEPA handler produce explicit typed outcomes. The matcher restores the previous dedup value, records ManualReview evidence and returns without marking the request confirmed. A newly reserved bank identity is therefore not retained for a permanently unprocessable invoice, and later FIO movements are not trapped behind it. Existing recovery evidence is not erased.

The enum's default is RetryableFailure. Unknown enum values, transient false outcomes and exceptions retain rollback/throw behavior. AddPayment response loss retains same-reference retry/reconciliation; the patch does not turn unavailable or unknown recording into paid status. Actual invoice ownership is still checked before the recorder reaches payment dependencies. The prior automatic positive-credit, verified-origin and original-account checks remain intact; explicit manual semantics remain separate.

MQTT catch-up now handles per-notification failure, continues later retained items and withholds its success checkpoint when any item fails. Cancellation propagates. The caller can retry the retained set while the connection remains active; already recorded references remain idempotent. New per-item diagnostics omit raw payload and exception text. Upstream retention and non-atomic plugin/BTCPay persistence boundaries remain unchanged.

Reviewed regressions cover the three permanent recorder prerequisites, later FIO processing, unknown outcomes, actual missing-handler guard, mixed good/bad/good catch-up with retry and cancellation, plus the earlier nonpositive-credit and original-store/account cases. This reviewer inspected those tests but did not execute stateful PostgreSQL tests or parallel builds.

## Raffle and Tickets content changes

`RaffleDescriptionHtml.SanitizeForStorage` validates the final sanitized length against 8,000 characters, after any policy expansion and before persistence. All three write service paths use it. UI create/edit maps the description exception to its field, while API rejection stays before saving. Legacy rendering uses the separate sanitizer without silently truncating/rejecting existing long records. Scoped service contexts do not save partial name/pricing changes when length validation throws.

Native Tickets authoring permits a bad legacy redirect only when it exactly equals the current event's stored value. It removes only the new redirect-rule error; other binding/validation errors remain blocking, including the direct and prefixed field keys. Creation or changing to another invalid destination remains rejected. Existing purchase guards still reject an invalid effective redirect before a new order/invoice; preserving a historical field does not authorize navigation to it.

Public ticket-type descriptions render through the actual compiled `_TicketTypeDescription` Razor partial and the host's `Safe.RawRichText`, matching the existing event-description policy. No raw unsanitized output was introduced. The host sanitizer, rather than default sanitizer settings or a hand-built HTML string, remains the executable policy for formatting, event/script removal and iframe restrictions. Native editor textarea sanitization remains unchanged.

The suggested API-validation weakening is unnecessary: exact-host `Hosting/Startup.cs` configures InvalidModelStateResponseFactory to return UnprocessableEntityObjectResult with Greenfield validation errors. Existing API attributes correctly produce 422 and remain in place.

Raffle 1.3.2.5 and Tickets 2.2.3.1 distinguish these new delivery candidates from previously delivered versions. The private pre-review SEPA 0.8.2.0 candidate and earlier runtime/source evidence remain historical and cannot qualify the changed final bytes by filename alone.

## Verification, documentation and remaining gates

This review independently executed the 14 guard probes, committed guard regression, real exact-host guard and source diff checks. Source/auth/data-flow analysis covers the frozen manifest, automatic/manual distinction and the unchanged companion consumer. Content author reports 32 Raffle and 226 Tickets tests passed on SDK 10.0.403/runtime 10.0.14; those counts are author evidence, not runtime execution by this reviewer. Full SEPA/provider and packaged-DLL checks remain separately attributable to the coordinator/QA.

Current compatibility/integration documents correctly distinguish current source versions from historical archive identities, preserve 0.8.1.1 as compatibility-only SEPA history and require fresh manifests and archive checks for new delivery. Guard wording describes output exceptions directly under tracked projects; arbitrary nested bin/obj does not qualify. No deployed-version, bank-origin adapter readiness, real-money settlement, global cross-source event identity or crash-atomic recording guarantee is inferred.

Graphify was queried from the existing primary graph and returned unrelated heuristic nodes; targeted reads followed. No graph installation/rebuild occurred. The reviewer made no source/index/shared-state writes, installed no dependencies/browser/tools, and used no production environment, merchant credentials, live bank/API, Docker service or funds. Private report/probe files are the reviewer's only writes. No Git publication or deployment was performed by this reviewer.
