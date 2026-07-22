#pragma once
// SHIM: no-op console logging. Replaces Core's console_logging.h to sidestep the
// wchar_t*/CHAR16* (unsigned short) literal mismatch on Linux clang/gcc.
// Logging is cosmetic and never affects scoring.
#include <lib/platform_efi/uefi.h>

static unsigned char consoleLoggingLevel = 0;
static CHAR16 message[16384], timestampedMessage[16384];

template <class T> static unsigned int stringLength(const T*) { return 0; }
template <class A, class B> static void appendText(A*, const B*) {}
template <class A, class B> static void setText(A*, const B*) {}
template <class A, class B> static void appendTextShortenFront(A*, const B*, unsigned short) {}
template <class A, class B> static void appendTextShortenBack(A*, const B*, unsigned short) {}
template <class A> static void appendNumber(A*, unsigned long long, BOOLEAN) {}
template <class A> static void setNumber(A*, unsigned long long, BOOLEAN) {}
template <class A> static void logToConsole(const A*) {}
static inline void outputStringToConsole(const CHAR16*) {}
