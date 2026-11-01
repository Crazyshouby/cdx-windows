"""Rend cdx-presentation.html en vidéo : une image par instant (Chrome headless, ?t=ms), puis ffmpeg.
    python docs/presentation/render.py [sortie.mp4] [--fps 30] [--jobs 4]
Les images vont dans un dossier temporaire ; la vidéo (1280 x 720, H.264) est écrite à côté de la page par défaut (cdx-presentation.mp4).
Aucune fenêtre ne s'ouvre, aucune frappe n'est simulée : chaque image est une capture d'une page statique à l'instant t.
"""
import os
import shutil
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

HERE = Path(__file__).resolve().parent
PAGE = HERE / 'cdx-presentation.html'
DUR_MS = 14000
CHROME = next((p for p in [r'C:\Program Files\Google\Chrome\Application\chrome.exe', r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'] if os.path.exists(p)), None)
FFMPEG = shutil.which('ffmpeg') or r'C:\Users\Valentin\Dev\MultiCam\ffmpeg\ffmpeg.exe'


def frame(i, fps, outdir, profiles):
    t = round(i * 1000 / fps)
    out = outdir / f'f{i:04d}.png'
    prof = profiles[i % len(profiles)]
    url = PAGE.resolve().as_uri() + f'?t={t}'
    subprocess.run([CHROME, '--headless=new', '--no-first-run', '--disable-gpu', '--hide-scrollbars', '--disable-background-networking', '--disable-component-update',
                    f'--user-data-dir={prof}', '--window-size=1280,720', '--force-device-scale-factor=1', '--virtual-time-budget=1500', f'--screenshot={out}', url],
                   check=True, capture_output=True, timeout=60)
    return out


def main():
    args = sys.argv[1:]
    fps = int(args[args.index('--fps') + 1]) if '--fps' in args else 30
    jobs = int(args[args.index('--jobs') + 1]) if '--jobs' in args else 4
    out = Path(next((a for a in args if a.endswith('.mp4')), HERE / 'cdx-presentation.mp4'))
    if not CHROME:
        sys.exit('Chrome ou Edge introuvable')
    n = int(DUR_MS * fps / 1000)
    tmp = Path(tempfile.mkdtemp(prefix='cdx-anim-'))
    profiles = [tmp / f'prof{j}' for j in range(jobs)]
    print(f'{n} images à {fps} i/s, {jobs} Chrome en parallèle, dans {tmp}')
    with ThreadPoolExecutor(jobs) as ex:
        for k, _ in enumerate(ex.map(lambda i: frame(i, fps, tmp, profiles), range(n))):
            if k % 60 == 0:
                print(f'  {k}/{n}')
    subprocess.run([FFMPEG, '-y', '-loglevel', 'error', '-framerate', str(fps), '-i', str(tmp / 'f%04d.png'), '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-crf', '18', '-movflags', '+faststart', str(out)], check=True)
    print('écrit :', out, out.stat().st_size // 1024, 'Ko')
    shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    main()
