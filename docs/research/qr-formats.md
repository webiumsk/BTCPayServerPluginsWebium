# QR payload formats - research notes

Country profiles used by the plugin: SK → PayMe (Payment Link Standard 2.0),
CZ → SPD (Short Payment Descriptor), EU-generic → EPC QR (EPC069-12).
All specs below were read from the primary sources on 2026-07-30.

## SK: Payment Link Standard v2.0 (PayMe)

Source: SBA, <https://www.sbaonline.sk/projekt/standard-platobnej-linky/>,
PDF <https://www.sbaonline.sk/wp-content/uploads/2020/06/Payment_Link_standard_2_0.pdf>
(v2.0, effective 1.1.2026). The payme.sk implementation of the standard is
what NOP/"QR platby" uses; NOP's transactionId is the PI value.

- URI: `https://{PaymentLinkDomain}/{Version}/{Type}/{PaymentLinkSchemeID}?{Query}`
  → payme.sk implementation: `https://payme.sk/2/{type}/PME?...`
- Version segment: `2`. Scheme ID: `PME`.
- Types: `/m/` dynamic QR at POI (large merchants, non-editable order,
  **SCT Inst only**), `/e/` e-commerce, `/q/` static QR at POI (editable,
  only IBAN+CN mandatory), `/p/` person-to-person.
- **The plugin uses `/m/`** (dynamic QR at the point of interaction; the
  standard's footnote explicitly pairs it with Push Payment Notification).

Attributes (query string; max lengths before URL encoding):

| Param | Meaning | Type/max | `/m/` condition |
|---|---|---|---|
| `IBAN` | beneficiary IBAN | 34, ISO 20022 pattern `[A-Z]{2}[0-9]{2}[a-zA-Z0-9]{1,30}` | Mandatory |
| `AM` | amount, float, dot separator, 2 decimals | 9 | Mandatory |
| `CC` | ISO 4217; v2 allows only `EUR` | 3 | Mandatory |
| `DT` | due date `YYYYMMDD` | ISODate | **Omit** for `/m/`, `/q/`, `/e/` |
| `PI` | Payment identification = ISO 20022 EndToEndId | 35 | Mandatory (`ID_transaction`) |
| `MSG` | message → ISO 20022 Remittance information | 140 | Optional (recommended: business name + branch) |
| `CN` | creditor's name | 70 | Mandatory |

- PI formats: SK symbols `/VS{0,10}/SS{0,10}/KS{0,4}` **or** a plain E2E id
  such as `QR-ab29e346f1d841c8a95a63d857490818`. Recommendation 5.1: PI must
  not start or end with a single `/` nor contain `//` - note the spec's own
  `/VSxxx` example conflicts with this wording; for the NOP flow we always
  use the plain `QR-...` form, which avoids the ambiguity entirely.
- Encoding: standard URL encoding; spaces as `+` (preferred for readability)
  or `%20` - banks must accept both. Recommended character set (Annex A):
  `a-z A-Z 0-9 / - ? : ( ) . , ' +` and space; replace or drop characters
  outside the set; normalize Slovak diacritics to ASCII (recommended
  especially for CN).
- QR rendering: ISO/IEC 18004:2024, error correction level **M**.
- Example (`/m/`, from the spec):
  `https://payme.sk/2/m/PME?IBAN=SK6807200002891987426353&AM=200.30&CC=EUR&PI=QR-ab29e346f1d841c8a95a63d857490818&CN=The+Best+Cafes+ltd&MSG=Cafe+on+the+corner+Zilina`
- Non-participating banks: scanning falls back through the central payme.sk
  website (displays a PAY by square code with the details) - so a PayMe QR
  degrades gracefully even where the bank app lacks native support.

### PAY by square (implemented in v0.3 as the SK "bysquare" variant)

The older/parallel SK QR standard (SBA "PAY by square specifications" 1.2.0,
<https://bysquare.com/>). Encoding pipeline (verified against the
production-proven satflux implementation - Trinetus generator + xz - and
cross-checked with python lzma FORMAT_RAW; both produce identical bytes):

1. Tab-separated data string: `"" 1 1 amount currency dueDate(yyyyMMdd) VS
   CS SS originatorsReferenceInformation paymentNote 1 IBAN BIC 0 0
   beneficiaryName addr1 addr2` (amount = invariant `0.##`; UTF-8 with
   diacritics preserved).
2. CRC32 (IEEE, little-endian binary) prepended to the data.
3. Raw LZMA1, `lc=3, lp=0, pb=2, dict=128KiB`, end-of-payload marker
   (equivalent of `xz --format=raw --lzma1=lc=3,lp=0,pb=2,dict=128KiB`).
4. Header `0x00 0x00` (type Pay, version 0) + uint16 LE length of CRC+data,
   then the compressed stream.
5. 5-bit groups mapped through base32hex alphabet `0-9A-V` (zero-padded).

The plugin puts the NOP `QR-` reference into
OriginatorsReferenceInformation; whether a given bank propagates it as the
SEPA end-to-end id is bank-specific, so PayMe stays the recommended variant
for NOP auto-confirmation. LZMA note: independent encoders may emit
different (equally valid) streams for the same input - golden tests pin the
xz-identical ASCII vectors and round-trip-decode the UTF-8 one.

## CZ: SPD - Short Payment Descriptor ("QR Platba")

Source: <https://qr-platba.cz/pro-vyvojare/specifikace-formatu/>.

- Format: `SPD*1.0*` header + `KEY:value*` pairs. Charset ISO-8859-1, but for
  QR efficiency use only `0-9 A-Z space $ % * + - . / :`; special characters
  URL-encoded (`*` → `%2A`).
- Keys (subset relevant to the plugin):
  - `ACC` (mandatory, 46): `IBAN[+BIC]`, e.g. `ACC:CZ5855000000001265098001+RZBCCZPP`
  - `AM` (10): decimal, dot, max 2 places - `AM:480.55`
  - `CC` (3): ISO 4217 - **EUR works** (CZ banks route it as SEPA/foreign
    payment; UX per bank varies - documented in README as a caveat)
  - `RF` (16, integer): payment reference
  - `RN` (35): recipient name
  - `DT` (8): `YYYYMMDD`
  - `MSG` (60): message
  - `X-VS` (10, integer) / `X-SS` (10) / `X-KS` (10): Czech payment symbols
  - `PT` (3): payment type, `PT:IP` requests instant payment
  - `CRC32` (8, hex): optional integrity checksum
- Example: `SPD*1.0*ACC:CZ9106000000000000000123*AM:450.00*CC:CZK*MSG:PLATBA ZA ZBOZI*X-VS:1234567890`
- Plugin mapping: reference goes to `X-VS` (numeric, max 10 digits), `PT:IP`
  set to request instant processing, `MSG` = store/branch label, uppercase
  ASCII normalization applied.

## EU-generic: EPC QR (EPC069-12 v3.1, "girocode")

Source: EPC,
<https://www.europeanpaymentscouncil.eu/document-library/guidance-documents/quick-response-code-guidelines-enable-data-capture-initiation>
(PDF EPC069-12 v3.1, March 2024).

- QR error level **M**; max QR version 13 / **331-byte payload**.
- Elements in fixed order, separated by LF (or CRLF); last populated element
  has no trailing separator; empty optional elements between populated ones
  stay as empty lines:

| # | O/M | Max | Content |
|---|---|---|---|
| 1 | M | 3 | Service tag `BCD` |
| 2 | M | 3 | Version `001` or `002` |
| 3 | M | 1 | Character set (`1`=UTF-8, `2`=ISO 8859-1, ... `8`=ISO 8859-15) |
| 4 | M | 3 | Identification `SCT` |
| 5 | V1:M / V2:O | 11 | BIC (mandatory in V1; V2 optional within EEA) |
| 6 | M | 70 | Beneficiary name |
| 7 | M | 34 | IBAN (only IBAN allowed) |
| 8 | O | 12 | Amount `EUR#.##`; 0.01 ≤ amount ≤ 999999999.99 |
| 9 | O | 4 | Purpose (AT-T007) |
| 10 | O {Or | 35 | Structured remittance (ISO 11649 RF creditor reference) |
| 11 | Or} | 140 | Unstructured remittance (only one of 10/11) |
| 12 | O | 70 | Beneficiary-to-originator information |

- Plugin choice: version `002`, charset `1` (UTF-8), no BIC, **unstructured
  remittance** carrying the payment reference (our references are not ISO
  11649 RF references).
- Example payload (V2, from the spec):
  `BCD\n002\n2\nSCT\n\nFrançois D'Alsace S.A.\nFR1420041010050500013M02606\nEUR12.3\n\n\nClient:Marie Louise La Lune`

## Reference/VS semantics per profile (v0.1)

- SK: reference = NOP-shaped `QR-` + 32 lowercase hex (locally generated
  until the NOP backend phase acquires real ids via generateNewTransactionId;
  the format matches, so upgrading is transparent). Carried in PayMe `PI`.
- CZ: reference = numeric VS, 1-10 digits, no leading zero. Carried in SPD
  `X-VS`.
- EU: reference = the SK-shaped `QR-...` id carried in the EPC unstructured
  remittance line.

## Still to verify at implementation time

- Real-world scanning coverage: which SK bank apps handle a `/m/` PayMe QR
  natively vs via the payme.sk fallback (manual test matrix in README).
- CZ bank-app UX for `CC:EUR` SPD payments (Fio, ČSOB, KB).
