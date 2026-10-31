# Independent PR45 round2 content/form security review

Reviewer: sepa247_security, independent of author cr45_round2_plugins. Date: 2026-10-31. Repository: webiumsk/BTCPayServerPluginsWebium. Base c1912bfcdcbc7237b79d9ddc6f057cf519c1dcba plus twelve-file uncommitted snapshot. Exact manifest: bmail-security-provider-ui-manifest.json, SHA-256 5f532089f1f417931ba1d5ab0975c5df99ee5873d3e7eb1e5a4eade1f805d5e0. All twelve hashes independently match the author's owned-source.json.

## Disposition

Accepted for the bounded source security gate. No verified blocking security finding in this UI-only patch. No SEPA/payment or redirect implementation was changed. This review does not qualify final archive bytes, installed-host HTTP, production deployment or a full plugin security audit.

## Boundaries verified

- SatfluxTickets/Services/TicketDescriptionHtml.cs preserves bracketed email-like tokens by HTML-encoding the entire match. Its comment correctly keeps the result untrusted. The public compiled _TicketTypeDescription.cshtml always sends that result through the unchanged exact-host Safe.RawRichText policy before rendering; no new direct Html.Raw/HtmlString bypass is introduced. Tokens containing special characters become text; unmatched markup still reaches the host sanitizer. The regexes use .NET NonBacktracking and fixed patterns, avoiding unbounded backtracking on legacy input.
- IsPlainText only controls a CSS class. Even a mistaken classification cannot grant HTML trust. The new sales-plain-description class adds white-space:pre-wrap only for the plain wrapper. Rich content retains the prior policy/layout; legacy originals are not rewritten. Contact preservation occurs before sanitization, so a bracketed address and following instructions can survive without admitting script, attributes or unsafe URLs.
- BTCPayRaffle/Views/UIRaffle/_RaffleFormFields.cshtml adds the ordinary MVC Description validation tag helper. Existing sanitized-length validation, write checks, authorization and native form flow remain intact. Rendering errors does not introduce raw error HTML. Both create/edit model variants use the shared compiled form.
- Version increments are isolated: Raffle1.3.2.6 and Tickets2.2.3.2. Source minimum host/dependencies and SEPA0.8.2.0 remain unchanged. Documentation correctly preserves historical qualification identities rather than relabeling older archives.

## Evidence and tests

Independent targeted source review of all twelve files, author-manifest comparison and git diff --check pass. Reviewed actual rendered-test code, including the compiled Razor partial with actual host sanitizer factory and create/edit validation tag helpers. Author reports Tickets3 plus Raffle10 tests passed, zero failures/skips, on SDK10.0.403/runtime14 and exact guarded host2868deedd9d911b715fec7306ea9cada36d86bff. This reviewer did not rerun shared builds or relabel those author counts as independent execution. Main/QA owns final runtime and package gates.

Regression expectations covered by the reviewed tests: bracketed plain contact and following whitespace remain visible; formatted contacts retain formatting; malicious scripts/attributes/unsafe URLs remain removed; iframe sandbox remains constrained; plain-only CSS class is absent from rich markup; both native Raffle forms display a Description ModelState error. Browser computed layout and final installed binaries are not asserted by these rendered-source tests.

## Dependencies and remaining gates

The exact host rich-text sanitizer remains a trusted dependency already qualified in the prior bounded review. No sanitizer policy/library change, new service, key, network, database migration, authorization, session, tenant, webhook, payment or entitlement path was added. Final build/package/DLL provenance and fresh CI remain main/QA gates. No source, host, Git, shared-state, production or external-application write was performed by this reviewer. Graphify was queried earlier against the primary graph with irrelevant results; this external worktree has no graph and none was installed/rebuilt.
