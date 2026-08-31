"use strict";

// Browser mirror of firmware/tablet_state_machine.cpp for explicit bench evidence.
// The firmware table remains the device authority.
const TABLET_POWER_POLICY = Object.freeze({
  READY_FOR_SETUP: Object.freeze({ sleepMode: "STAY_AWAKE", wifiDefault: "OFF", wakeSources: "BUTTON", periodicRefreshSeconds: 0, buttonRefresh: "WAIT_FOR_IN_SETUP_OR_TIMEOUT", wifiSessionTimeoutSeconds: 30 }),
  IN_SETUP: Object.freeze({ sleepMode: "STAY_AWAKE", wifiDefault: "OFF", wakeSources: "BUTTON", periodicRefreshSeconds: 0, buttonRefresh: "REFRESH_ONCE", wifiSessionTimeoutSeconds: 30 }),
  IN_SETUP_RUN: Object.freeze({ sleepMode: "STAY_AWAKE", wifiDefault: "OFF", wakeSources: "BUTTON", periodicRefreshSeconds: 0, buttonRefresh: "REFRESH_ONCE", wifiSessionTimeoutSeconds: 30 }),
  IN_QC: Object.freeze({ sleepMode: "DEEP_SLEEP", wifiDefault: "OFF", wakeSources: "BUTTON", periodicRefreshSeconds: 0, buttonRefresh: "REFRESH_ONCE", wifiSessionTimeoutSeconds: 30 }),
  READY_FOR_PRODUCTION: Object.freeze({ sleepMode: "DEEP_SLEEP", wifiDefault: "OFF", wakeSources: "BUTTON+TIMER", periodicRefreshSeconds: 60, buttonRefresh: "REFRESH_ONCE", wifiSessionTimeoutSeconds: 30 }),
  IN_PRODUCTION: Object.freeze({ sleepMode: "DEEP_SLEEP", wifiDefault: "OFF", wakeSources: "BUTTON", periodicRefreshSeconds: 0, buttonRefresh: "REFRESH_ONCE", wifiSessionTimeoutSeconds: 30 }),
  BLOCKED: Object.freeze({ sleepMode: "DEEP_SLEEP", wifiDefault: "OFF", wakeSources: "BUTTON+TIMER", periodicRefreshSeconds: 120, buttonRefresh: "REFRESH_ONCE", wifiSessionTimeoutSeconds: 30 }),
  UNKNOWN: Object.freeze({ sleepMode: "DEEP_SLEEP", wifiDefault: "OFF", wakeSources: "BUTTON+TIMER", periodicRefreshSeconds: 120, buttonRefresh: "REFRESH_ONCE", wifiSessionTimeoutSeconds: 30 })
});

function tabletPowerPolicyFor(status) {
  return TABLET_POWER_POLICY[status] || TABLET_POWER_POLICY.UNKNOWN;
}

window.MeimadTabletPowerPolicy = Object.freeze({
  all: TABLET_POWER_POLICY,
  forStatus: tabletPowerPolicyFor
});
