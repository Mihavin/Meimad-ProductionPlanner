# FANUC FOCAS 2 library (Meimad Server)

Put the 64-bit FANUC FOCAS 2 (Data Window Library) files from the FANUC kit in this folder:

- `Fwlib64.dll` (main library)
- `fwlibe64.dll` (Ethernet; required for `cnc_allclibhndl3`)
- the control-series libraries the main library loads at run time, for example
  `fwlib0iD64.dll`, `fwlib0DN64.dll`, `fwlib30i64.dll`, `fwlibNCG64.dll`

The Server build copies every file matching `*64.dll` here — FANUC's own naming convention
for its 64-bit binaries — into its own `focas\` subfolder (`bin\...\focas\` and the
published/installer payload), and `FocasNative` pre-loads the companions from that folder
before the first FOCAS call. Without `fwlibe64.dll` the connection test reports
`EW_NODLL (-15)`; without any library it reports that `Fwlib64.dll` was not found.

**Only `*64.dll` ships.** The 32-bit counterparts (`Fwlib32.dll`, `fwlib0iD.dll`, ...) and
anything else you keep here for reference (sample projects, other-platform builds, docs) are
never copied and are git-ignored (`focas/*` except this file) — the Server process is
x64-only and does not need them.

The shipped binaries are FANUC-licensed. Each build machine that produces the Server
installer needs its own copy of the `*64.dll` set here.
