// Les cas de conformité communs du bus et de suite.json (cdx-ui bus/bus-vectors.json, recopié dans cdx/ par charte-sync) :
// le client C++ (cdx/cdx_bus.h) doit donner les mêmes réponses que les clients C# et Rust, qui rejouent le même fichier.
#include "check.h"
#include "cdx/cdx_bus.h"
#include <nlohmann/json.hpp>

#include <fstream>
#include <set>
#include <sstream>
#include <string>

namespace {

nlohmann::json Vectors() {
    std::ifstream f(CDX_BUS_VECTORS);
    std::stringstream s;
    s << f.rdbuf();
    return nlohmann::json::parse(s.str());
}

}  // namespace

TEST(line_limit_is_the_common_one) {
    CHECK_EQ(Vectors()["max_line"].get<int>(), cdx::kBusMaxLine);
}

TEST(program_list_is_the_common_one) {
    auto v = Vectors();
    std::set<std::string> apps;
    for (const auto& a : v["apps"]) { apps.insert(a.get<std::string>()); CHECK(cdx::bus::IsApp(a.get<std::string>())); }
    CHECK_EQ(apps.size(), static_cast<size_t>(12));
    CHECK(!cdx::bus::IsApp("pilote"));
    CHECK(!cdx::bus::IsApp(""));
    CHECK(!cdx::bus::IsApp("Shell"));
}

TEST(every_accent_case_gives_the_expected_answer) {
    auto v = Vectors();
    CHECK(v["accent"].size() >= 10);
    for (const auto& c : v["accent"]) {
        std::string got = cdx::suite::ParseAccent(c["json"].get<std::string>(), c["fallback"].get<std::string>());
        std::string want = c["expect"].get<std::string>();
        if (got != want) std::printf("    %s : %s (attendu %s)\n", c["why"].get<std::string>().c_str(), got.c_str(), want.c_str());
        CHECK(got == want);
    }
}

TEST_MAIN()