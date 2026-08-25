#pragma once

#include <Arduino.h>
#include <stdarg.h>

// Single serial-output boundary for firmware diagnostics. Keep message values
// key/value shaped so logs remain useful when copied from a factory device.
namespace meimad::logging {

inline void write(const char* category, const char* format, ...) {
  char message[256]{};
  va_list arguments;
  va_start(arguments, format);
  vsnprintf(message, sizeof(message), format, arguments);
  va_end(arguments);
  Serial.printf("[%s] %s\n", category, message);
}

inline void flush() {
  Serial.flush();
}

}  // namespace meimad::logging

#define MEIMAD_LOG(category, format, ...) \
  meimad::logging::write(category, format, ##__VA_ARGS__)
