# Meimad server PC: recommended hardware

One machine that runs the Meimad Planner Server service, serves NC programs to the CNC machines over FTP, and holds the shared CNC data folder (SMB network drive).

Size from Michael's Network.xlsx (2026-09-28): 15 machines (9 FTP clients, 6 network-share), 4 NE2-D14P FTP data collectors, 3 office PCs, 15 WiFi user terminals, 1 dashboard TV. The spec below is comfortable up to roughly 30 machines.

## What the software actually needs (checked in the repo)

- The Server is a self-contained .NET win-x64 Windows service; no .NET runtime to install (installer/README.md).
- Data is SQLite in `%ProgramData%\MeimadPlanner\Server\data`, with verified backups written to `%ProgramData%\MeimadPlanner\Server\backups`, keeping the newest 14 (appsettings.json `Backup:RetentionCount`). Those backups sit on the same disk as the database, so the PC needs its own off-box backup.
- Listens on port 5080 for clients, TV dashboard and tablets. CNC monitoring polls Haas MTConnect/TCP, FANUC FOCAS (64-bit FANUC DLLs) and DPRNT over TCP, FTP or file share. All of this is light, constant network polling, not heavy compute.
- STEP 3D viewing and the NC toolpath viewer run on the client PCs, not on the server (docs/architecture.md), so the server needs no graphics card.
- Kitaron sync reads a SQL Server database over the network; nothing extra on this PC.
- Current config already maps `J:` to `\\M-Matehet-SRV\DATA`, so there is an existing file server today.

## Recommended build

| Part | Recommended | Why |
|---|---|---|
| Form factor | Small tower server (Dell PowerEdge T160/T360, HPE ProLiant ML30 Gen11, or Lenovo ThinkSystem ST50 V3/ST250 V3) | Built for 24/7, ECC memory, hot-swap disks, remote management card. A desktop PC works but fails more often and silently. |
| CPU | Intel Xeon E-2400 series, 6 cores (E-2436 / E-2456) | Meimad, FTP and SMB are light. 6 cores leave room for antivirus, Windows updates and backup jobs. |
| RAM | 32 GB ECC (16 GB minimum) | ECC protects the database and NC files from silent memory errors. 32 GB gives file-cache headroom for the CNC share. |
| System disk | 2 x 960 GB enterprise SSD, RAID 1 | OS, Meimad and the SQLite database. Enterprise SSDs have power-loss protection, which matters for a database. |
| Data disk | 2 x 2 to 4 TB (SSD or NAS-grade HDD), RAID 1 | CNC programs, releases, E-Ink packages, stock files. Separate from the system disk so a full share can never stop the database. |
| RAID controller | Hardware RAID (Dell PERC H355, HPE MR408i) | Disk failure does not stop production; you swap the disk. |
| Network | 2 x 1 GbE minimum | One port on the office LAN, one on a separate machine network for the CNCs (see notes). |
| Remote management | iDRAC / iLO / XClarity | Restart, see the screen and get disk-failure alerts without being at the PC. |
| OS | Windows Server 2025 Standard (or 2022) | See OS notes below. |
| UPS | 1500 VA line-interactive with USB or network shutdown (APC Smart-UPS 1500 or similar) | A power cut mid-write can damage the SQLite file. The UPS shuts Windows down cleanly. |
| Backup target | Small NAS (Synology/QNAP, 2 disks) or at least a USB disk, plus an offsite/cloud copy | Meimad's own backups stay on the same disk. Copy them, plus the CNC data folder, off the server every night. |

## Budget option

Dell T160 / HPE ML30 with a 4-core Xeon E-2414, 16 GB ECC, 2 x 1 TB SSD RAID 1 (system and data together), Windows Server 2025 Essentials, 1000 VA UPS, nightly copy to a USB disk. Fine for up to about 10 machines and 25 users. Main trade-offs: one volume for database and CNC data, less headroom, weaker backup.

Rough prices, inferred and not quoted (local prices will be higher): budget about 2,500 to 3,500 USD, recommended about 5,000 to 7,000 USD including OS license, UPS and NAS.

## OS notes

- Avoid Windows 10/11 Pro for this role. Pro allows only 20 simultaneous inbound connections to shared folders; 15 CNCs plus client PCs will hit that and machines will randomly fail to open the drive.
- Windows Server Standard is licensed per core (16-core minimum pack), which covers a 6-core CPU. Server Essentials is cheaper but only sold with the hardware (OEM), limited to 25 users / 50 devices and 10 cores.
- The FTP server is built into Windows Server (IIS FTP role). No third-party FTP software needed.

## CNC network notes

- Put the CNCs on their own network (second NIC or a VLAN on a managed switch) with no internet access. Old controls cannot be patched, and this keeps office traffic and viruses away from them.
- Check which file-share version each control supports. Some older controls only speak SMB 1, which is insecure and disabled on modern Windows. For those machines use FTP instead of the network drive rather than turning SMB 1 on for the whole server.
- Open only the needed ports in Windows Firewall: 5080 (Meimad) on the office side; FTP (21 plus a fixed passive range) and SMB (445) on the machine side.
- Meimad must also reach the CNCs from this PC: MTConnect/TCP ports on the Haas machines, FOCAS port 8193 on FANUCs, and each machine's DPRNT port.

## Open question

`\\M-Matehet-SRV\DATA` already exists as a file server. If the new PC replaces it, size the data disks for everything currently on it plus growth. If it stays, the new PC could hold only Meimad and the CNC share, and 2 TB of data disk is enough.

## Checked against Network.xlsx (2026-09-28)

- Windows Server is needed, not Windows 11 Pro. 9 CNC FTP clients plus 4 FTP data collectors means 13 FTP clients. The FTP server in Windows 10/11 is capped at about 10 simultaneous connections (Windows Server has no cap). The 20-connection share limit alone would still be fine (6 share machines + 3 PCs).
- Windows Server 2025 Essentials is enough: about 38 devices and fewer than 25 users is under its 50-device / 25-user limit, with little room to grow. Standard has no such limit.
- Two NICs, as the sheet shows: one on Factory, one on Machines.
- Machine 1 is listed on Factory; the other 14 machines are on Machines. Probably a typo; it should be on Machines.
- The 15 WiFi terminals sit on "Machines WIFI". Give that WiFi its own subnet/VLAN, not a bridge into the wired CNC network, and allow it only to reach the server on port 5080. Otherwise every tablet can reach every CNC control.
- Missing from the sheet: the router/firewall, switches, WiFi access points, the Kitaron/ERP server, \\M-Matehet-SRV, the backup NAS and the UPS. Add them so the IP plan is complete.
- Suggested addressing: Factory 192.168.1.0/24, Machines 192.168.10.0/24, Machines WIFI 192.168.20.0/24. Server, CNCs, collectors and the TV get fixed IPs; terminals get DHCP reservations by MAC.
