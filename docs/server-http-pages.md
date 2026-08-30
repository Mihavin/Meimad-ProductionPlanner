# Server HTTP pages

This is the implemented inventory of browser-facing pages served by the Meimad Planner Server. It is not a list of the JSON API. The default development base address is `http://127.0.0.1:5080`; replace it with the configured factory-LAN Server address when applicable.

## Browser pages

| URL | Purpose | Access and behavior |
|---|---|---|
| `http://127.0.0.1:5080/tv-dashboard/` | Full-screen Machine Status TV dashboard. | Factory-LAN, read-only operational display. It has no forms or edit controls, reads the TV projection, and receives live Machine updates. HTML, CSS, and JavaScript responses use no-cache headers. |
| `http://127.0.0.1:5080/eink-simulator/` | Physical-firmware simulator for the current 800×480 monochrome E-Ink tablet. | Enter the registered hardware MAC and scoped device token. The page obtains the Server-assigned Tablet ID, renders the firmware production/Service screens, and provides the same D1/D2/D4 short/hold actions plus Reset. Bench fixtures remain local. Official Server access is limited to registration/status reads and the exact guarded `SEND_TO_QC` event; it is not a planning editor, package editor, or CNC-transfer page. |
| `http://127.0.0.1:5080/kitaron-setup/` | Configure/test the Server-owned read-only Kitaron SQL connection, mapping, and synchronization. | **Loopback only.** It is available from the Server PC through `127.0.0.1`/localhost. Non-local requests receive `404`. It stores the SQL secret through the Server protection boundary and may synchronize permitted Kitaron data into Meimad; Kitaron itself is always opened read-only. |

Use the trailing `/` shown above. Each page also serves its own `styles.css` and `app.js` below the same path.

## HTTP status URL

| URL | Response | Purpose |
|---|---|---|
| `http://127.0.0.1:5080/health` | JSON | Lightweight Server process/service status with `status`, service name, build version, and Server UTC time. This is an API-style status response, not an HTML page. |

Example response:

```json
{
  "status": "healthy",
  "service": "Meimad Planner Server",
  "version": "0.1.48",
  "serverTimeUtc": "2026-08-28T12:00:00Z"
}
```

The exact installed build version and current time vary.

## Routes that are not browser pages

- `/` has no implemented landing page and normally returns `404`.
- `/api/v1/...` and `/api/tablet...` are JSON/file/WebSocket application contracts used by the Windows client, TV page, E-Ink devices/simulator, and integrations. See [API contract](api-contract.md).
- Database size, diagnostic-data cleanup, and HTTP backup are exposed in the Windows client under **Setup > Database / Backup**. Their Server routes are `/api/v1/server-maintenance/...`; no separate HTML maintenance page exists.
- The Windows Planning Client, QC Queue, User Terminals, Case workspace, Machine Board, and Timeline are desktop application surfaces, not Server-hosted web pages.
- No Swagger/OpenAPI browser page is implemented.

## Deployment boundary

Keep all pages on the factory LAN/Wi-Fi. Do not add router port forwarding or public Internet exposure. The current human display-name/Edit Mode headers are not production authentication. The Kitaron setup page must remain loopback-only.
