// Taking over AudioLinker's settings after the rename (LegacyMigration.cpp), on a temp folder that
// stands for %APPDATA%. The Run entry (registry) is not exercised here.

#include "check.h"
#include "LegacyMigration.h"

#include <windows.h>
#include <fstream>
#include <string>

namespace {

std::wstring TempRoot() {
    wchar_t dir[MAX_PATH] = {};
    GetTempPathW(MAX_PATH, dir);
    std::wstring root = std::wstring(dir) + L"audio_linker_migration_" + std::to_wstring(GetCurrentProcessId()) + L"_" + std::to_wstring(GetTickCount64());
    CreateDirectoryW(root.c_str(), nullptr);
    return root;
}

void Write(const std::wstring& path, const std::string& text) { std::ofstream(path, std::ios::binary) << text; }

std::string Read(const std::wstring& path) {
    std::ifstream f(path, std::ios::binary);
    return std::string((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
}

bool Exists(const std::wstring& path) { return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES; }

} // namespace

TEST(nothing_to_take_over) {
    std::wstring root = TempRoot();
    CHECK(al::MigrateLegacyData(root).empty());
    CHECK(!Exists(root + L"\\cdx-audio_linker"));
}

TEST(takes_over_the_old_settings_and_keeps_them) {
    std::wstring root = TempRoot();
    CreateDirectoryW((root + L"\\AudioLinker").c_str(), nullptr);
    CreateDirectoryW((root + L"\\AudioLinker\\WebView2").c_str(), nullptr);
    Write(root + L"\\AudioLinker\\config.json", "{\"setups\":[\"Salon\"]}");
    Write(root + L"\\AudioLinker\\config.json.bak", "{}");
    std::wstring line = al::MigrateLegacyData(root);
    CHECK(!line.empty());
    CHECK(Read(root + L"\\cdx-audio_linker\\config.json") == "{\"setups\":[\"Salon\"]}");
    CHECK(Exists(root + L"\\cdx-audio_linker\\config.json.bak"));
    CHECK(!Exists(root + L"\\cdx-audio_linker\\WebView2"));                       // the cache is not copied
    CHECK(Read(root + L"\\AudioLinker\\config.json") == "{\"setups\":[\"Salon\"]}");   // the old copy stays
}

TEST(never_overwrites_the_new_settings) {
    std::wstring root = TempRoot();
    CreateDirectoryW((root + L"\\AudioLinker").c_str(), nullptr);
    CreateDirectoryW((root + L"\\cdx-audio_linker").c_str(), nullptr);
    Write(root + L"\\AudioLinker\\config.json", "ancien");
    Write(root + L"\\cdx-audio_linker\\config.json", "nouveau");
    CHECK(al::MigrateLegacyData(root).empty());
    CHECK(Read(root + L"\\cdx-audio_linker\\config.json") == "nouveau");
}

TEST_MAIN()
