#include <Arduino.h>

namespace {
constexpr int kButtonPins[] = {2, 3, 5};
constexpr const char* kButtonNames[] = {"KEY1/D1", "KEY2/D2", "KEY3/D4"};
bool previousStates[] = {HIGH, HIGH, HIGH};
uint32_t lastHeartbeat = 0;
}

void setup() {
  Serial.begin(115200);
  const uint32_t serialStarted = millis();
  while (!Serial && millis() - serialStarted < 3000) delay(10);

  Serial.println("MEIMAD HARDWARE DIAGNOSTIC: BUTTONS ONLY");
  Serial.println("Expected: HIGH=released, LOW=pressed");
  for (size_t index = 0; index < 3; ++index) {
    pinMode(kButtonPins[index], INPUT_PULLUP);
    previousStates[index] = digitalRead(kButtonPins[index]);
    Serial.printf("%s GPIO%d initial=%s\n",
                  kButtonNames[index], kButtonPins[index],
                  previousStates[index] == LOW ? "LOW/PRESSED" : "HIGH/RELEASED");
  }
}

void loop() {
  for (size_t index = 0; index < 3; ++index) {
    const bool state = digitalRead(kButtonPins[index]);
    if (state != previousStates[index]) {
      delay(30);
      const bool debouncedState = digitalRead(kButtonPins[index]);
      if (debouncedState != previousStates[index]) {
        previousStates[index] = debouncedState;
        Serial.printf("%s GPIO%d %s\n",
                      kButtonNames[index], kButtonPins[index],
                      debouncedState == LOW ? "PRESSED" : "RELEASED");
      }
    }
  }

  if (millis() - lastHeartbeat >= 2000) {
    lastHeartbeat = millis();
    Serial.printf("ALIVE uptime_ms=%lu states=%d,%d,%d\n",
                  static_cast<unsigned long>(millis()),
                  digitalRead(kButtonPins[0]),
                  digitalRead(kButtonPins[1]),
                  digitalRead(kButtonPins[2]));
  }
  delay(10);
}
