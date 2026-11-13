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
