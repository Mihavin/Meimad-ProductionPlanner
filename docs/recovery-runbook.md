# Complete recovery sets

Owner decisions, 2026-10-08: destination `\\192.168.0.240\data\SQlite_BU`; one external Windows scheduled task; maximum data loss **4 hours** (RPO); maximum restore time **4 hours** (RTO). Those are acceptance targets, not measured results. Cadence and retention are still open. An hourly trigger is a proposed starting point, allowing time for a failed attempt before the four-hour limit; validate it against measured backup duration. No new automatic deletion policy applies to recovery sets.

## What is implemented

The Server snapshots local SQLite online, checks integrity and foreign keys, enumerates the snapshot's immutable NC/tool/subprogram, Production Package and E-Ink file references, and holds a cleanup lease until those files have been copied. It checks stored sizes and SHA-256 hashes. It includes effective configuration, recoverable ASP.NET Data Protection keys (including revocations), and a manifest listing external Case Working Folders. Original engineering folders are dependencies only: their owner must back them up separately.

The `.mprb` archive uses AES-256-CBC with HMAC-SHA256 encrypt-then-MAC and independent keys derived using PBKDF2-SHA256 (600,000 iterations, random salt). Authentication precedes extraction. The Server performs a separate test restore, checks the database, referenced files and actual protected ERP/email secrets, then publishes `.mprb.complete.json` last. Both files constitute a published set. Pending archives, archives without markers and failed sets must not be used. The marker contains no keys/passwords. Database-only HTTP backups remain separate and retain their existing settings.

Restore always requires a new directory; it never overwrites or activates the live database. Keys are reprotected using Windows machine DPAPI in the restored private directory, so subsequent Server access requires the service identity to have filesystem permission. An activation guard prevents startup from the staged configuration until explicitly cleared. Automated same-PC restoration does not demonstrate replacement-PC, NAS or Windows Service acceptance.

## Configure the Server

1. Keep the live database and immutable artifact roots on local Server storage. Verify that the Server's Windows service account can create/read/rename files at the approved NAS destination. A mapped drive in an interactive session is insufficient. Never put the live database on the NAS.
2. Choose a strong recovery passphrase (at least 16 characters) and escrow it separately from the Server/NAS, with an owner able to retrieve it during an outage. Loss of the passphrase makes the archive unrecoverable. Do not place it in command arguments, scripts, logs or source control.
3. Run the Server executable's offline command `--recovery-password-file <new absolute local path>`, providing the passphrase through the process environment variable `MEIMAD_RECOVERY_PASSWORD`. This command creates a Windows machine-DPAPI protected credential file with access restricted to its creator, SYSTEM and Administrators, then exits without starting Server workers. Remove the variable from the launching shell afterward; the child clears only its own environment. Grant the actual service identity read access to the credential if needed. Keep a separate escrow copy of the passphrase; this local credential cannot recover a lost PC.
4. Set these Server configuration values through the existing service configuration mechanism, substituting the real private local credential path:

   ```json
   {
     "Recovery": {
       "Folder": "\\\\192.168.0.240\\data\\SQlite_BU",
       "PasswordFile": "C:\\ProgramData\\MeimadPlanner\\recovery-password.dpapi",
       "IndependentDestinationConfirmed": true
     }
   }
   ```

   `IndependentDestinationConfirmed` records the operator's storage check; the program cannot prove physical disk independence. This example is not an installed configuration. No default is redirected to the NAS by a software build.
5. Start the Server normally. In Windows Setup / Server maintenance, refresh and use **Create complete recovery set** with Setup permission. Verify a complete marker and the displayed age/result. A failed attempt retains the preceding successful receipt. The status is a local receipt cache, not a continuous NAS integrity monitor; periodically restore a selected published set.

## External Windows Task Scheduler

Use `installer/Invoke-RecoveryBackup.ps1` on the Server PC. Under the Windows account that will run the task, create a protected `PSCredential` file using `Get-Credential | Export-Clixml -LiteralPath <private credential file>`. Enter a Planner account with Setup permission. Restrict this file to the task identity and administrators. The file is bound to that Windows account and PC; recreate it on replacement hardware. This credential is distinct from the archive passphrase and the Server's NAS access identity.

Configure one task to invoke PowerShell with `-NoProfile -NonInteractive -File <absolute script path> -CredentialFile <absolute credential file> -ServerUri http://127.0.0.1:5080`, adjusting the configured Server port. The script rejects non-loopback addresses, signs in, requests a Server-owned complete set, and signs out. It does not open SQLite or copy engineering files. Disable overlapping task instances, run whether the user is logged on or not, and capture task exit/failure results. Choose cadence, retry and task runtime limits from measured duration so failed attempts are detected before the newest verified snapshot exceeds four hours. Do not add an in-process scheduler alongside it. The supplied script does not register or enable a task.

The HTTP trigger is synchronous. A timeout/disconnect can leave an unconfirmed result; inspect status and completion markers before retrying. Repeated successful requests create separate sets. The Windows button uses the client's existing long-request timeout; use the external script for large scheduled sets. Scheduling, service-account access and alerts need a deployment test.

## Replacement-PC restore and activation

1. Record incident time and available set snapshot times. Stop/quiesce the original Server and all external tasks before cutover. Preserve the old data for reconciliation; do not overwrite it. Record any production/ERP events after the selected snapshot before resuming external activity.
2. On an isolated replacement Windows PC, obtain the matching Server build (or a deliberately tested compatible build), the archive and its complete marker, escrow passphrase and engineering-folder backups. Older binaries refuse newer schema versions; verify app/schema versions in the manifest. Matching version is preferred. Do not start the normal service yet.
3. Supply `MEIMAD_RECOVERY_PASSWORD` in the process environment and run `Meimad.Planner.Server.exe --recovery-restore <archive.mprb> --restore-to <new absolute local directory>`. Clear the variable in the launching shell afterward. Wrong passwords, corrupt/missing files, incompatible schema, unsafe paths or unusable keys fail verification and remove the new staging directory. Existing destinations are refused.
4. Review `recovery-manifest.json` and `appsettings.Recovery.json`. Restore or explicitly map every listed external engineering folder and verify service permissions. Inspect all effective settings, including network addresses, external integrations and client distribution paths. The generated configuration redirects SQLite, artifact roots, key storage and database backups into the new restore directory. The source-machine password-file setting is cleared. Recreate recovery credentials, permissions and scheduler credentials on this PC.
5. Grant the intended service identity access to the restored local data and protected-key folder. Test protected ERP/email settings without sending mail or writing ERP. Verify representative current and historical NC/tool/subprogram, Production Package and E-Ink content. Run only one authoritative Server. Prevent external integrations from acting until reconciled and deliberately enabled.
6. After the checks, explicitly change `Recovery:ActivationPending` to `false` in the staged configuration and start with `--RecoveryConfigurationFile <absolute appsettings.Recovery.json path>` using the intended Windows Service account. Merely restoring files does not alter the installed service's arguments/configuration. Record and review cutover configuration before starting it.
7. Verify signed-in Windows access, TV/tablet reads, protected settings, package checksums and service restart behavior. Measure elapsed restore/cutover time and the gap between incident time and restored snapshot. Accept only if RTO and RPO are each at most four hours, including separately restored dependencies. Keep the original system stopped after cutover.

## Acceptance evidence still required

Record NAS permission failure, unavailable share, actual disk-full behavior, interrupted transfer, restart during a backup, independent passphrase retrieval, replacement-PC key recovery under the production service identity, representative artifact counts/sizes, and measured restore time. Unit-test injected write failure and same-PC DPAPI checks do not replace these exercises. Decide recovery-set and evidence retention separately; never infer permission to delete production evidence from this runbook.
