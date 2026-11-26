"""Generates the fake API's images and the tagged video files: python3 gen.py <work_dir>
→ <work_dir>/img/*.jpg et <work_dir>/media/series/<Série {dm-id}>/<Saison {dm-id}>/E01 … {dm-idvN}.mkv"""
import os, sys, subprocess
sys.path.insert(0, os.path.dirname(__file__)); from data import SERIES, PEOPLE
from PIL import Image, ImageDraw, ImageFont
T = sys.argv[1]; img = os.path.join(T, "img"); os.makedirs(img, exist_ok=True)
try: font = lambda s: ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", s)
except Exception: font = lambda s: ImageFont.load_default()
def make(path, w, h, color, text, size):
    im = Image.new("RGB", (w, h)); d = ImageDraw.Draw(im)
    for y in range(h):
        k = 1 - y / h * 0.8; d.line([(0, y), (w, y)], fill=tuple(int(c * k) for c in color))
    f = font(size); bb = d.textbbox((0, 0), text, font=f)
    d.text(((w - bb[2]) / 2, (h - bb[3]) / 2), text, font=f, fill=(255, 255, 255))
    im.save(path, quality=88)
for pid, (pname, _) in PEOPLE.items():
    make(f"{img}/{pid}.jpg", 300, 300, (110, 90, 160), pname.strip()[0].upper(), 120)
for sid, s in SERIES.items():
    make(f"{img}/{sid}.jpg", 400, 600, s["color"], s["title"].split()[-1], 44)
    sdir = os.path.join(T, "media", "series", f"{s['title']} {{dm-{sid}}}")
    for nid, n in s["seasons"].items():
        make(f"{img}/{nid}.jpg", 400, 600, tuple(min(255, c + 40) for c in s["color"]), n["title"], 40)
        ndir = os.path.join(sdir, f"{n['title']} {{dm-{nid}}}"); os.makedirs(ndir, exist_ok=True)
        for i, v in enumerate(n["videos"], 1):
            make(f"{img}/{nid}v{i}.jpg", 640, 360, s["color"], f"Ép. {i}", 60)
            if i <= n["present"]:
                out = os.path.join(ndir, f"E{i:02d} {v[0]} {{dm-{nid}v{i}}}.mkv")
                if not os.path.exists(out):
                    subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=gray:s=320x180:d=2", "-c:v", "libx264", "-t", "2", out], check=True)

# A real episode WITHOUT any {prefix-id}: our providers never see it, it only gets the cast cascaded from its series/season
untagged = os.path.join(T, "media", "series", "Les Aventuriers {dm-aventuriers}", "Saison 2024 {dm-a1}", "E08 Sans identifiant.mkv")
if not os.path.exists(untagged):
    subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=gray:s=320x180:d=2", "-c:v", "libx264", "-t", "2", untagged], check=True)

# A whole series WITHOUT any {prefix-id} (series, season, episodes): it only gets a cast through the fallback key "jf-<item id>"
for ep in ("E01 Premier.mkv", "E02 Second.mkv"):
    out = os.path.join(T, "media", "series", "Série sans identifiant", "Saison 1", ep)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    if not os.path.exists(out):
        subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=gray:s=320x180:d=2", "-c:v", "libx264", "-t", "2", out], check=True)

# A heavy poster (noise does not compress): used by the source test to check the reduced preview
if not os.path.exists(os.path.join(img, "big.jpg")):
    import random
    big = Image.frombytes("RGB", (2400, 3200), random.Random(1).randbytes(2400 * 3200 * 3))
    big.save(os.path.join(img, "big.jpg"), quality=95)

# Demo dump: images of its entries, and the files of the dump library (/media/dump): tagged, exact name, noisy name,
# SxxExx, other title (search_titles), bare {id}, and one file that matches nothing. Targets of dump-real.mjs.
from data import DUMP
for did, *_ in DUMP:
    make(f"{img}/dump{did}.jpg", 400, 600, (60 + did % 9 * 14, 90, 150), str(did), 70)
    make(f"{img}/dump{did}c.jpg", 640, 360, (150, 60 + did % 7 * 15, 90), "#%d" % did, 70)
# An episode of a SERIES of the library that is also an entry of the dump (tagged): the coverage screen links the dump's series to it
sdump = os.path.join(T, "media", "series", "Série liée au dump", "Season 1", "Chroniques de la forêt {dp-1004}.mkv")
os.makedirs(os.path.dirname(sdump), exist_ok=True)
if not os.path.exists(sdump):
    subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=gray:s=320x180:d=2", "-c:v", "libx264", "-t", "2", sdump], check=True)
ddir = os.path.join(T, "media", "dump"); os.makedirs(ddir, exist_ok=True)
for fname in ["Voyage {dp-1001}.mkv", "Les Sentiers du Nord.mkv", "Rivages oublies VOSTFR 1080p.mkv", "Chroniques de la forêt S01E02 (RAW) [x264].mkv",
              "The Last Lighthouse.mkv", "Machin {1007}.mkv", "Documentaire inconnu sans rapport.mkv"]:
    out = os.path.join(ddir, fname)
    if not os.path.exists(out):
        subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=gray:s=320x180:d=2", "-c:v", "libx264", "-t", "2", out], check=True)

# Episodes of a SERIES library with no tag at all: a dump source must recognise them by name (dump-real.mjs, last phase)
for fname in ["Épisode démo 10 S01E01.mkv", "Épisode démo 11 S01E02.mkv"]:
    out = os.path.join(T, "media", "seriesdump", "Série sans tag", "Season 1", fname)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    if not os.path.exists(out):
        subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=gray:s=320x180:d=2", "-c:v", "libx264", "-t", "2", out], check=True)

# Videos the mock API offers for download (download-real.mjs): a direct MP4 (served with a generic content type), an HLS playlist
# with MPEG-TS segments, and a page that is not a video at all
dl = os.path.join(img, "media"); os.makedirs(dl, exist_ok=True)
src = os.path.join(dl, "b1v2.mp4")
if not os.path.exists(src):
    subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=gray:s=320x180:d=20", "-f", "lavfi", "-i", "sine=d=20", "-vf", "noise=alls=70:allf=t", "-c:v", "libx264", "-b:v", "4M", "-c:a", "aac", "-shortest", src], check=True)
if not os.path.exists(os.path.join(dl, "b1v3.m3u8")):
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", src, "-c", "copy", "-f", "hls", "-hls_time", "1", "-hls_list_size", "0",
                    "-hls_segment_filename", os.path.join(dl, "b1v3-%d.ts"), os.path.join(dl, "b1v3.m3u8")], check=True)

# Master playlist of the HLS episode (b1v3): three real renditions (90, 180 and 270 px tall) advertised as 360 / 720 / 1080
# so that the quality choice of the download can be checked on the height of the resulting MP4. b1v3.m3u8 is replaced by this master.
if not os.path.exists(os.path.join(dl, "b1v3-q720.m3u8")):
    for tag, size in (("q360", "160x90"), ("q720", "320x180"), ("q1080", "480x270")):
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", src, "-vf", "scale=" + size.replace("x", ":"), "-c:v", "libx264", "-c:a", "aac",
                        "-f", "hls", "-hls_time", "1", "-hls_list_size", "0", "-hls_segment_filename", os.path.join(dl, f"b1v3-{tag}-%d.ts"),
                        os.path.join(dl, f"b1v3-{tag}.m3u8")], check=True)
    with open(os.path.join(dl, "b1v3.m3u8"), "w") as f:
        f.write("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=640x360\nb1v3-q360.m3u8\n"
                "#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=1280x720\nb1v3-q720.m3u8\n"
                "#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1920x1080\nb1v3-q1080.m3u8\n")
# Preview video of an episode that has its file (theme hover preview): a 12 s moving pattern served by server.py at /previews/
pv = os.path.join(img, "previews"); os.makedirs(pv, exist_ok=True)
if not os.path.exists(os.path.join(pv, "a3v1.mp4")):
    subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=s=640x360:d=12:r=15", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", os.path.join(pv, "a3v1.mp4")], check=True)
