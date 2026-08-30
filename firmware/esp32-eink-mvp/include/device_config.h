#pragma once

#ifndef MEIMAD_PROVISION_DEVICE_TOKEN
#define MEIMAD_PROVISION_DEVICE_TOKEN ""
#endif

#ifndef MEIMAD_PROVISION_SERVER_URL
#define MEIMAD_PROVISION_SERVER_URL "http://192.168.137.1:5080"
#endif

// Development defaults. Leave these empty in source control and provide values
// through PlatformIO build flags or a local uncommitted copy of this file.
// Non-empty values are copied into NVS on first boot and then survive updates.
namespace meimad::config {
constexpr char kDefaultWifiSsid[] = "Planner-Server";
constexpr char kDefaultWifiPassword[] = "a12345678";
constexpr char kServerBaseUrl[] = MEIMAD_PROVISION_SERVER_URL;
constexpr char kDefaultTabletId[] = "6328";
// Provision a revocable device-scoped token locally. Never commit a live token.
constexpr char kDefaultDeviceToken[] = MEIMAD_PROVISION_DEVICE_TOKEN;
}
