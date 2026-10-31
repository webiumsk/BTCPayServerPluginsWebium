# PR45 second form/content review corrections

Source base c1912bfcdcbc7237b79d9ddc6f057cf519c1dcba. Review5720098606 identified
four valid follow-ups: wrong manifest link, invisible Raffle Description errors,
legacy bracketed ticket contact loss and plain-text newline loss. Current source
corrects all four and rephrases one unverified wording nitpick.

Raffle1.3.2.6 adds the standard Description validation message in the shared native
form. Tickets2.2.3.2 encodes bracketed email tokens before the unchanged host
sanitizer and applies whitespace preservation only to plain descriptions.
Original stored text, rich HTML policy, redirects, payments and SEPA0.8.2.0 remain
unchanged. Current compatibility rows match these versions; older archives and
qualification identities remain historical.

[Implementation](implementation.md) and [owned source](owned-source.json) bind
twelve files. [Independent security](bmail-security-provider-ui-review.md) accepts
its [matching snapshot](bmail-security-provider-ui-manifest.json), SHA-256
`5f532089f1f417931ba1d5ab0975c5df99ee5873d3e7eb1e5a4eade1f805d5e0`.
[Independent QA](qa.md) executes 13 actual compiled Razor/policy cases, zero
failures/skips, with [source/pre-commit binary hashes](qa-source-manifest.json)
and [test counters](test-summary.json). Those pre-commit build hashes are not
final shipped-DLL qualification. The two create/edit form cases and three ticket
partial cases use real host tag helpers and sanitizer behavior; browser computed
layout is not asserted. Exact host2868deed guard and diff checks pass.

Main alone owns commit/push, clean-revision packaging, new patch archive/DLL hashes
and final artifact tests. Final installable archives must be qualified separately
and delivered beside preserved older versions; do not overwrite prior candidates.
No merge, Release, production database, funds, installation or deployment occurred.
