# Changelog

## 0.4.0 - unreleased

- Greenfield API for external control panels (satflux) under
  `/api/v1/stores/{storeId}/plugins/sepa-instant-qr`: settings GET/PUT,
  eKasa certificate upload (JSON, base64/PEM) and removal, backend test,
  pending/review payment-request listing and manual confirmation.
  Store-scoped API key with the `btcpay.store.canmodifystoresettings`
  permission; responses never contain certificate material or secrets.
- NOP certificate handling extracted into a shared SepaCertificateService
  used by both the settings UI and the API (identical validation:
  private-key presence, UTC validity window, eKasa subject).


## 0.3.0 - unreleased

- PAY by square QR variant for the SK profile (`SkQrVariant` store
  setting: `payme` default | `bysquare`). Payload per SBA "PAY by square
  specifications" 1.2.0: raw LZMA1 + CRC32 + base32hex, UTF-8 diacritics
  preserved, NOP reference carried as originator's reference information.
  PayMe remains the recommended variant with NOP auto-confirmation.


## 0.2.0 - unreleased

- NOP backend (Slovak state instant-payment notifications, project
  KVERKOM): `nop-mqtt` (per-store mTLS MQTT subscription, QoS 1 dedup,
  exponential-backoff reconnect, 2-hour REST catch-up) and `nop-rest`
  (NOP Lite polling fallback). eKasa cash-register certificate upload
  (PEM pair or PKCS#12, encrypted at rest; VATSK/POKLADNICA identity
  parsed from the subject), INT/PROD environment toggle, live-status
  test button. Payment references issued by NOP
  `generateNewTransactionId` with graceful local fallback.
  `dataIntegrityHash` verification per the SBA Standard for Push Payment
  Notification - mismatches route to manual review, never auto-settle.
- Aggregator note: GoCardless Bank Account Data closed to new signups
  (July 2025); an aggregator backend stays deferred until an operator
  contract with a successor (e.g. Enable Banking) exists.

## 0.1.0 - unreleased

- Initial version: `SEPA_INSTANT` payment method for EUR invoices with a
  checkout QR tab (SK PayMe /m/, CZ SPD, EU EPC QR), per-store settings
  (country profile, IBAN with mod-97 validation, beneficiary, tolerance),
  manual payment confirmation settling the invoice through BTCPay's normal
  lifecycle, pending/review tables, and the pluggable
  `IPaymentConfirmationSource` seam for the upcoming Fio, NOP (MQTT + REST)
  and GoCardless backends.
