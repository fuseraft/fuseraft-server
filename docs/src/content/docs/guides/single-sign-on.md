---
title: Single sign-on
description: Sign in with any OpenID Connect provider, link identities to accounts, and create accounts on first sign-in.
---

Any OpenID Connect provider works: Entra ID, Google, Okta, Keycloak, Authentik and others.

## Configure it

Register `https://<your host>/signin-oidc` as a redirect URI with your provider, then add an
`Auth:Oidc` section to `appsettings.json`:

```json
"Auth": {
  "Oidc": {
    "Authority": "https://login.example.com/realms/acme",
    "ClientId": "fuseraft-web",
    "DisplayName": "Acme SSO",
    "AutoProvision": true,
    "AllowedEmailDomains": ["acme.com"],
    "AdminEmails": ["alice@acme.com"]
  }
}
```

Put the client secret in the environment rather than the file:

```bash
Auth__Oidc__ClientSecret=…
```

`DisplayName` labels the sign-in button. `Scopes` defaults to `openid`, `profile` and `email`.
Every key is listed in [Configuration](/fuseraft-web/reference/config/#single-sign-on).

## Which account an identity signs in as

An identity signs in as the account it's linked to, matched on issuer and subject, never on email.

- **Link an existing account:** sign in with a password, open the **Account** page and click
  **Link**.
- **Create accounts on first sign-in:** with `AutoProvision`, a verified email in an
  `AllowedEmailDomains` domain gets an account the first time it signs in.
- **Admins:** a verified email in `AdminEmails` gets an admin account on its first sign-in, with or
  without `AutoProvision`. On a fresh install, that's a way to create the first admin instead of the
  setup link.

An email the provider marks unverified never gets an account. Someone without one is told to ask an
admin to create it, then link it from their Account page.

## Single sign-on only

Set `Auth:LocalAccounts` to `false` to turn off username and password sign-in. Keep a way to sign in
as an admin first: an `AdminEmails` address, or an admin account with a linked identity.
