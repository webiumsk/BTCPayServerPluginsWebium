# Settlement safety (1.4.0.0)

Install CashuMelt 1.4.0.0 before the Satflux wallet-safety update. Compatible host: BTCPay Server 2.4.5. No new production environment variables are required.

## Before upgrading

Back up the plugin database and BTCPay's ASP.NET Data Protection key ring together. The database schema adds durable issuance/sweep intents and immutable per-payment mint/payout settings. New bearer assets are encrypted; existing plaintext proofs and change secrets are protected at startup. Losing the key ring loses access to these assets. Stop older plugin instances before upgrading: mixed-version processing is unsupported. Rolling back requires recovery-aware export/decryption; the migration deliberately refuses a destructive downgrade.

CashuMelt temporarily holds spendable ecash on **BTCPay**, including routing change and failed payouts. Satflux receives neither these proofs nor wallet private keys. Encryption reduces database-read exposure; it does not make Cashu settlement non-custodial. The configured mint is a trusted issuer and Lightning payer. A payout to Linky may result in a second mint conversion at the destination and incurs fees on both sides.

## What constitutes settlement

Only a PAID response for the original melt quote, bound to the stored Lightning invoice and amount, confirms payout. A supplied preimage must hash to that invoice's payment hash. An absent preimage relies on the mint's exact PAID quote; it is not an independent cryptographic proof. Spent proofs, a missing quote, TokenAlreadySpent, transport errors, or another quote's receipt cannot mark an invoice paid.

Issuance intent is committed before minting, including original blinding material and public keys. Recovery uses NUT-09 with those exact outputs; a new set of secrets is never generated for an already-issued quote. New payments require a sat mint with NUT-09 support. Checkout IDs are opaque; actual mint quote identifiers are encrypted and no longer exposed in new public checkout/poll links.

A lost melt response retains the original payout invoice, quote, proofs, and change blinding. UNPAID retries with confirmed unspent proofs resend the same intent. Unknown results wait; expired intents require manual recovery. Successful melts recover change before clearing funding assets, then durably enter MELT_COMPLETE before BTCPay accounting. Accounting retries retain the same payment ID. PostgreSQL advisory locks exclude duplicate quote processing across instances and block settings changes while settlement runs.

## Merchant recovery

Legacy in-flight records lack a trustworthy original settings snapshot. They enter MANUAL_REVIEW instead of adopting the current mint/address. On the CashuMelt settings page, open **Cashu recovery** and explicitly confirm the original quote ID, mint URL, and payout address. The supplied mint must return the original funding invoice/amount/unit. This is a merchant assertion about the original payout address, not historical proof. If the original issuance intent is missing and the quote was already issued, recovery requires the mint operator; retry cannot invent blinding secrets.

Small change below the automatic sweep minimum is still an asset. **Download available change tokens**, privately import the file's cashuA tokens into your own Cashu wallet, then select **Verify imported change**. Exported proofs stay excluded from automatic sweeps and keep the wallet-change guard blocked until NUT-07 confirms every exported proof is SPENT. Download can be repeated after an interrupted response. Never share these bearer token files or send them through Satflux.

MANUAL_REVIEW, FAILED with assets, PENDING funding quotes, outstanding change, and retained blinding all block a mint/address/unit/enabled change or deletion. Fee-only changes affect future payment snapshots. GET `/api/v1/stores/{storeId}/plugins/cashumelt/safety` requires store-settings permission and reports `safeToChangeWallet`, `unresolvedPayments`, and `unresolvedChange`. Missing/unreachable endpoints must fail closed in integrators. Expired funding quotes become EXPIRED_UNPAID only after BTCPay invoice monitoring has ended and the mint reports no funding and no recovery assets exist.

## Validation and remaining limits

Offline mock mint tests cover issuance before-commit failure, lost mint responses, interrupted proof commits, wrong receipts, and original-payout retry. PostgreSQL tests exercise locks, schema baselining, legacy asset encryption, and settings guards. CI provisions a disposable PostgreSQL service using `CASHUMELT_TEST_CONNECTION`; this variable is test-only.

No real payment was made during these tests. Mint/operator behavior, Lightning routing availability, and merchant refunds still need controlled staging acceptance. A lost legacy issuance secret cannot be reconstructed. A mint that lies about PAID, refuses restore/change, or loses its records can require operator recovery. Automatic payout does not make Linky's currently deployed address a native LUD-21 address. Production rollout should start with a low-value staging payment and deliberate restart/lost-response scenarios.
