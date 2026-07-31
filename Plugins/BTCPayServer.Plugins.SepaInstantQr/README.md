# SEPA Instant QR - BTCPay Server plugin

Adds **"SEPA Instant QR"** as a payment method next to Bitcoin/Lightning:
a EUR invoice shows a QR code that the customer scans with their banking
app, paying by instant SEPA credit transfer **directly to the merchant's own
bank account**. Designed as a free "terminal" satisfying the Slovak cashless
mandate (effective 1 May 2026) - QR-based instant transfers legally count as
cashless payment without a card terminal.

**The plugin never takes custody of funds.** SEPA payments settle on the
merchant's own account; Lightning stays self-custodial via BTCPay's native
capabilities.

## v0.1 scope

- Payment method `SEPA_INSTANT` (EUR invoices only; hidden otherwise).
- QR formats by store country profile:
  - **SK**: PayMe (SBA Payment Link Standard 2.0, type `/m/` dynamic QR,
    SCT Inst) - the same format the state NOP/"QR platby" initiative uses.
    The payment reference is NOP-shaped (`QR-` + 32 hex), so upgrading to
    automated NOP confirmation later changes nothing for the payer.
    Since v0.3 the SK profile can switch to **PAY by square** (SBA
    "PAY by square specifications" 1.2.0) - the long-established binary QR
    every Slovak banking app scans. Pick it when your customers' apps do
    not open PayMe links; note the payment reference then travels as
    originator's reference information, which not every bank propagates as
    the SEPA end-to-end id - PayMe stays the recommended variant with NOP
    auto-confirmation.
  - **CZ**: QR Platba (SPD 1.0), reference in `X-VS`, `PT:IP` instant flag.
    Note: `CC:EUR` makes CZ banks route it as a SEPA/foreign payment - UX
    varies per bank.
  - **EU generic**: EPC QR ("girocode", EPC069-12 v3.1, version 002).
- Confirmation backend: **Manual** - the merchant sees the transfer in their
  banking app and presses "Mark as paid" on the store's SEPA page
  (permission: modify store settings). Settlement runs through BTCPay's
  normal invoice lifecycle: webhooks fire, the POS shows paid.
- Store settings: country profile, IBAN (mod-97 validated), beneficiary,
  optional BIC/message, amount tolerance, per-store enable toggle.
- Pending/review tables: automated backends (from later versions) never
  auto-settle a mismatched payment - amount/currency mismatches land in
  "Needs review" for the merchant to decide.

## Automatic confirmation: NOP (Slovakia, v0.2)

The **NOP backend** turns confirmations fully automatic for Slovak
merchants: the payer's bank pushes an instant-payment notification to the
state NOP system (project KVERKOM) and the plugin receives it within
seconds over MQTT - the invoice settles while the customer is still at the
counter. Works for **every Slovak bank joined to the QR-payments scheme**
(Tatra banka and SLSP as of July 2026 per info-qrplatby.sk; coverage grows
with the cashless-payment law), independent of the merchant's bank having
an API.

- `NOP - instant notifications` (recommended): persistent mTLS MQTT
  subscription per store, QoS 1 with deduplication, automatic reconnect
  with exponential backoff and a 2-hour catch-up over REST after downtime.
- `NOP - polling fallback`: the same notifications fetched via the NOP Lite
  REST API every minute - for deployments where a persistent MQTT
  connection is impractical.
- Every notification's `dataIntegrityHash` (SHA-256 per the SBA Standard
  for Push Payment Notification) is verified; tampered or mismatched
  notifications go to "Needs review" and never auto-settle.
- Payment references are issued by NOP (`generateNewTransactionId`). If NOP
  is unreachable at invoice creation, a compatible local reference is used
  and confirmation degrades to Manual for that invoice.

Aggregator status (all-EU PSD2 account access): GoCardless Bank Account
Data stopped accepting new signups in July 2025; successor services
(e.g. Enable Banking) require a paid operator contract for multi-merchant
production use. See `docs/research/qr-formats.md` / `docs/research/nop.md`
and the aggregator notes in `docs/research/btcpay-plugin-patterns.md`.
An aggregator backend can plug into the existing confirmation seam once
that contract exists.

## Merchant setup

1. Store settings → **SEPA Instant QR** (wallets nav).
2. Pick the country profile, enter IBAN + beneficiary name, save, enable.
3. Create a EUR invoice - the SEPA tab appears next to Bitcoin/Lightning.
4. Confirmation:
   - **Manual** (default, any bank): confirm under "Awaiting payment" once
     the transfer shows in your banking app.
   - **NOP** (Slovakia): upload your eKasa cash-register certificate and
     switch the backend - see below.

### Slovakia - NOP setup

1. Ask your bank to mark the business account as **"notifikačný"**
   (notification-enabled) for the QR platby service. Banks supporting it as
   of July 2026: Tatra banka, SLSP - check <https://www.info-qrplatby.sk/>
   for the current list (coverage grows with the cashless-payment law).
2. Get your **eKasa cash-register certificate** - the same identity your
   cash register uses (authentication package from the eKasa zone of the
   Financial Administration portal; VRP certificates download directly,
   ORP certificates come from your cash-register registration/vendor).
   Upload it (PEM pair or .p12/.pfx) in the plugin settings; the identity
   (VATSK / POKLADNICA) is read from the certificate automatically.
3. Choose environment: **INT** for testing against
   `api-erp-i.kverkom.sk` / `mqtt-i.kverkom.sk` (open, no whitelisting) or
   **PROD** (production operation launched by FR SR in March 2026 per
   info-qrplatby.sk).
4. Press **Test confirmation backend** - it performs a live mTLS status
   call and reports your certificate identity.
5. Payer-side support: bank apps implementing PayMe open the payment
   pre-filled; others fall back through the central payme.sk page.

If NOP is down when an invoice is created, the invoice still works - the
QR renders with a locally generated reference and the merchant confirms
manually for that one payment.

### Counter-top POS setup

Use BTCPay's built-in **Point of Sale** app on a tablet/phone stand with the
store's checkout: the cashier types the amount, the customer picks the
Bitcoin/Lightning tab or the SEPA Instant QR tab and scans. No extra
hardware.

## Legal boundary

This plugin is **software only**: it renders payment instructions (QR codes)
for transfers between the customer and the merchant's own bank account and
records confirmations. It does not receive, hold, forward or exchange funds,
does not execute refunds (refund = the merchant sends a manual transfer from
their bank), and is not a payment service. Merchants should verify their own
regulatory and fiscal obligations - in particular, eKasa receipt duties
remain with the merchant's cash register (no fiscal integration in v1).

## Development

```bash
dotnet build Plugins/BTCPayServer.Plugins.SepaInstantQr -c Debug
dotnet test  Plugins/BTCPayServer.Plugins.SepaInstantQr.Tests
./Plugins/BTCPayServer.Plugins.SepaInstantQr/build-plugin.sh   # packs .btcpay
```

- Local run: point `DEBUG_PLUGINS` in
  `submodules/btcpayserver/BTCPayServer/appsettings.dev.json` at
  `bin/Debug/net10.0/BTCPayServer.Plugins.SepaInstantQr.dll`.
- EF migrations: `EfMigrations=true dotnet ef migrations add <Name> -o Data/Migrations --context SepaDbContext`
  (the env toggle copies BTCPayServer assemblies for the design-time load).
- Research notes with primary-source citations: `docs/research/nop.md`,
  `docs/research/qr-formats.md`, `docs/research/btcpay-plugin-patterns.md`.

## Manual QR test instructions (v0.1 acceptance)

1. Configure an SK store profile with a real IBAN, create a 0.10 EUR invoice.
2. Decode the QR (any QR reader): the payload must be a
   `https://payme.sk/2/m/PME?...` link whose `PI` matches the reference shown
   at checkout (golden tests cover the exact format).
3. Scan with at least one Slovak banking app (e.g. Tatra banka, George) -
   the payment form must open pre-filled with amount + IBAN + reference.
4. Pay (or not), press "Mark as paid" in store settings → the invoice
   settles, webhooks deliver, the POS shows the paid screen; the Lightning
   tab keeps working next to the SEPA tab.
