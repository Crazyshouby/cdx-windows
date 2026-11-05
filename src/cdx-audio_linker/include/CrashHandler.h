#pragma once

// Writes a minidump to %LOCALAPPDATA%\cdx-audio_linker\crash\ on an unhandled
// exception (the last few are kept). Call once, early in startup.
void InstallCrashHandler();
