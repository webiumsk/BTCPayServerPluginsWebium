# Changelog

## 0.1.0 - unreleased

- Initial version: `SEPA_INSTANT` payment method for EUR invoices with a
  checkout QR tab (SK PayMe /m/, CZ SPD, EU EPC QR), per-store settings
  (country profile, IBAN with mod-97 validation, beneficiary, tolerance),
  manual payment confirmation settling the invoice through BTCPay's normal
  lifecycle, pending/review tables, and the pluggable
  `IPaymentConfirmationSource` seam for the upcoming Fio, NOP (MQTT + REST)
  and GoCardless backends.
