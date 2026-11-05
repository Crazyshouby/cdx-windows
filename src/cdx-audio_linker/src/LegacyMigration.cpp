// Taking over AudioLinker's settings and Run entry after the rename (see LegacyMigration.h).

#include "LegacyMigration.h"

#include <windows.h>

namespace al {

namespace {

constexpr const wchar_t* kRunKeyPath = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr const wchar_t* kOldName = L"AudioLinker";
constexpr const wchar_t* kNewName = L"cdx-audio_linker";

bool FileExists(const std::wstring& path) {
    DWORD a = GetFileAttributesW(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

} // namespace

std::wstring MigrateLegacyData(const std::wstring& appData) {
    const std::wstring oldDir = appData + L"\\" + kOldName, newDir = appData + L"\\" + kNewName;
    if (FileExists(newDir + L"\\config.json") || !FileExists(oldDir + L"\\config.json")) return L"";
    CreateDirectoryW(newDir.c_str(), nullptr);
    int copied = 0, failed = 0;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW((oldDir + L"\\*").c_str(), &fd);
    if (h != INVALID_HANDLE_VALUE) {
        do {
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
            // FALSE: overwrite. Nothing of ours is there yet (no config.json), except perhaps a file
            // a failed earlier attempt left half-copied.
            if (CopyFileW((oldDir + L"\\" + fd.cFileName).c_str(), (newDir + L"\\" + fd.cFileName).c_str(), FALSE)) ++copied;
            else ++failed;
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    std::wstring line = L"Réglages repris d'AudioLinker (" + oldDir + L" -> " + newDir + L") : " + std::to_wstring(copied) + L" fichier(s)";
    if (failed) line += L", " + std::to_wstring(failed) + L" en échec";
    return line;
}

std::wstring MigrateLegacyRunEntry() {
    HKEY key;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, kRunKeyPath, 0, KEY_QUERY_VALUE | KEY_SET_VALUE, &key) != ERROR_SUCCESS) return L"";
    std::wstring line;
    if (RegQueryValueExW(key, kOldName, nullptr, nullptr, nullptr, nullptr) == ERROR_SUCCESS) {
        if (RegQueryValueExW(key, kNewName, nullptr, nullptr, nullptr, nullptr) != ERROR_SUCCESS) {
            wchar_t exe[MAX_PATH] = {};
            GetModuleFileNameW(nullptr, exe, MAX_PATH);
            std::wstring cmd = L"\"" + std::wstring(exe) + L"\" --tray";
            RegSetValueExW(key, kNewName, 0, REG_SZ, reinterpret_cast<const BYTE*>(cmd.c_str()),
                           static_cast<DWORD>((cmd.size() + 1) * sizeof(wchar_t)));
        }
        RegDeleteValueW(key, kOldName);
        line = L"Lancement avec Windows repris d'AudioLinker (entrée Run « AudioLinker » remplacée par « cdx-audio_linker »)";
    }
    RegCloseKey(key);
    return line;
}

} // namespace al
