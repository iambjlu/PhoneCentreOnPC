# -*- coding: utf-8 -*-
import codecs
chars = set()
for lead in range(0x81, 0xFF):
    for trail in list(range(0x40, 0x7F)) + list(range(0xA1, 0xFF)):
        for enc in ("big5", "gb2312"):
            try: chars.add(bytes([lead, trail]).decode(enc))
            except Exception: pass
for lo, hi in [(0x20, 0x7E), (0xA0, 0x17F), (0x2000, 0x206F), (0x2190, 0x21FF), (0x2460, 0x24FF), (0x2500, 0x25FF), (0x2600, 0x26FF),
               (0x3000, 0x303F), (0x3040, 0x30FF), (0x3100, 0x312F), (0x31A0, 0x31BF), (0x3200, 0x32FF), (0xFE30, 0xFE4F), (0xFF00, 0xFFEF)]:
    chars.update(chr(c) for c in range(lo, hi + 1))
chars = {c for c in chars if len(c) == 1 and ord(c) > 0x1f}
open("subset-chars.txt", "w", encoding="utf-8").write("".join(sorted(chars)))
print(len(chars), "個字元")
