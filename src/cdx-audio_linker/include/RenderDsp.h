#pragma once

// Les traitements purs du fil de rendu (AudioEngine::runRenderSession), sortis de cette fonction de 400 lignes
// pour être testés seuls (tests/test_render_dsp.cpp ; audit du 3/11/2026, point 4.6). Aucun appel audio ici :
// des tampons de float entrelacés, des entiers, des atomiques. Le comportement est celui d'avant, à l'identique.

#include "Eq.h"

#include <algorithm>
#include <atomic>
#include <cstddef>
#include <vector>

namespace render {

// Fondu linéaire appliqué aux premières millisecondes jouées après une coupure (sous-alimentation, saut en avant
// de la correction de dérive), pour que le saut dans le flux ne claque pas.
constexpr int kDeclickFadeMs = 5;

// Écrêtage doux au-dessus de ce niveau : le fil de rendu donne au mélangeur de Windows des float non bornés, et une
// bosse d'égaliseur sur un passage fort y était écrêtée net. Genou progressif, 1.0 n'est atteint qu'à la limite.
constexpr float kSoftClipKnee = 0.95f;
inline float SoftClip(float x) {
    float a = x < 0.0f ? -x : x;
    if (a <= kSoftClipKnee) return x;
    float e = a - kSoftClipKnee;
    float y = kSoftClipKnee + e / (1.0f + e * 8.0f);
    if (y > 1.0f) y = 1.0f;
    return x < 0.0f ? -y : y;
}

// Relève `a` à `v` s'il est plus petit. Comparer-échanger plutôt que lire puis écrire : la lecture du statut remet
// le compteur à zéro par exchange(0), qu'une écriture fondée sur une lecture périmée pourrait écraser.
inline void RaiseMax(std::atomic<int>& a, int v) {
    int prev = a.load();
    while (v > prev && !a.compare_exchange_weak(prev, v)) {
    }
}

// Les fondus anti-clic. Le fondu d'entrée demandé à un tour s'applique au tour SUIVANT (jamais au tampon qui le
// demande) : appeler fadeIn avant underrun dans un même tour.
class Declicker {
public:
    void request(int sampleRate) {
        total_ = std::max(1, sampleRate * kDeclickFadeMs / 1000);
        remaining_ = total_;
    }
    bool pending() const { return remaining_ > 0; }

    void fadeIn(float* s, int frames, int channels) {
        if (remaining_ <= 0) return;
        int n = std::min(remaining_, frames);
        const int doneBefore = total_ - remaining_;
        for (int f = 0; f < n; ++f) {
            float g = static_cast<float>(doneBefore + f + 1) / total_;
            for (int c = 0; c < channels; ++c) s[f * channels + c] *= g;
        }
        remaining_ -= n;
    }

    // L'anneau n'a rendu que `got` échantillons sur `size` : la fin de l'audio reçu descend à zéro au lieu d'être
    // coupée en pleine onde, le reste est du silence, et le retour de l'audio se fera en fondu.
    void underrun(float* s, size_t size, size_t got, int channels, int sampleRate) {
        int gotFrames = static_cast<int>(got / channels);
        int fadeOutFrames = std::min(gotFrames, std::max(1, sampleRate * kDeclickFadeMs / 1000));
        for (int f = 0; f < fadeOutFrames; ++f) {
            int frameIdx = gotFrames - fadeOutFrames + f;
            float g = 1.0f - static_cast<float>(f + 1) / fadeOutFrames;
            for (int c = 0; c < channels; ++c) s[frameIdx * channels + c] *= g;
        }
        std::fill(s + got, s + size, 0.0f);
        request(sampleRate);
    }

private:
    int remaining_ = 0;
    int total_ = 0;
};

// Le gain appliqué au dernier échantillon, gardé d'un tour à l'autre : un changement de volume ou une coupure du son
// glisse échantillon par échantillon sur un tampon au lieu de sauter d'un coup (le « zipper »). Part de 0 : la lecture
// entre aussi en fondu au tout premier tampon.
class GainRamp {
public:
    float current() const { return current_; }

    void apply(std::vector<float>& s, int channels, float target) {
        size_t frameCount = channels > 0 ? s.size() / channels : 0;
        if (frameCount == 0) return;
        if (current_ == target) {
            // Régime établi (le cas courant : 100 %, sans atténuation de Windows) : pas de boucle par échantillon.
            if (target != 1.0f) {
                for (float& x : s) x *= target;
            }
            return;
        }
        float step = (target - current_) / static_cast<float>(frameCount);
        for (size_t f = 0; f < frameCount; ++f) {
            current_ += step;
            for (int c = 0; c < channels; ++c) s[f * channels + c] *= current_;
        }
        current_ = target;   // tomber juste sur la cible, sans dérive d'arrondi
    }

private:
    float current_ = 0.0f;
};

// La crête du tampon pour le vumètre, puis la protection contre l'écrêtage : ce qui dépasse le genou est adouci ici.
// Rend la crête, bornée à 1.
inline float PeakAndSoftClip(std::vector<float>& s) {
    float pk = 0.0f;
    for (float x : s) {
        float a = x < 0 ? -x : x;
        if (a > pk) pk = a;
    }
    if (pk > kSoftClipKnee) {
        for (float& x : s) x = SoftClip(x);
        if (pk > 1.0f) pk = 1.0f;
    }
    return pk;
}

// Le spectre à N bandes du panneau d'égaliseur : des passe-bandes d'analyse sur une copie du signal réellement
// envoyé, sans toucher à celui-ci. Reconfigurés seulement quand le format change.
template <int N>
class SpectrumMeter {
public:
    void process(const std::vector<float>& s, size_t frames, int channels, int sampleRate, const double (&freqs)[N],
                 std::atomic<int> (&milli)[N]) {
        if (channels != channels_ || sampleRate != rate_) {
            for (int b = 0; b < N; ++b) filters_[b].configure(channels, sampleRate, BiquadFilter::Type::BandPass, freqs[b], 0.0);
            channels_ = channels;
            rate_ = sampleRate;
        }
        for (int b = 0; b < N; ++b) {
            scratch_.assign(s.begin(), s.end());
            filters_[b].process(scratch_.data(), frames);
            float bpk = 0.0f;
            for (float x : scratch_) {
                float a = x < 0 ? -x : x;
                if (a > bpk) bpk = a;
            }
            RaiseMax(milli[b], static_cast<int>((bpk > 1.0f ? 1.0f : bpk) * 1000.0f));
        }
    }

private:
    BiquadFilter filters_[N];
    int channels_ = -1, rate_ = -1;
    std::vector<float> scratch_;
};

// La correction de dérive d'horloge, vérifiée chaque seconde : un correcteur proportionnel sur l'écart entre le
// remplissage de l'anneau et sa cible, et un filet de sécurité pour les grands écarts (veille, reprise).
// 20 ms d'écart → 0,05 % de vitesse ; borné à ±0,15 % pour rester inaudible.
constexpr double kDriftKp = 0.000025;
constexpr double kDriftMaxAdjust = 0.0015;

struct DriftStep {
    double speed = 1.0;   // le facteur de vitesse du rééchantillonneur
    size_t drop = 0;      // échantillons à jeter de l'anneau (trop plein)
    size_t pad = 0;       // échantillons de silence à faire ajouter par le fil de capture (trop vide)
};

// Écart positif : l'anneau est trop plein, le rééchantillonneur accélère ; négatif : il ralentit.
inline DriftStep DriftCheck(size_t avail, size_t target, int sampleRate, int channels) {
    DriftStep st;
    double errorMs = (static_cast<double>(avail) - static_cast<double>(target)) /
                     (static_cast<double>(sampleRate * channels) / 1000.0);
    double adj = 1.0 + errorMs * kDriftKp;
    if (adj < 1.0 - kDriftMaxAdjust) adj = 1.0 - kDriftMaxAdjust;
    if (adj > 1.0 + kDriftMaxAdjust) adj = 1.0 + kDriftMaxAdjust;
    st.speed = adj;
    if (avail > target * 3) {
        size_t drop = avail - target;
        drop -= drop % channels;
        if (drop > 0) { st.drop = drop; st.speed = 1.0; }
    } else if (avail < target / 4) {
        size_t add = target - avail;
        add -= add % channels;
        if (add > 0) { st.pad = add; st.speed = 1.0; }
    }
    return st;
}

// Un changement de latence voulu (curseur, alignement automatique) : aller droit à la cible au lieu d'y dériver à
// ±0,15 % (200 ms prendraient plus de deux minutes). Vitesse remise à 1.
inline DriftStep Resync(size_t avail, size_t target, int channels) {
    DriftStep st;
    if (avail > target) {
        size_t drop = avail - target;
        st.drop = drop - drop % channels;
    } else if (avail < target) {
        size_t add = target - avail;
        st.pad = add - add % channels;
    }
    return st;
}

}  // namespace render
