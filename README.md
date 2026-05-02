# LPM SIM

On-prem .NET 9 Blazor Server app for warehouse-to-store stock allocation.
EOM Generate computes per-store / per-division targets; SIM Generate
allocates eligible boxes from the warehouse onto stores under SKU Max +
EOM caps.

## Stack

- **.NET 9** · Blazor Server · MudBlazor 7
- **SQL Server** (LPMSIM, RACKS, BFLDATA, Datareporting) — accessed via cross-DB queries
- **Microsoft Entra ID SSO** (optional, behind `Auth:Mode = OIDC`) — falls back to Windows Negotiate

## Repo layout

```
src/
  LpmSim.Core/         entities, role constants, ICurrentUser
  LpmSim.Data/         EF DbContext + engine code (EOM, SIM, reports)
  LpmSim.Web/          Blazor Server UI, auth, controllers, wwwroot
db/
  001_…sql                 chronological migrations — run in order on a new DB
  002_…sql
  …
  023_lpmuser_email_index.sql
docs/
  AUTH_OIDC_SETUP.md   step-by-step Entra ID cutover playbook
```

## First-run checklist for a new developer

```powershell
git clone https://github.com/<org>/LpmSim.git
cd LpmSim

# 1) Set the SQL connection strings (NEVER commit real ones to appsettings.json)
cd src/LpmSim.Web

dotnet user-secrets set "ConnectionStrings:Lpm" `
  "Server=<sqlhost>;Database=LPMSIM;User Id=<user>;Password=<pwd>;TrustServerCertificate=True;Encrypt=False;"

dotnet user-secrets set "ConnectionStrings:Warehouse" `
  "Server=<sqlhost>;Database=RACKS;User Id=<user>;Password=<pwd>;TrustServerCertificate=True;Encrypt=False;"

# 2) (Optional) If you're testing OIDC SSO locally, add Entra client secret:
# dotnet user-secrets set "AzureAd:ClientSecret" "<value>"

# 3) Run
cd ../..
dotnet run --project src/LpmSim.Web
```

App listens on `http://localhost:5216` by default. Browse there.

## Database — running migrations

The `db/` folder contains numbered SQL files that must run in order on a
fresh SQL Server. Each one is idempotent (safe to re-run).

```sql
-- In SSMS, connected to the LPMSIM database, F5 each file in order:
:r D:\path\to\repo\db\001_create_lpmdivmax.sql
…
:r D:\path\to\repo\db\023_lpmuser_email_index.sql
```

Or from the command line:

```powershell
foreach ($f in (Get-ChildItem db/*.sql | Sort-Object Name)) {
    sqlcmd -S "<sqlhost>" -d LPMSIM -E -i $f.FullName
}
```

## Authentication modes

The app supports two auth flows controlled by `Auth:Mode` in
`appsettings.json`:

| Mode        | Identifier               | When to use                     |
|-------------|--------------------------|---------------------------------|
| `Negotiate` | `BFLDOMAIN\sheeja`       | On-prem IIS, Windows AD network |
| `OIDC`      | `sheeja@bflgroup.ae`     | Microsoft Entra ID SSO (Azure)  |

See `docs/AUTH_OIDC_SETUP.md` for the cutover playbook when switching to
OIDC.

## Branching & PRs

- `main` is protected — no direct pushes.
- Work on a feature branch named `feature/<short-description>`,
  `fix/<short-description>`, or `chore/<short-description>`.
- Open a Pull Request → at least one reviewer approves → merge to `main`.
- CI builds every PR (see `.github/workflows/ci.yml`); merges blocked
  if the build fails.

## What never to commit

- Real connection strings, passwords, API keys, client secrets.
- `bin/`, `obj/`, `*.user`, `.vs/` (covered by `.gitignore`).
- Local-only `appsettings.*.json` overrides (also gitignored).

If you accidentally commit a secret, **rotate it immediately** (change
the SQL password, regenerate the Entra client secret) — git history is
retrievable even after deletion.

## Common commands

```powershell
# Build the whole solution
dotnet build

# Run the web app
dotnet run --project src/LpmSim.Web

# Run only the tests (when added)
dotnet test

# Add a NuGet package
dotnet add src/LpmSim.Web package <Package.Name>
```

## Where to find things

| Looking for…                          | Open                                                            |
|---------------------------------------|-----------------------------------------------------------------|
| EOM calculation                       | `src/LpmSim.Data/Eom/EomCalculator.cs`                          |
| SIM allocation engine                 | `src/LpmSim.Data/LpmSim/LpmSimGenerator.cs`                     |
| Report SQL                            | `src/LpmSim.Data/LpmSim/LpmSimReports.cs`                       |
| Q&A "Why is allocation blocked?" etc. | `src/LpmSim.Data/LpmSim/LpmSimInvestigator.cs`                  |
| Auth wiring                           | `src/LpmSim.Web/Program.cs`, `src/LpmSim.Web/Auth/`             |
| Database schema                       | `src/LpmSim.Data/LpmDbContext.cs` + `db/*.sql`                  |

