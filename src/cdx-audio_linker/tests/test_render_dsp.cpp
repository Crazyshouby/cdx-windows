// Les traitements purs du fil de rendu (include/RenderDsp.h), sortis de AudioEngine::runRenderSession pour être testés seuls.
#include "check.h"
#include "RenderDsp.h"

#include <atomic>
#include <cmath>
#include <vector>

namespace {
constexpr double kPi = 3.14159265358979323846;

std::vector<float> Ones(int frames, int channels) { return std::vector<float>(static_cast<size_t>(frames) * channels, 1.0f); }

std::vector<float> Sine(double freq, int rate, int frames, int channels, float amp = 0.5f) {
    std::vector<float> v(static_cast<size_t>(frames) * channels);
    for (int f = 0; f < frames; ++f)
        for (int c = 0; c < channels; ++c) v[f * channels + c] = amp * static_cast<float>(std::sin(2 * kPi * freq * f / rate));
    return v;
}
}  // namespace

TEST(soft_clip_leaves_quiet_samples_and_bounds_loud_ones) {
    CHECK_EQ(render::SoftClip(0.5f), 0.5f);
    CHECK_EQ(render::SoftClip(-0.95f), -0.95f);
    CHECK(render::SoftClip(3.0f) <= 1.0f && render::SoftClip(3.0f) > 0.95f);
    CHECK(render::SoftClip(-3.0f) >= -1.0f && render::SoftClip(-3.0f) < -0.95f);
    CHECK(render::SoftClip(0.97f) < render::SoftClip(1.0f));   // croissant au-dessus du genou, jusqu'à saturer à 1
}

TEST(peak_is_measured_then_clipped_softly) {
    std::vector<float> v{0.1f, -0.5f, 0.3f};
    CHECK_NEAR(render::PeakAndSoftClip(v), 0.5f, 1e-6);
    CHECK_EQ(v[1], -0.5f);   // sous le genou : intact
    std::vector<float> loud{2.0f, -0.2f};
    CHECK_EQ(render::PeakAndSoftClip(loud), 1.0f);   // crête bornée à 1
    CHECK(loud[0] <= 1.0f && loud[0] > 0.95f);
    CHECK_EQ(loud[1], -0.2f);
}

TEST(gain_ramp_starts_from_silence_and_lands_on_target) {
    render::GainRamp g;
    auto v = Ones(100, 2);
    g.apply(v, 2, 1.0f);
    CHECK(v[0] > 0.0f && v[0] < 0.05f);              // premier tampon : entrée en fondu
    CHECK_NEAR(v[199], 1.0f, 1e-5);                   // dernière trame à la cible
    CHECK_EQ(v[0], v[1]);                             // les deux canaux d'une trame ont le même gain
    for (int f = 1; f < 100; ++f) CHECK(v[f * 2] >= v[(f - 1) * 2]);   // monotone
    CHECK_EQ(g.current(), 1.0f);
    auto w = Ones(50, 2);
    g.apply(w, 2, 1.0f);                              // régime établi à 1 : rien ne change
    for (float x : w) CHECK_EQ(x, 1.0f);
    g.apply(w, 2, 0.0f);                              // coupure du son : glisse jusqu'à 0
    CHECK(w[0] > 0.9f);
    CHECK_NEAR(w[99], 0.0f, 1e-5);
    std::vector<float> empty;
    g.apply(empty, 2, 0.5f);                          // tampon vide : le gain courant ne bouge pas
    CHECK_EQ(g.current(), 0.0f);
}

TEST(declicker_fades_out_the_tail_and_in_the_next_buffer) {
    const int rate = 48000, ch = 2, fade = rate * render::kDeclickFadeMs / 1000;   // 240 trames
    render::Declicker d;
    CHECK(!d.pending());
    auto v = Ones(480, ch);
    size_t got = 400 * ch;                             // l'anneau n'a rendu que 400 trames sur 480
    d.underrun(v.data(), v.size(), got, ch, rate);
    CHECK_EQ(v[(399) * ch], 0.0f);                     // la dernière trame reçue descend à zéro
    CHECK(v[(400 - fade) * ch] > 0.99f);               // le début du fondu de sortie est presque intact
    CHECK_EQ(v[0], 1.0f);                              // avant le fondu : intact
    for (size_t i = got; i < v.size(); ++i) CHECK_EQ(v[i], 0.0f);   // le manque est du silence
    CHECK(d.pending());
    auto next = Ones(100, ch);
    d.fadeIn(next.data(), 100, ch);                    // le tour suivant entre en fondu...
    CHECK_NEAR(next[0], 1.0f / fade, 1e-6);
    CHECK_NEAR(next[99 * ch], 100.0f / fade, 1e-6);
    auto after = Ones(200, ch);
    d.fadeIn(after.data(), 200, ch);                   // ...qui continue là où il s'était arrêté
    CHECK_NEAR(after[0], 101.0f / fade, 1e-6);
    CHECK_EQ(after[139 * ch], 1.0f);                   // 240e trame du fondu : plein niveau
    CHECK_EQ(after[199 * ch], 1.0f);
    CHECK(!d.pending());
}

TEST(spectrum_meter_lights_the_band_of_the_tone) {
    const int rate = 48000, ch = 2, frames = rate / 2;
    const double freqs[3] = {63.0, 1000.0, 8000.0};
    std::atomic<int> milli[3] = {};
    render::SpectrumMeter<3> m;
    auto v = Sine(1000.0, rate, frames, ch);
    m.process(v, frames, ch, rate, freqs, milli);
    CHECK(milli[1].load() > 300);                      // la bande de 1 kHz voit le ton
    CHECK(milli[0].load() < milli[1].load() / 4);
    CHECK(milli[2].load() < milli[1].load() / 4);
    CHECK_NEAR(v[1 * ch], 0.5f * std::sin(2 * kPi * 1000.0 / rate), 1e-6);   // le signal envoyé n'est pas touché
    std::atomic<int> quietMilli[3] = {};
    render::SpectrumMeter<3> fresh;                     // un nouveau mètre : l'état des filtres suit le flux d'un tampon à l'autre
    auto quiet = Sine(1000.0, rate, frames, ch, 0.05f);
    fresh.process(quiet, frames, ch, rate, freqs, quietMilli);
    CHECK(quietMilli[1].load() > 0 && quietMilli[1].load() < 100);   // proportionnel au niveau
}

TEST(raise_max_only_raises) {
    std::atomic<int> a{500};
    render::RaiseMax(a, 300);
    CHECK_EQ(a.load(), 500);
    render::RaiseMax(a, 700);
    CHECK_EQ(a.load(), 700);
}

TEST(drift_check_is_proportional_and_bounded) {
    const int rate = 48000, ch = 2;
    const size_t target = static_cast<size_t>(rate) * ch / 10;          // 100 ms
    const size_t ms = static_cast<size_t>(rate) * ch / 1000;            // 1 ms d'échantillons
    auto at = [&](size_t avail) { return render::DriftCheck(avail, target, rate, ch); };
    CHECK_EQ(at(target).speed, 1.0);
    CHECK_NEAR(at(target + 20 * ms).speed, 1.0005, 1e-9);                 // 20 ms de trop : +0,05 %
    CHECK_NEAR(at(target - 20 * ms).speed, 0.9995, 1e-9);
    CHECK_NEAR(at(target + 90 * ms).speed, 1.0015, 1e-9);                 // borné à ±0,15 %
    CHECK_NEAR(at(target / 4 + 2 * ms).speed, 1.0 - 0.0015, 1e-9);
    CHECK_EQ(at(target + 90 * ms).drop, static_cast<size_t>(0));
}

TEST(drift_check_safety_net_drops_or_pads_whole_frames) {
    const int rate = 48000, ch = 2;
    const size_t target = 9600;
    auto full = render::DriftCheck(target * 3 + 11, target, rate, ch);    // plus de trois fois la cible
    CHECK_EQ(full.drop, target * 2 + 10);                                   // ramené à la cible, en trames entières
    CHECK_EQ(full.speed, 1.0);
    CHECK_EQ(full.pad, static_cast<size_t>(0));
    auto empty = render::DriftCheck(target / 4 - 1, target, rate, ch);    // moins du quart
    CHECK_EQ(empty.pad % ch, static_cast<size_t>(0));
    CHECK(empty.pad >= target - target / 4 && empty.pad <= target - target / 4 + 1);
    CHECK_EQ(empty.speed, 1.0);
    CHECK_EQ(empty.drop, static_cast<size_t>(0));
}

TEST(resync_goes_straight_to_the_target) {
    auto down = render::Resync(10001, 6000, 2);
    CHECK_EQ(down.drop, static_cast<size_t>(4000));
    CHECK_EQ(down.pad, static_cast<size_t>(0));
    auto up = render::Resync(1000, 6001, 2);
    CHECK_EQ(up.pad, static_cast<size_t>(5000));
    auto same = render::Resync(6000, 6000, 2);
    CHECK_EQ(same.drop + same.pad, static_cast<size_t>(0));
    CHECK_EQ(down.speed, 1.0);
}

TEST_MAIN()