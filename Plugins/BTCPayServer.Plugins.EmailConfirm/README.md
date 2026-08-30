# Email Confirm API

Adds one admin Greenfield endpoint that core BTCPay Server is missing:

```text
POST /api/v1/plugins/email-confirm/users/{idOrEmail}/confirm-email
```

- **Auth:** Greenfield API key with `btcpay.server.canmodifyserversettings` (server admin).
- **Response:** `200 {"emailConfirmed": true, "changed": true|false}`; `404` when the user does not exist.
- **Idempotent:** confirming an already-confirmed user returns `changed: false`. Callers can use this as a capability probe.

## Why

Changing a user's email (via `PUT /api/v1/users/me`) resets `EmailConfirmed`. Core BTCPay has no API to set it back - only the server admin UI checkbox. On servers with the *"Require a confirmed email to log in"* policy this permanently locks the account's API keys after any email change ("You must have a confirmed email to log in").

This plugin lets orchestration software (e.g. [satflux.io](https://satflux.io)) re-confirm an account it manages right after a legitimate email change, instead of a human clicking checkboxes.

## Security notes

- The endpoint only flips `EmailConfirmed` - it does not change the email, password, roles or approval state.
- It requires the highest server-scoped permission; store-level or user-level keys cannot call it.
- Confirming an email you do not control does not grant anyone access; login still requires the account's credentials.

## Build

```bash
./build-plugin.sh
```

Produces `packaged/BTCPayServer.Plugins.EmailConfirm/<version>/BTCPayServer.Plugins.EmailConfirm.btcpay` - upload via BTCPay Server > Settings > Plugins.
