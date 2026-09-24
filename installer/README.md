# Windows installers

Current package version: `0.1.131`. `build-installers.ps1` increases it by one on every run (in both csproj files and both `Package.wxs`), so every distributed rebuild is a real Windows Installer major upgrade instead of merely reconfiguring an older payload; keep `test-install-server-upgrade.ps1` and this line at the version that was actually shipped.

The repository builds two independent 64-bit Windows Installer packages:

- `Meimad-Planner-Client-Setup.msi` installs the Windows desktop client and a Start Menu shortcut.
- `Meimad-Planner-Server-Setup.msi` installs the Server and registers `Meimad Planner Server` as an automatic Windows Service.

The Server service uses bounded crash recovery: restart after 60 seconds on the
first and second failures, then take no automatic
action on the third or later failure until the failure count resets after 24
hours. This avoids an unbounded restart loop and leaves repeated failures visible
for operator diagnosis.

Build both packages from the repository root:

```powershell
.\installer\build-installers.ps1
```

The resulting packages are written to `installer\artifacts`. The script publishes self-contained `win-x64` application payloads first, so the target computer does not need a separately installed .NET runtime.

FANUC FOCAS support needs the FANUC-licensed FOCAS 2 library, which is not in version control. Put the 64-bit `Fwlib64.dll`, `fwlibe64.dll`, and the control-series `fwlib*64.dll` files from the FANUC kit into the repository's `focas\` folder (see `focas\README.md`) before building: the Server publish copies them into its `focas\` subfolder and the Server MSI harvests them from there, so installed Servers need no manual copy. On a Server installed without them, copy the same files into the install folder's `focas` subfolder (next to `Meimad.Planner.Server.exe`) or set the `MEIMAD_FOCAS_LIBRARY_DIR` environment variable for the service. Without the library, FANUC Machines report a failed `focas` connection check and stay `OFFLINE`; nothing else is affected.

Both packages are machine-wide installers and require Administrator elevation. Double-click the MSI and accept the Windows UAC prompt, or launch it from an elevated terminal. A non-elevated silent (`/qn`) install cannot display a UAC prompt and fails with Windows Installer error 1730.

Verify both package payloads without installing either application:

```powershell
.\installer\verify-installers.ps1
```

The build also writes `installer\artifacts\SHA256SUMS.txt`. Before an elevated
Server upgrade, run the non-mutating preflight from an ordinary PowerShell window:

```powershell
.\installer\install-server-upgrade.ps1 -ValidateOnly
```

It verifies the MSI hash/version and refuses readiness if any configured Machine
has CNC verification enabled. Then open PowerShell **as Administrator** and run:

```powershell
.\installer\install-server-upgrade.ps1
```

The elevated path installs the checksummed MSI, verifies the Service is Running
and Automatic, checks the bounded recovery policy, and rechecks that CNC
verification remained disabled. It never self-elevates and never prints a
verification secret.

The Server binaries are installed below `Program Files`. Mutable Server state is deliberately outside the installation directory:

- database: `%ProgramData%\MeimadPlanner\Server\data\meimad-planner.db`
- backups: `%ProgramData%\MeimadPlanner\Server\backups`
- E-Ink packages: `%ProgramData%\MeimadPlanner\Server\eink`

Uninstalling or upgrading the Server does not remove these mutable-data folders. The service keeps the default loopback-only address (`http://127.0.0.1:5080`); remote factory access still requires the deployment-specific TLS, authentication, firewall, and host-binding configuration described in the deployment documentation.

## Bundled client installer and client auto-update

Since 0.1.119 the Server MSI carries the matching client MSI. `build-installers.ps1` builds the client MSI first, copies it into the Server payload as `client-installer\Meimad-Planner-Client-Setup.msi`, and writes `client-installer\Meimad-Planner-Client-Setup.json` next to it:

```json
{"fileName":"Meimad-Planner-Client-Setup.msi","version":"0.1.119","sha256":"…","byteLength":107358447,"builtAt":"2026-09-18T09:14:00Z"}
```

The installed Server serves both through `GET /api/v1/client-installer` (manifest) and `GET /api/v1/client-installer/download` (MSI with `X-Meimad-Checksum-SHA256`). It recomputes the SHA-256 from the file and offers the MSI only when the manifest describes it, so a hand-edited folder is never distributed. The folder and file name are configurable (`ClientInstaller:Folder`, `ClientInstaller:FileName`), and a Server upgrade replaces the bundled MSI like any other payload file.

Every Windows client compares its own version with the Server's client package once per session after the first health check. When the Server carries a newer client, the client shows "New version available, the client will be installed", downloads the MSI to `%LOCALAPPDATA%\MeimadPlanner\updates`, verifies the checksum, and runs `msiexec /i … /passive /norestart` through a helper script that waits for the client to exit and restarts it afterwards (log: `install-client-update.log` in the same folder; the UAC prompt still appears because the client is a per-machine install). A client that is newer than the Server's package is never downgraded; it shows an attention notice instead.

Release consequence: upgrading the Server is enough to bring every client PC to the same version. The client MSI still exists separately for first installations. `verify-installers.ps1` fails when the bundled MSI or its manifest does not match the distributed client MSI.

## Customer portal push (Server)

Since 0.1.119 the Server can push customer-safe Order status to the cloud customer portal (`ClientPortal` section in `appsettings.json`, disabled by default). Because `appsettings.json` is a permanent, never-overwritten component, an upgraded Server keeps its existing file and does not gain the new section automatically; add it by hand (see `docs/user-help.md`, "Customer portal push"). The secret belongs in the file named by `ClientPortal:SharedSecretFile`, next to the executable, never in `appsettings.json`.
