#!/usr/bin/env python3
"""Screenshot a Home Assistant dashboard as HA's own frontend draws it (reference for layout fidelity).

    ha_screenshot.py <ha-url> <token-file> <dashboard-path> <out.png> [--view N] [--width 1920] [--height 1080] [--light]

Logs in with a long-lived token (stored the way the frontend stores its own), dark theme by default, kiosk mode if the
dashboard has it. Needs: pip install playwright && python -m playwright install chromium
"""
import json, sys, time
from playwright.sync_api import sync_playwright

a = sys.argv[1:]
if len(a) < 4:
    print(__doc__); sys.exit(2)
url, tokfile, dash, out = a[0].rstrip("/"), a[1], a[2], a[3]
opt = lambda k, d: a[a.index(k) + 1] if k in a else d
view, w, h = opt("--view", "0"), int(opt("--width", "1920")), int(opt("--height", "1080"))
token = open(tokfile).read().strip()
tokens = {"access_token": token, "token_type": "Bearer", "expires_in": 315360000, "hassUrl": url,
          "clientId": url + "/", "expires": int(time.time() * 1000) + 315360000000, "refresh_token": ""}
with sync_playwright() as p:
    b = p.chromium.launch()
    ctx = b.new_context(viewport={"width": w, "height": h}, color_scheme="light" if "--light" in a else "dark", device_scale_factor=1)
    ctx.add_init_script("localStorage.setItem('hassTokens', %s);" % json.dumps(json.dumps(tokens)))
    page = ctx.new_page()
    page.goto(url + "/" + dash + "/" + view + "?kiosk", wait_until="networkidle", timeout=60000)
    time.sleep(6)   # cards, templates and history settle
    page.screenshot(path=out)
    b.close()
print("wrote", out)
