# Release notes

## 1.0.1

- Concurrent confirmations no longer fail: when `UpdateAsync` hits an Identity
  concurrency error and another request already confirmed the email, the
  endpoint returns `200 {emailConfirmed: true, changed: false}`.
- README: language tag on the endpoint code fence (markdownlint MD040).

## 1.0.0

- Initial release: admin Greenfield endpoint
  `POST /api/v1/plugins/email-confirm/users/{idOrEmail}/confirm-email`
  (policy `btcpay.server.canmodifyserversettings`), idempotent, returns
  `{emailConfirmed, changed}`.
