"use strict";

const ids = ["serverHost", "serverPort", "databaseName", "viewSchema", "viewName",
  "username", "password", "clearPassword", "enabled", "refreshIntervalSeconds"];
const elements = Object.fromEntries(ids.map(id => [id, document.getElementById(id)]));
const statusBadge = document.getElementById("statusBadge");
const message = document.getElementById("message");
const testSummary = document.getElementById("testSummary");
const testTime = document.getElementById("testTime");
const passwordState = document.getElementById("passwordState");
const columns = document.getElementById("columns");
const columnPanel = document.getElementById("columnPanel");
let currentVersion = 0;

async function request(path, options) {
  const response = await fetch(path, options);
  let body = null;
  try { body = await response.json(); } catch { }
  if (!response.ok) {
    const text = body?.error?.message || body?.message || `${response.status} ${response.statusText}`;
    const error = new Error(text);
    error.body = body;
    throw error;
  }
  return body;
}

function setBusy(busy) {
  document.querySelectorAll("button").forEach(button => button.disabled = busy);
}

function showSettings(value) {
  currentVersion = value.version;
  elements.serverHost.value = value.serverHost;
  elements.serverPort.value = value.serverPort;
  elements.databaseName.value = value.databaseName;
  elements.viewSchema.value = value.viewSchema;
  elements.viewName.value = value.viewName;
  elements.username.value = value.username;
  elements.password.value = "";
  elements.clearPassword.checked = false;
  elements.enabled.checked = value.enabled;
  elements.refreshIntervalSeconds.value = value.refreshIntervalSeconds;
  passwordState.textContent = value.passwordConfigured
    ? "An encrypted password is stored on this Server."
    : "No password is stored.";
  statusBadge.textContent = value.lastTestStatus.replaceAll("_", " ");
  statusBadge.className = `badge ${value.lastTestStatus === "succeeded" ? "success" : value.lastTestStatus === "failed" ? "failed" : "neutral"}`;
  testSummary.textContent = value.lastTestMessage || "Not tested.";
  testTime.textContent = value.lastTestAt ? `Tested ${new Date(value.lastTestAt).toLocaleString()}` : "";
}

async function load() {
  setBusy(true); message.textContent = "";
  try { showSettings(await request("/api/v1/kitaron/connection")); }
  catch (error) { message.textContent = error.message; }
  finally { setBusy(false); }
}

document.getElementById("connectionForm").addEventListener("submit", async event => {
  event.preventDefault(); setBusy(true); message.textContent = "";
  try {
    const value = await request("/api/v1/kitaron/connection", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        serverHost: elements.serverHost.value,
        serverPort: Number(elements.serverPort.value),
        databaseName: elements.databaseName.value,
        viewSchema: elements.viewSchema.value,
        viewName: elements.viewName.value,
        username: elements.username.value,
        password: elements.password.value || null,
        clearPassword: elements.clearPassword.checked,
        enabled: elements.enabled.checked,
        refreshIntervalSeconds: Number(elements.refreshIntervalSeconds.value),
        version: currentVersion
      })
    });
    showSettings(value); message.textContent = "Settings saved. Test the read-only connection before enabling periodic checks.";
    message.style.color = "#0b5f38";
  } catch (error) { message.textContent = error.message; message.style.color = "#9d231b"; }
  finally { setBusy(false); }
});

document.getElementById("testButton").addEventListener("click", async () => {
  setBusy(true); message.textContent = "Testing the read-only SQL Server view…"; message.style.color = "#33475b";
  columns.replaceChildren(); columnPanel.hidden = true;
  try {
    const result = await request("/api/v1/kitaron/connection/test", { method: "POST" });
    showSettings(result.settings);
    result.columns.forEach(item => {
      const row = document.createElement("div"); row.className = "column";
      row.textContent = `${item.name} · ${item.dataType}`; columns.append(row);
    });
    columnPanel.hidden = result.columns.length === 0;
    message.textContent = result.message; message.style.color = "#0b5f38";
  } catch (error) {
    if (error.body?.settings) showSettings(error.body.settings);
    message.textContent = error.message; message.style.color = "#9d231b";
  } finally { setBusy(false); }
});

document.getElementById("reloadButton").addEventListener("click", load);
load();
