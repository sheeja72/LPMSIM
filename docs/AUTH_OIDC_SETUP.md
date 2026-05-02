# LPM SIM — switching to Microsoft Entra ID SSO

The app now supports two authentication modes selected by a single
configuration value, `Auth:Mode`:

| Mode        | Identifier          | Sign-in flow                           |
|-------------|---------------------|----------------------------------------|
| `Negotiate` | `BFLDOMAIN\sheeja`  | Windows Kerberos / NTLM — current      |
| `OIDC`      | `sheeja@bflgroup.ae`| Microsoft Entra ID redirect, email key |

`Negotiate` is the default. The cutover plan below lets you stage the
switch without breaking existing users.

---

## 1. Database — make every active user discoverable by email

Run **migration 023** in SSMS (idempotent):

```
:r D:\AI Projects\LpmSim\db\023_lpmuser_email_index.sql
```

The script:

1. Adds `Email varchar(200) NULL` if missing (already present on most installs).
2. Adds the filtered unique index `UX_LPMUser_Email` on non-NULL emails.
3. Best-effort seeds `Email = Username` whenever `Username` already looks
   like an email (contains `@`).

Then populate the `Email` column for every active user. The simplest path
is one UPDATE per user:

```sql
UPDATE dbo.LPMUser
   SET Email = 'sheeja@bflgroup.ae'
 WHERE Username = 'BFLDOMAIN\sheeja';
```

Or if your AD has UPN already aligned with email, the data team can ship a
single bulk UPDATE joining to the AD export.

**Verification before cutover:**

```sql
SELECT COUNT(*) AS ActiveUsersMissingEmail
  FROM dbo.LPMUser
 WHERE IsActive = 1 AND (Email IS NULL OR Email = '');
-- Must return 0 before flipping Auth:Mode = OIDC.
```

---

## 2. Microsoft Entra ID — register the application

Ask your IT / Entra admin to register a new app with these settings.

| Setting                          | Value                                                     |
|----------------------------------|-----------------------------------------------------------|
| Supported account types          | Accounts in this organisational directory only (single tenant) |
| Platform                         | **Web**                                                   |
| Redirect URI (production)        | `http://lpmsim.bflgroup.ae/signin-oidc`                   |
| Redirect URI (dev — optional)    | `http://localhost:5216/signin-oidc`                       |
| Front-channel logout URL         | `http://lpmsim.bflgroup.ae/signout-callback-oidc`         |
| ID tokens                        | Enabled                                                   |
| Implicit grant                   | NOT required                                              |

Under **Certificates & Secrets → New client secret**, generate a client secret.
Capture:

- **Tenant ID** (Overview → Tenant ID)
- **Application (client) ID** (Overview → Application (client) ID)
- **Client secret value** (visible only once at creation)
- **Domain** (e.g. `bflgroup.ae` or `bflgroup.onmicrosoft.com`)

---

## 3. App configuration

### `appsettings.json` — placeholders to replace

```jsonc
"AzureAd": {
  "Instance":              "https://login.microsoftonline.com/",
  "Domain":                "bflgroup.ae",
  "TenantId":              "<Tenant ID GUID>",
  "ClientId":              "<Application (client) ID GUID>",
  "CallbackPath":          "/signin-oidc",
  "SignedOutCallbackPath": "/signout-callback-oidc"
}
```

### Client secret — User Secrets in dev, environment variable in prod

**Local dev** (from `src/LpmSim.Web/`):

```
dotnet user-secrets set "AzureAd:ClientSecret" "<value>"
```

**Production / IIS** — set as an environment variable in the application pool
(IIS Manager → site → Configuration Editor → `system.webServer/aspNetCore/environmentVariables`):

```
AzureAd__ClientSecret = <value>
```

The double underscore is the ASP.NET Core convention for config section
nesting via env vars. **Never** commit the secret to `appsettings.json`.

---

## 4. IIS — change the authentication module

In IIS Manager → site → **Authentication**:

| Module                  | State    |
|-------------------------|----------|
| Anonymous               | **Enabled**  |
| Windows                 | **Disabled** |
| Forms                   | Disabled |
| ASP.NET Impersonation   | Disabled |

The OIDC flow is unauthenticated at the IIS layer — Microsoft.Identity.Web
intercepts the request, redirects to Entra, validates the returned token,
and drops a session cookie that the rest of the pipeline sees as
authenticated.

If your `web.config` has an explicit `<windowsAuthentication enabled="true" />`
block, change it to `false` (or remove the block). Anonymous must be enabled.

---

## 5. Flip the switch

In `appsettings.json` (or production transform):

```jsonc
"Auth": {
  "Mode": "OIDC"
}
```

Restart the app pool. The next browser hit redirects to
`https://login.microsoftonline.com/...` — once the user signs in with their
corporate email, they land back on the app, the cookie is set, and they're in.

---

## 6. Verification checklist after cutover

```sql
-- a) Every active user has an email
SELECT COUNT(*) FROM dbo.LPMUser WHERE IsActive = 1 AND (Email IS NULL OR Email = '');
-- = 0
```

In the browser, after signing in:

- The sidebar still shows your name and role badge.
- Navigation works (no AccessDenied banner).
- The audit log (`LPMAuditLog`) records `ChangedBy = sheeja@bflgroup.ae`, not
  `BFLDOMAIN\sheeja`.
- Clicking the **Sign out** button in the sidebar redirects to Entra's
  global sign-out and back to the app, ending the session for real.
- The 5-minute idle watchdog also redirects to Entra logout.

---

## 7. Rollback plan

If anything is wrong post-cutover:

1. Set `Auth:Mode = Negotiate` in `appsettings.json`.
2. Re-enable Windows Authentication / disable Anonymous in IIS.
3. Restart the app pool.
4. The original Windows-Auth flow is back. No data loss — the new `Email`
   column is harmless when the mode is `Negotiate`.

The same code base supports both modes, so rollback is a config change,
not a redeploy.

---

## 8. What changed in code (for the curious)

- **`Program.cs`** — branch on `Auth:Mode`. OIDC mode wires up
  `AddMicrosoftIdentityWebApp(...)` + cookie auth + a controller for the
  sign-in / sign-out endpoints. Negotiate mode is unchanged.
- **`LpmClaimsTransformer`** — looks up `LPMUser.Username` (Negotiate) or
  `LPMUser.Email` (OIDC). Both modes attach role claims and the
  `lpm_active` marker.
- **`AuthStateCurrentUser`** — returns the email claim under OIDC so audit
  rows carry a recognisable identifier.
- **`App.razor`** — emits a `<meta name="lpm-signout-url">` whose value
  depends on the mode. `lpm-idle.js` reads it.
- **`UserEditDialog`** — now hints clearly that Email is the OIDC sign-in
  key.

No SQL is duplicated and no existing data is migrated destructively.
