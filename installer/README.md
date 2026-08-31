# Windows installers

Current package version: `0.1.53`. Increase it for every distributed rebuild so Windows Installer performs a real major upgrade instead of merely reconfiguring an older payload.

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
