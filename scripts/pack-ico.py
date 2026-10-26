#!/usr/bin/env python3
"""把資料夾裡的 <尺寸>.png 打包成 .ico（每個尺寸一張 PNG，Windows Vista 以上支援）：python3 scripts/pack-ico.py <png 資料夾> <輸出.ico>"""
import os, struct, sys
src, out = sys.argv[1], sys.argv[2]
sizes = sorted(int(f[:-4]) for f in os.listdir(src) if f.endswith(".png") and f[:-4].isdigit())
images = [(s, open(os.path.join(src, f"{s}.png"), "rb").read()) for s in sizes]
header = struct.pack("<HHH", 0, 1, len(images))
offset = 6 + 16 * len(images)
entries, blobs = b"", b""
for s, data in images:
    entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset + len(blobs))
    blobs += data
open(out, "wb").write(header + entries + blobs)
print(out, len(images), "個尺寸", os.path.getsize(out), "bytes")
