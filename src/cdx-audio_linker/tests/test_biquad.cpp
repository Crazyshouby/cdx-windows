#include "check.h"
#include "Eq.h"

#include <algorithm>
#include <cmath>
#include <vector>

namespace {
constexpr double kPi = 3.14159265358979323846;

// Steady-state RMS gain (dB) of the filter for a tone at `freq`, measured
// over the second half of a 1s buffer so the transient has died down.
double toneGainDb(BiquadFilter& f, double freq, int rate = 48000) {
    const int n = rate;
    std::vector<float> s(n);
    for (int i = 0; i < n; ++i) s[i] = static_cast<float>(std::sin(2.0 * kPi * freq * i / rate));
    f.process(s.data(), n);
    double acc = 0.0;
    for (int i = n / 2; i < n; ++i) acc += static_cast<double>(s[i]) * s[i];
    double rms = std::sqrt(acc / (n / 2));
    return 20.0 * std::log10(rms / std::sqrt(0.5));
}
} // namespace

// Low sample-rate outputs (Bluetooth hands-free: 16 / 22.05 / 24 kHz) with
// the 12 kHz top band boosted: the output must stay finite and bounded.
TEST(top_band_stays_stable_on_low_sample_rates) {
    for (int rate : { 16000, 22050, 24000 }) {
        for (double db : { 12.0, -12.0 }) {
            for (auto type : { BiquadFilter::Type::HighShelf, BiquadFilter::Type::Peaking, BiquadFilter::Type::BandPass }) {
                BiquadFilter f;
                f.configure(2, rate, type, 12000.0, db);
                std::vector<float> s(static_cast<size_t>(rate) * 2);
                unsigned seed = 12345;
                for (auto& v : s) { seed = seed * 1103515245u + 12345u; v = ((seed >> 9) & 0xFFFF) / 32768.0f - 1.0f; }
                f.process(s.data(), static_cast<size_t>(rate));
                float peak = 0.0f; bool finite = true;
                for (float v : s) { finite = finite && std::isfinite(v); peak = (std::max)(peak, std::fabs(v)); }
                CHECK(finite);
                CHECK(peak < 20.0f);
            }
        }
    }
}

// An impulse followed by silence: the filter memory decays to exactly zero
// (flush-to-zero is set per audio thread; here the decay itself must end).
TEST(impulse_tail_decays) {
    BiquadFilter f;
    f.configure(1, 48000, BiquadFilter::Type::Peaking, 1000.0, 9.0);
    std::vector<float> s(48000 * 5, 0.0f);
    s[0] = 1.0f;
    f.process(s.data(), s.size());
    CHECK(std::fabs(s.back()) < 1e-30f);
}
TEST(zero_db_shelf_is_bypassed_bit_exact) {
    BiquadFilter f;
    f.configure(2, 48000, BiquadFilter::Type::LowShelf, 60.0, 0.0);
    std::vector<float> s = { 0.5f, -0.5f, 0.25f, 0.125f };
    auto copy = s;
    f.process(s.data(), 2);
    for (size_t i = 0; i < s.size(); ++i) CHECK(s[i] == copy[i]);
}

TEST(low_shelf_boosts_bass_not_treble) {
    BiquadFilter f;
    f.configure(1, 48000, BiquadFilter::Type::LowShelf, 60.0, 12.0);
    CHECK_NEAR(toneGainDb(f, 10.0), 12.0, 0.5);
    BiquadFilter g;
    g.configure(1, 48000, BiquadFilter::Type::LowShelf, 60.0, 12.0);
    CHECK_NEAR(toneGainDb(g, 8000.0), 0.0, 0.3);
}

TEST(high_shelf_cuts_treble_not_bass) {
    BiquadFilter f;
    f.configure(1, 48000, BiquadFilter::Type::HighShelf, 12000.0, -12.0);
    CHECK_NEAR(toneGainDb(f, 20000.0), -12.0, 1.0);
    BiquadFilter g;
    g.configure(1, 48000, BiquadFilter::Type::HighShelf, 12000.0, -12.0);
    CHECK_NEAR(toneGainDb(g, 100.0), 0.0, 0.3);
}

TEST(peaking_band_is_centered) {
    BiquadFilter f;
    f.configure(1, 48000, BiquadFilter::Type::Peaking, 1000.0, 6.0);
    CHECK_NEAR(toneGainDb(f, 1000.0), 6.0, 0.2);
    BiquadFilter g;
    g.configure(1, 48000, BiquadFilter::Type::Peaking, 1000.0, 6.0);
    CHECK_NEAR(toneGainDb(g, 100.0), 0.0, 0.5);
}

TEST(bandpass_passes_center_rejects_far) {
    BiquadFilter f;
    f.configure(1, 48000, BiquadFilter::Type::BandPass, 1000.0, 0.0);
    CHECK_NEAR(toneGainDb(f, 1000.0), 0.0, 0.3);
    BiquadFilter g;
    g.configure(1, 48000, BiquadFilter::Type::BandPass, 1000.0, 0.0);
    CHECK(toneGainDb(g, 16000.0) < -15.0);
}

TEST(reconfigure_keeps_state_between_gain_tweaks) {
    // Nudging the gain mid-stream must not reset the filter memory (that
    // caused an audible click per slider move).
    BiquadFilter f;
    f.configure(2, 48000, BiquadFilter::Type::Peaking, 1000.0, 3.0);
    std::vector<float> s(400, 0.5f);
    f.process(s.data(), 200);
    float before = s[398];
    f.configure(2, 48000, BiquadFilter::Type::Peaking, 1000.0, 4.0);
    std::vector<float> t(4, 0.5f);
    f.process(t.data(), 2);
    // Continuous: the first output after the tweak stays close to the last one.
    CHECK_NEAR(t[0], before, 0.1);
}

TEST_MAIN()
