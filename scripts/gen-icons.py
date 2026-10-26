#!/usr/bin/env python3
"""從 @material-design-icons/svg 的 round 樣式取 SVG 路徑，產生 Ui/MaterialIconData.cs。
要新增圖示就把名稱加進 NAMES 再執行：python3 scripts/gen-icons.py"""
import os, re, urllib.request

NAMES = """wifi wifi_off usb lan smartphone apps folder notifications sms call photo_library add cast desktop_windows
inventory_2 fingerprint restart_alt visibility visibility_off tune keyboard location_on location_off volume_up photo_camera
screen_rotation mobile_off power_settings_new close chevron_left chevron_right expand_more refresh arrow_back arrow_forward
arrow_upward check more_horiz delete download upload create_new_folder signal_wifi_off warning build sync play_arrow
description image movie music_note android folder_zip picture_as_pdf storage article search zoom_in zoom_out
person chat_bubble notifications_active call_made call_received call_missed
wifi_1_bar wifi_2_bar settings lock lock_open public share content_copy edit link computer monitor volume_off volume_mute
vibration do_not_disturb_on notifications_off my_location near_me place keyboard_alt info error check_circle help
wifi_tethering cloud folder_open open_in_new push_pin favorite arrow_drop_down home circle radio_button_unchecked
phone_android tablet_android stop cancel remove check_box check_box_outline_blank sd_card dns qr_code_2 key
screen_lock_portrait speaker_phone ads_click language translate""".split()

root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
out = {}
for n in NAMES:
    try:
        svg = urllib.request.urlopen(f"https://cdn.jsdelivr.net/npm/@material-design-icons/svg@0.14.13/round/{n}.svg", timeout=20).read().decode()
    except Exception as e:
        print("缺", n, e); continue
    ds = [m.group(1) for m in re.finditer(r'<path\b[^>]*?\bd="([^"]+)"[^>]*?/?>', svg) if 'fill="none"' not in m.group(0)]
    # <circle> 也轉成路徑（wifi_1_bar 之類只有圓點）
    for m in re.finditer(r'<circle\b[^>]*?/?>', svg):
        a = dict(re.findall(r'(\w+)="([^"]+)"', m.group(0)))
        cx, cy, r = float(a["cx"]), float(a["cy"]), float(a["r"])
        ds.append(f"M{cx - r} {cy}a{r} {r} 0 1 0 {2 * r} 0a{r} {r} 0 1 0 {-2 * r} 0z")
    if not ds: print("無路徑", n); continue
    out[n] = " ".join(ds)
lines = ["// 由 scripts/gen-icons.py 從 Material Icons（Round，Apache-2.0）的 SVG 路徑產生，24x24 viewBox。",
         "namespace PhoneCentre.Ui;", "", "internal static class MaterialIconData", "{",
         "    public static readonly Dictionary<string, string> Paths = new()", "    {"]
for k in sorted(out): lines.append(f'        ["{k}"] = "{out[k]}",')
lines += ["    };", "}", ""]
open(os.path.join(root, "Ui", "MaterialIconData.cs"), "w", encoding="utf-8").write("\n".join(lines))
print(len(out), "個圖示")
