#!/usr/bin/env python3
"""
Draws the keyboard-and-mouse controls graphic.

Generated rather than hand-drawn because a keyboard is 60-odd rectangles on a grid, and because the
bindings change: when Keys.cs changes, edit the tables here and re-run rather than nudging SVG paths.

Output:
    ../docs/controls.svg          standalone, for sharing
    ../docs/controls_inline.svg   same drawing, no XML header, for pasting into the manual

Usage:
    python build_controls_graphic.py
"""

import os

W, H = 1760, 1130

# -- palette ------------------------------------------------------------------------------------
# One colour per group of related actions. Chosen to stay distinguishable in greyscale too, since
# these get screenshotted and reposted at any quality.

INK = "#1b1815"
PAPER = "#f4efe6"
KEY_FACE = "#ffffff"
KEY_EDGE = "#8b8378"
KEY_TEXT = "#3a352e"
MUTED = "#7a7166"

GROUPS = {
    "mode":    ("#7b3fa0", "Modes and panels"),
    "place":   ("#2e7d4f", "Place and select"),
    "shape":   ("#d2691e", "Position and rotate"),
    "cycle":   ("#1f6f8b", "Choose what to build"),
    "save":    ("#b8860b", "Save and export"),
    "camera":  ("#5a6472", "Camera and movement"),
}

# -- keyboard geometry ---------------------------------------------------------------------------
# Rows of (label, width in key units). A unit is one standard key.

ROWS = [
    [("`~", 1), ("1", 1), ("2", 1), ("3", 1), ("4", 1), ("5", 1), ("6", 1), ("7", 1),
     ("8", 1), ("9", 1), ("0", 1), ("-", 1), ("=", 1), ("Backspace", 2)],
    [("Tab", 1.5), ("Q", 1), ("W", 1), ("E", 1), ("R", 1), ("T", 1), ("Y", 1), ("U", 1),
     ("I", 1), ("O", 1), ("P", 1), ("[", 1), ("]", 1), ("\\", 1.5)],
    [("Caps", 1.75), ("A", 1), ("S", 1), ("D", 1), ("F", 1), ("G", 1), ("H", 1), ("J", 1),
     ("K", 1), ("L", 1), (";", 1), ("'", 1), ("Enter", 2.25)],
    [("Shift", 2.25), ("Z", 1), ("X", 1), ("C", 1), ("V", 1), ("B", 1), ("N", 1), ("M", 1),
     (",", 1), (".", 1), ("/", 1), ("Shift", 2.75)],
    [("Ctrl", 1.25), ("Win", 1.25), ("Alt", 1.25), ("Space", 6.25), ("Alt", 1.25),
     ("Win", 1.25), ("Menu", 1.25), ("Ctrl", 1.25)],
]

# Which keys are lit, and in which group. Keyed by (row, label) because labels repeat - there are two
# Shift keys and two Alt keys, and only the left ones are bound.
LIT = {
    (0, "`~"): "cycle",
    (1, "Tab"): "camera",
    (1, "Q"): "shape",
    (1, "E"): "shape",
    (1, "["): "cycle",
    (1, "]"): "cycle",
    (1, "\\"): "mode",
    (2, "A"): "camera",
    (2, "S"): "camera",
    (2, "D"): "camera",
    (2, "F"): "place",
    (2, "G"): "shape",
    (2, "H"): "shape",
    (2, "K"): "save",
    (2, "L"): "mode",
    (2, "'"): "cycle",
    (3, "V"): "mode",
    (3, "Shift"): "camera",
    (4, "Ctrl"): "shape",
    (4, "Alt"): "save",
    (4, "Space"): "camera",
}
# Only the LEFT Shift/Alt/Ctrl are bound; the right-hand duplicates must stay unlit.
LEFT_ONLY = {(3, "Shift"), (4, "Alt"), (4, "Ctrl")}

# W is on row 1 and A/S/D on row 2 - the movement cluster spans both.
LIT[(1, "W")] = "camera"

NUMPAD = [
    [("Num", 1), ("/", 1), ("*", 1), ("-", 1)],
    [("7", 1), ("8", 1), ("9", 1), ("+", 1)],
    [("4", 1), ("5", 1), ("6", 1)],
    [("1", 1), ("2", 1), ("3", 1), ("Ent", 1)],
    [("0", 2), (".", 1)],
]
NUMPAD_LIT = {
    (1, "8"): "shape",
    (2, "4"): "shape",
    (2, "6"): "shape",
    (3, "2"): "shape",
    (2, "5"): "shape",
    (3, "1"): "shape",
}

KEY = 62          # key unit size
GAP = 5
KB_X, KB_Y = 60, 470


def esc(text):
    return (text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


out = []


def rect(x, y, w, h, fill, stroke=None, rx=7, sw=1.6, extra=""):
    s = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
    out.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" '
               f'rx="{rx}" fill="{fill}"{s}{extra}/>')


def text(x, y, s, size=15, fill=KEY_TEXT, anchor="middle", weight="normal", family=None):
    fam = family or "Segoe UI, Helvetica, Arial, sans-serif"
    out.append(f'<text x="{x:.1f}" y="{y:.1f}" font-family="{fam}" font-size="{size}" '
               f'fill="{fill}" text-anchor="{anchor}" font-weight="{weight}">{esc(s)}</text>')


def line(x1, y1, x2, y2, colour, width=2.4):
    out.append(f'<path d="M {x1:.1f} {y1:.1f} L {x2:.1f} {y2:.1f}" stroke="{colour}" '
               f'stroke-width="{width}" fill="none" stroke-linecap="round"/>')


def callout(x, y, label, group, anchor_x, anchor_y=None, width=None, align="middle"):
    """
    A coloured tag with a leader line to each key it describes.

    Pass a list of (x, y) for a combination: Alt+S needs a line to Alt AND to S, or the tag appears
    to belong to whichever single key it happens to touch.
    """
    colour = GROUPS[group][0]
    w = width or (len(label) * 8.6 + 26)
    h = 30
    left = x - w / 2 if align == "middle" else x

    anchors = anchor_x if isinstance(anchor_x, list) else [(anchor_x, anchor_y)]
    for ax, ay in anchors:
        line(x, y + h / 2 if ay > y else y - h / 2, ax, ay, colour)

    rect(left, y - h / 2, w, h, colour, rx=6)
    text(left + w / 2, y + 5.5, label, size=14, fill="#ffffff", weight="600")


# ---------------------------------------------------------------------------------------------
# Canvas
# ---------------------------------------------------------------------------------------------

out.append(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" '
           f'font-family="Segoe UI, Helvetica, Arial, sans-serif">')
rect(0, 0, W, H, PAPER, rx=0)

# Title banner
rect(0, 0, 760, 92, INK, rx=0)
text(36, 62, "Custom Scene Creator", size=42, fill="#ffffff", anchor="start", weight="700")
text(786, 42, "Bannerlord v1.4.7  ·  keyboard and mouse", size=19, fill=INK, anchor="start", weight="600")
text(786, 70, "Every key rebindable in MCM (optional). Key names are US-layout positions.",
     size=15, fill=MUTED, anchor="start")

# ---------------------------------------------------------------------------------------------
# Keyboard
# ---------------------------------------------------------------------------------------------

key_pos = {}          # (row, label) -> (x, y, w, h), for leader lines

y = KB_Y
for r, row in enumerate(ROWS):
    x = KB_X
    seen = {}
    for label, units in row:
        w = KEY * units + GAP * (units - 1)
        seen[label] = seen.get(label, 0) + 1
        first = seen[label] == 1

        group = LIT.get((r, label))
        if group and (r, label) in LEFT_ONLY and not first:
            group = None                      # right-hand Shift/Alt/Ctrl are not bound

        fill = KEY_FACE
        edge = KEY_EDGE
        label_fill = KEY_TEXT
        if group:
            colour = GROUPS[group][0]
            fill = colour
            edge = colour
            label_fill = "#ffffff"

        rect(x, y, w, KEY, fill, edge, sw=1.6)
        size = 15 if len(label) <= 2 else 13
        text(x + w / 2, y + KEY / 2 + 5, label, size=size, fill=label_fill,
             weight="700" if group else "500")

        if first:
            key_pos[(r, label)] = (x, y, w, KEY)
        x += w + GAP
    y += KEY + GAP

# Numpad, to the right of the main block
NP_X = KB_X + 15 * (KEY + GAP) + 40
np_y = KB_Y
np_pos = {}
for r, row in enumerate(NUMPAD):
    x = NP_X
    for label, units in row:
        w = KEY * units + GAP * (units - 1)
        group = NUMPAD_LIT.get((r, label))
        fill = GROUPS[group][0] if group else KEY_FACE
        edge = GROUPS[group][0] if group else KEY_EDGE
        rect(x, np_y, w, KEY, fill, edge, sw=1.6)
        text(x + w / 2, np_y + KEY / 2 + 5, label, size=14,
             fill="#ffffff" if group else KEY_TEXT, weight="700" if group else "500")
        np_pos[(r, label)] = (x, np_y, w, KEY)
        x += w + GAP
    np_y += KEY + GAP

text(NP_X + 110, KB_Y - 16, "Numpad", size=14, fill=MUTED, anchor="middle", weight="600")


def kx(r, label, frac=0.5):
    x, y, w, h = key_pos[(r, label)]
    return x + w * frac


def ky(r, label, top=True):
    x, y, w, h = key_pos[(r, label)]
    return y if top else y + h


# ---------------------------------------------------------------------------------------------
# Callouts above the keyboard
# ---------------------------------------------------------------------------------------------

callout(kx(0, "`~"), 400, "Asset picker", "cycle", kx(0, "`~"), ky(0, "`~"))
callout(kx(1, "\\") + 30, 250, "Cycle edit mode", "mode", kx(1, "\\"), ky(1, "\\"))
callout(kx(1, "[") + 34, 316, "Prev / next object", "cycle", kx(1, "["), ky(1, "["))
callout(kx(1, "Q") + 34, 400, "Rotate object", "shape", kx(1, "Q"), ky(1, "Q"))
line(kx(1, "E") - 34, 415, kx(1, "E"), ky(1, "E"), GROUPS["shape"][0])

# Below the keyboard
BELOW = KB_Y + 5 * (KEY + GAP) + 60

callout(kx(2, "F") - 20, BELOW, "Place (keyboard)", "place", kx(2, "F"), ky(2, "F", top=False))
callout(kx(2, "G") + 150, BELOW + 56, "Drop to ground  /  ground follow", "shape",
        kx(2, "H"), ky(2, "H", top=False))
callout(kx(2, "K") + 30, BELOW, "Save", "save", [(kx(2, "K"), ky(2, "K", top=False))])

callout(kx(2, "S") - 40, BELOW + 112, "Alt + S   save", "save", [
    (kx(4, "Alt"), ky(4, "Alt", top=False)),
    (kx(2, "S"), ky(2, "S", top=False)),
])

callout(kx(1, "E") + 250, 190, "Alt + E   export", "save", [
    (kx(4, "Alt"), ky(4, "Alt")),
    (kx(1, "E"), ky(1, "E")),
])
callout(kx(2, "L") + 330, BELOW, "Scene contents list", "mode", kx(2, "L"), ky(2, "L", top=False))
callout(kx(3, "V") - 60, BELOW + 112, "Cycle camera", "mode", kx(3, "V"), ky(3, "V", top=False))
callout(kx(4, "Ctrl") + 60, BELOW + 168, "Reset rotation and height", "shape",
        kx(4, "Ctrl"), ky(4, "Ctrl", top=False))

# Numpad callout
npx = np_pos[(2, "5")][0] + np_pos[(2, "5")][2] / 2
callout(NP_X + 130, 250, "Tilt, roll, raise, lower", "shape", npx, np_pos[(1, "8")][1])

# Movement cluster label
wx, wy, ww, wh = key_pos[(1, "W")]
callout(kx(1, "W") + 60, 190, "Move / pan camera  (W A S D)", "camera", kx(1, "W"), ky(1, "W"))

# ---------------------------------------------------------------------------------------------
# Mouse
# ---------------------------------------------------------------------------------------------

MX, MY = 1190, 140          # top-left of the mouse body
MW, MH = 190, 300

out.append(f'<path d="M {MX+MW/2} {MY} '
           f'C {MX+MW*0.95} {MY} {MX+MW} {MY+MH*0.30} {MX+MW} {MY+MH*0.55} '
           f'C {MX+MW} {MY+MH*0.90} {MX+MW*0.80} {MY+MH} {MX+MW/2} {MY+MH} '
           f'C {MX+MW*0.20} {MY+MH} {MX} {MY+MH*0.90} {MX} {MY+MH*0.55} '
           f'C {MX} {MY+MH*0.30} {MX+MW*0.05} {MY} {MX+MW/2} {MY} Z" '
           f'fill="#ffffff" stroke="{KEY_EDGE}" stroke-width="3"/>')
line(MX + 6, MY + MH * 0.40, MX + MW - 6, MY + MH * 0.40, KEY_EDGE, 2)

# Buttons
rect(MX + 22, MY + 18, 52, 92, GROUPS["place"][0], rx=18)
rect(MX + MW - 74, MY + 18, 52, 92, GROUPS["shape"][0], rx=18)
rect(MX + MW / 2 - 15, MY + 14, 30, 74, GROUPS["shape"][0], rx=15)

callout(MX - 150, MY + 60, "Place / act", "place", MX + 30, MY + 62)
callout(MX + MW + 130, MY + 60, "Hold: tilt and roll", "shape", MX + MW - 30, MY + 62)
callout(MX + MW + 138, MY - 10, "Wheel: raise / lower", "shape", MX + MW / 2, MY + 26)

text(MX + MW / 2, MY + MH + 40, "Left click does whatever the mode says:", size=14,
     fill=MUTED, weight="600")
text(MX + MW / 2, MY + MH + 62, "place · delete · pick up · open scripts", size=14, fill=MUTED)

# ---------------------------------------------------------------------------------------------
# Legend and notes
# ---------------------------------------------------------------------------------------------

LEG_X, LEG_Y = 1425, 545
text(LEG_X, LEG_Y - 16, "COLOUR KEY", size=13, fill=MUTED, anchor="start", weight="700")

for i, (key, (colour, label)) in enumerate(GROUPS.items()):
    row_y = LEG_Y + i * 29
    rect(LEG_X, row_y, 22, 22, colour, rx=5)
    text(LEG_X + 32, row_y + 16, label, size=15, fill=INK, anchor="start")

# Camera notes, in the gap right of the legend
NOTE_X = 1425
text(NOTE_X, LEG_Y + 208, "OVERHEAD (RTS) CAMERA", size=13, fill=MUTED, anchor="start", weight="700")
for i, note in enumerate([
    "WASD  pan  ·  speed scales with height",
    "Space / Left Alt  raise and lower",
    "Hold Shift + drag  rotate the view",
    "Shift + WASD  fly along the view",
]):
    text(NOTE_X, LEG_Y + 238 + i * 25, note, size=14, fill=INK, anchor="start")

NOTE2_X = 1425
text(NOTE2_X, LEG_Y + 366, "GOOD TO KNOW", size=13, fill=MUTED, anchor="start", weight="700")
for i, note in enumerate([
    "Hold Tab to leave.  No placement range limit.",
    "Weapons sheathe and the mouse stops reaching",
    "combat while an edit mode is on.",
    "Unsaved work is flagged in the top right.",
]):
    text(NOTE2_X, LEG_Y + 396 + i * 25, note, size=14, fill=INK, anchor="start")

# Footer
text(W / 2, H - 18, "Edit modes cycle with  \\  :  Off → Build → Delete → Move → Script",
     size=16, fill=INK, weight="600")

out.append("</svg>")

# ---------------------------------------------------------------------------------------------
# Write
# ---------------------------------------------------------------------------------------------

here = os.path.dirname(os.path.abspath(__file__))
docs = os.path.join(here, "..", "docs")
os.makedirs(docs, exist_ok=True)

body = "\n".join(out)

with open(os.path.join(docs, "controls.svg"), "w", encoding="utf-8") as handle:
    handle.write('<?xml version="1.0" encoding="utf-8"?>\n' + body + "\n")

with open(os.path.join(docs, "controls_inline.svg"), "w", encoding="utf-8") as handle:
    handle.write(body + "\n")

print("Wrote docs/controls.svg and docs/controls_inline.svg")
print(f"{W} x {H}")
