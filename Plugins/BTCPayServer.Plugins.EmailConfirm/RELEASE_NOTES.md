# Release notes

## 1.0.0

- Initial release: admin Greenfield endpoint
  `POST /api/v1/plugins/email-confirm/users/{idOrEmail}/confirm-email`
  (policy `btcpay.server.canmodifyserversettings`), idempotent, returns
  `{emailConfirmed, changed}`.
