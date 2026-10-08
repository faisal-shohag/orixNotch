# Building, releasing and the Microsoft Store

## What runs where

| Workflow | Trigger | Does |
|---|---|---|
| `ci.yml` | every push to `main`, every PR | Builds, then builds all three packages (exe, MSI, MSIX) so packaging breaks show up in the PR. Packages are attached to the run as an artifact for 14 days. |
| `release.yml` | pushing a tag `vX.Y.Z` | Checks the tag matches `<Version>` in `OrixNotch.csproj`, builds the packages, writes `SHA256SUMS.txt`, publishes a GitHub Release. Tags with a suffix (`v1.2.0-beta.1`) become pre-releases. |
| `store.yml` | called by `release.yml` after a normal (non-pre-) release, or run by hand | Uploads the release's `*-store.msix` to Partner Center. Skipped until the Store app is set up (below). |
| Dependabot | weekly / monthly | PRs for NuGet packages and GitHub Actions. |

## Packages

| File | For | Install behaviour |
|---|---|---|
| `OrixNotch-X.Y.Z-setup.msi` | Most users (GitHub download) | Per-user install to `%LOCALAPPDATA%\Programs\OrixNotch`, **no admin prompt**. Starts OrixNotch when the install finishes and registers it to **open at login**. Upgrades close the running app and start the new one. Uninstall removes the app, the Start menu entry and the login entry; user data in `%LOCALAPPDATA%\OrixNotch` is kept. Silent: `msiexec /i <file> /qn LAUNCHAPP=0`. |
| `OrixNotch-X.Y.Z-portable.exe` | No-install use | Single self-contained exe. Turns on open-at-login for itself on first run. |
| `OrixNotch-X.Y.Z-store.msix` | Microsoft Store upload only | Self-contained (MSIX can't depend on the .NET 8 Desktop Runtime). The Store signs it; it can't be installed from the file. Its StartupTask is enabled, so Windows opens it at login after the first launch. |

Open at login is **on by default** for every install type (the user can switch it off in Settings → General, or in Task Manager → Startup apps). `Services/StartupService.cs` uses the MSIX StartupTask when the app is packaged and the per-user `Run` key otherwise.

## Releasing a version

```powershell
pwsh tools/Bump-Version.ps1 0.6.0      # sets <Version>, commits "release: v0.6.0", tags v0.6.0
git push origin main --follow-tags     # the tag starts release.yml
```

Build locally (output also copied to `output/`, which is git-ignored):

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
pwsh packaging/Build-Exe.ps1
pwsh packaging/Build-Msi.ps1
pwsh packaging/Build-Msix.ps1            # add -Sign for a self-signed sideload test package
```

Upgrading from 0.5.0: that MSI installed per-machine (Program Files). The new per-user MSI can't replace it, so uninstall the old OrixNotch from Settings → Apps once.

## Code signing (recommended before wide distribution)

Unsigned exe/MSI downloads show Windows SmartScreen "unknown publisher" warnings. To sign them in the Release workflow, add a code-signing certificate as repository secrets:

- `SIGNING_PFX_BASE64` — the `.pfx` file, base64 (`[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`)
- `SIGNING_PFX_PASSWORD` — its password

Without them everything still builds, unsigned. (The Store package never needs your certificate — the Store signs it.)

## Publishing to the Microsoft Store (when you're ready)

1. **Partner Center account** — register as an individual developer at <https://partner.microsoft.com/dashboard> (one-time fee).
2. **Reserve the name** — Apps and games → New product → MSIX or PWA app → reserve "OrixNotch".
3. **Product identity** — Product management → Product identity. Add these as repository **variables** (Settings → Secrets and variables → Actions → Variables):
   - `STORE_IDENTITY_NAME` = `Package/Identity/Name`
   - `STORE_PUBLISHER` = `Package/Identity/Publisher` (the `CN=…` value)
   - `STORE_PUBLISHER_DISPLAY_NAME` = `Package/Properties/PublisherDisplayName`
   - `STORE_PRODUCT_ID` = the Store ID (e.g. `9NXXXXXXXXXX`)

   The next release then builds `*-store.msix` with that identity.
4. **First submission by hand** — create the first submission in Partner Center: upload the `*-store.msix` from the release, fill in the listing (screenshots and art are in `store-assets/`), privacy policy URL (`docs/privacy-policy.md` published somewhere public), age rating, pricing.
   - Restricted capability `runFullTrust`: explain it's a desktop (WPF) app that needs full trust to draw the notch over other windows.
   - `userNotificationListener`: used to show Windows notifications on the notch, with the user's consent.
5. **Automate updates** — Partner Center → Account settings → User management → Microsoft Entra applications → add an app with the **Manager** role, create a key. Add repository **secrets**: `PARTNER_CENTER_TENANT_ID`, `PARTNER_CENTER_CLIENT_ID`, `PARTNER_CENTER_CLIENT_SECRET`, `PARTNER_CENTER_SELLER_ID` (Account settings → Legal info → Seller ID).

   From then on every published release uploads to the Store as a **draft** submission; review and submit it in Partner Center, or run the "Microsoft Store" workflow by hand with *commit* ticked to submit straight away.
