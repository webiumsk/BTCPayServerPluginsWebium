# NOP (Notifikátor okamžitých platieb) - research notes

Sources (primary, retrieved 2026-07-30):

- KVERKOM - NOP Lite - Integračný manuál v1.7 (29.1.2026):
  <https://www.financnasprava.sk/_img/pfsedit/Dokumenty_PFS/Elektronicke_sluzby/Verejne_dostupne_elektronicke_sluzby/QR_platby/2026/2026.01.30_NOP_Lite_Integracny.pdf>
- NOP Lite - NOP Services API v1.3 (8.12.2025):
  <https://www.financnasprava.sk/_img/pfsedit/Dokumenty_PFS/Elektronicke_sluzby/Verejne_dostupne_elektronicke_sluzby/QR_platby/2025/2025.12.08_NOP_services.pdf>
- SBA Standard for Push Payment Notification v1.1 (errata 2, 17.7.2025):
  <https://www.sbaonline.sk/wp-content/uploads/2025/07/API_PushPaymentNotification_1_1_errata2.pdf>
- Public info: <https://www.info-qrplatby.sk/>, diagnostics app <https://www.kdejemojaplatba.sk>

## Identity model (the key finding)

- ERP/cash-register clients ("pokladnica") authenticate with **the same X.509
  client certificate used for eKasa** (issued by FR SR). There is no separate
  software-partner identity for ERP clients - a plugin acts on behalf of the
  merchant using the merchant's own eKasa cash-register certificate.
- Certificate subject: `C=SK, OU=XXXXXXXXXXXXXXXX, CN=VATSK-XXXXXXXXXX POKLADNICA XXXXXXXXXXXXXXXX`.
  From CN the system derives the tax-subject id `VATSK-{DIČ}` (used even for
  non-VAT payers - it is DIČ-based, see FAQ) and the cash-register id
  `POKLADNICA-{code}` (ORP codes start `888`, VRP `999`; OAuth/OIDC planned
  for VRP). Both drive REST authorization and MQTT topic ACLs.
- Cert formats: PEM (.crt + .key) or PKCS#12 (.p12/.pfx). TLS 1.2+ (1.3
  recommended). Client CA chain: DigiCert Global Root G2 + GeoTrust TLS RSA
  CA G1.
- Onboarding (closed pilot, per manual): merchant requests via
  `kverkom.kasoveIS@financnasprava.sk` (Dohoda o spolupráci). INT environment
  has **no identity whitelisting**; PROD plans a whitelist by VATSK.
- **Adoption risk (open point):** many merchants' eKasa certificates live
  inside sealed ORP/CHDÚ devices and may not be exportable; VRP certificates
  are downloadable from the PFS portal. Practicality per merchant segment
  must be validated before the NOP phases ship.

## Environments

| Env | ERP REST | BANK REST | MQTT |
|---|---|---|---|
| INT | `https://api-erp-i.kverkom.sk` | `https://api-banka-i.kverkom.sk` | `mqtt-i.kverkom.sk:8883` |
| PROD | `https://api-erp.kverkom.sk` | `https://api-banka.kverkom.sk` | `mqtt.kverkom.sk:8883` |

PROD was marked "v príprave" (in preparation) as of manual v1.7 - re-verify
before enabling the PROD toggle. SNI must match; IP ranges limited to
SK/EU/USA; system timezone Europe/Bratislava; NTP drift < 200 ms required.
Public diagnostics REST (no mTLS): `https://kdejemojaplatba.kverkom.sk` /
`https://kdejemojaplatba-i.kverkom.sk`.

## REST API (ERP side, mTLS)

- `GET /api/v1/status` - health; returns `{timestamp, instance, build, version}`.
- `POST /v1/generateNewTransactionId` - body optional `{"comment": "..."}`
  (max 256 chars). Response: `{"id": "QR-<uuid4-hex-lowercase-no-dashes>",
  "created_at": "..."}` (the integration-manual example shows the field as
  `transaction_id` while the Services API doc names it `id` - handle both).
  The id is the **EndToEndId / Payment identification (PI)** for the payment
  link per the SBA Payment Link Standard.
- `GET /v1/getAllTransactions/{POKLADNICA-...}?date_from=YYYY-MM-DDTHH:mm:ss.sssZ` -
  list of notifications for a cash register. Notifications **expire 2 hours**
  after registration; `date_from` compares against the transaction's
  `created_at`.
- `GET /v1/getTransactionHistory/{transactionId}` - **public, no auth**, on
  the kdejemojaplatba host; diagnostic metadata: `transactionId, createdAt,
  indexedAt?, matchedAt?, organizationId?, organizationName?, requestId?,
  publishedAt?, receivedAt?`.
- Error codes: 400 Bad Request, 401 Unauthorized (`MTLS_REQUIRED`), 403
  Forbidden, 404, 405, 408 Request Timeout, 415, 409 CONFLICT "Duplicate"
  (integration manual), 429 RATE_LIMITED, 5xx. Backoff guidance: exponential
  1s, 2s, 4s ... max 30 s, max 5 attempts.

## MQTT

- MQTT **3.1.1**, TLS on port 8883, mTLS mandatory, KeepAlive 60 s,
  QoS 0 and 1 supported (**QoS 1 recommended** → at-least-once → client MUST
  deduplicate), retained messages: yes with `MessageExpiryInterval=7200`
  (2 h), no LWT.
- Subscribe topics (ACL-checked against certificate identity):
  - `VATSK-X/POKLADNICA-Y/QR-Z` - one transaction
  - `VATSK-X/POKLADNICA-Y/#` - one cash register (recommended for the plugin)
  - `VATSK-X/#` - whole tax subject
- Publish (alternative to REST generateNewTransactionId):
  `TRANSACTIONS/VATSK-X/POKLADNICA-Y` with `{"request": "transaction_id"}`;
  the response `{"id": "QR-...", "created_at": "..."}` is published to
  `VATSK-X/POKLADNICA-Y`.
- Health probe: publish "ping" to `test/ping`.
- WebSockets announced as a future additional notification channel (FAQ).

## Notification payload (Standard for Push Payment Notification v1.1)

```json
{
  "transactionStatus": "ACCC",
  "transactionAmount": {"currency": "EUR", "amount": "123.45"},
  "endToEndId": "QR-ab29e346f1d841c8a95a63d857490818",
  "dataIntegrityHash": "b150d2343fefd404f89788efece5e0c6bd423005553d708fb40bf600b1f4c8ae",
  "creditorAccount": {"iban": "SK4811000000002944116480"},
  "creditorName": "Merchant Name, sro",
  "happened_at": "2025-07-13T21:33:51.534Z"
}
```

- `transactionStatus`: only `ACCC` (AcceptedSettlementCompletedCreditorAccount,
  ISO 20022 external code) is supported - settlement completed on creditor
  account.
- `transactionAmount.amount`: decimal string, dot separator, **exactly two
  digits after the separator for EUR**, integer part without leading zeros,
  up to nine digits. Currency: EUR only (per law; banks always send EUR).
- `creditorAccount`/`creditorName` optional; `endToEndId` max 35,
  `dataIntegrityHash` max 64 hex.
- MQTT deliveries additionally carry `receivedAt`; getAllTransactions rows
  carry `happened_at`.

### dataIntegrityHash (Annex B - verified algorithm + test vector)

```
inputString = IBAN + "|" + amount + "|" + currency + "|" + endToEndId
hash = lowercase hex of SHA-256(inputString)
```

- IBAN: from `creditorAccount`, uppercase, no whitespace. Amount: dot
  decimal, ISO 4217 minor units (EUR → always 2 digits). Separator is the
  pipe `|` (U+007C).
- Golden vector:
  `SK4811000000002944116480|123.45|EUR|QR-ab29e346f1d841c8a95a63d857490818`
  → `b150d2343fefd404f89788efece5e0c6bd423005553d708fb40bf600b1f4c8ae`.
- Note: when `creditorAccount` is absent from a notification, verify against
  the merchant IBAN stored in plugin settings (it is the same account).

## Testing (INT)

- Prereqs: client cert (PEM), key, CA bundle; hosts `api-erp-i`, `api-banka-i`
  (443), `mqtt-i` (8883) reachable from SK/EU/USA IPs.
- `openssl s_client -connect api-erp-i.kverkom.sk:443 -cert client.pem -key client.key -CAfile ca.pem` → "Verification: OK".
- `curl -X GET https://api-erp-i.kverkom.sk/api/v1/status --cert ... --key ... --cacert ...`
- `mosquitto_sub -h mqtt-i.kverkom.sk -p 8883 -q 1 -t "VATSK-X/POKLADNICA-Y/#" --cafile ... --cert ... --key ...`
- Bank-side simulation: `POST https://api-banka-i.kverkom.sk/api/v1/payments`
  with headers `X-Request-ID` (UUID) and `Date` (ISO-8601 UTC, e.g.
  `2026-01-21T09:48:11Z` - NOT RFC 9110 format) and the notification JSON
  body. FR SR also ships a `kverkom_test.py` simulator (see manual).
- Contract stability: backward-compatible for 6 months; breaking changes in
  `v{n+1}`; deprecations announced min. 90 days ahead.

## Bank support (info-qrplatby.sk, as of research date)

Notification service ("notifikačný účet") activation documented for
**Tatra banka** and **SLSP** (George/Business24); merchant must ask their
bank to mark the business account as notification-enabled. Closed pilot for
"Family & Friends" merchants ran from autumn 2025; incident support during
pilot until 15.1.2026 via `kverkom.kasoveIS@financnasprava.sk`.

## Still to verify at implementation time (NOP phases)

- Current PROD availability + onboarding process outside the closed pilot.
- Whether the `id` vs `transaction_id` response-field discrepancy is resolved
  in a newer Services API revision (OpenAPI: `erp_openapi-0.5.1.yaml`).
- eKasa certificate export practicality per merchant segment (ORP vs VRP).
- MQTTnet library pin: current major version, MQTT 3.1.1 + client-cert TLS
  options on net10.0, reconnect patterns.
