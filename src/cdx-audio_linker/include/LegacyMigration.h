#pragma once

// Up to 1.3, Audio Linker was "AudioLinker": its settings lived in %APPDATA%\AudioLinker and its
// "launch with Windows" entry was the HKCU Run value "AudioLinker". From 1.4 (cdx-audio_linker, part of
// the CDX suite) they are %APPDATA%\cdx-audio_linker and the Run value "cdx-audio_linker". On the first
// launch under the new name, these take the old ones over. The old folder is copied, not moved: an
// older build still installed keeps working with its own settings.

#include <string>

namespace al {

// Copies the top-level files of <appData>\AudioLinker (config.json and its backups) into
// <appData>\cdx-audio_linker, unless the latter already has a config.json. Sub-folders (WebView2
// cache) are not copied: they rebuild themselves. Returns a line for the log, empty when there was
// nothing to do. Pure file operations on the given folder, so the tests can run it on a temp folder.
std::wstring MigrateLegacyData(const std::wstring& appData);

// HKCU Run: the old "AudioLinker" value, if any, is removed and "cdx-audio_linker" set to this exe
// (--tray), so the option stays on without starting two programs at logon. Returns a line for the log,
// empty when there was nothing to do.
std::wstring MigrateLegacyRunEntry();

} // namespace al
