#pragma once

#ifndef MEIMAD_PROVISION_TABLET_ID
#define MEIMAD_PROVISION_TABLET_ID "7439"
#endif

#ifndef MEIMAD_PROVISION_SERVER_URL
#define MEIMAD_PROVISION_SERVER_URL "http://192.168.137.1:5080"
#endif

// Development defaults. Provide per-device values through PlatformIO build
// flags for a provisioning upload. Wi-Fi and Server values are persisted in
// NVS; TabletID is compiled into the firmware and is never read from NVS.
namespace meimad::config {
constexpr char kDefaultWifiSsid[] = "Planner-Server";
constexpr char kDefaultWifiPassword[] = "a12345678";
constexpr char kServerBaseUrl[] = MEIMAD_PROVISION_SERVER_URL;
constexpr char kFirmwareTabletId[] = MEIMAD_PROVISION_TABLET_ID;
}
