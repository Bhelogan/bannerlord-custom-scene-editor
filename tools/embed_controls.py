#!/usr/bin/env python3
"""
Copies docs/controls.svg into the user manual.

The manual carries the drawing INLINE rather than linking it, so the .htm stays one shareable file.
The cost is that the manual holds a copy: editing controls.svg changes nothing until this is run.

Kept separate from build_controls_graphic.py on purpose. That script REGENERATES the drawing and
would overwrite anything edited by hand; this one only ever reads the .svg as it stands, so hand
edits survive.

Usage:
    python embed_controls.py
"""

import io
import os
import re

here = os.path.dirname(os.path.abspath(__file__))
repo = os.path.join(here, "..")
svg_path = os.path.join(repo, "docs", "controls.svg")
manual_path = os.path.join(repo, "USER_MANUAL.htm")

svg = io.open(svg_path, encoding="utf-8").read().strip()

# Drop the XML declaration - legal in a standalone file, not legal partway through an HTML document.
svg = re.sub(r"^<\?xml[^>]*\?>\s*", "", svg)

# The manual scales it to the page; the class is what the manual's CSS hooks onto.
#
# Matched with a regex rather than on the literal "<svg " because an editor may reformat the tag -
# Inkscape writes each attribute on its own line, so a plain string replace on
# "<svg " silently does nothing, leaving the drawing unscaled.
if "controls-svg" not in svg:
    svg = re.sub(r"<svg(?=[\s>])", '<svg class="controls-svg"', svg, count=1)

manual = io.open(manual_path, encoding="utf-8").read()

start = manual.find('<div class="controls-wrap">')
if start < 0:
    raise SystemExit("Could not find the controls block in USER_MANUAL.htm.")

end = manual.find("</div>", manual.find("</svg>", start))
if end < 0:
    raise SystemExit("The controls block in USER_MANUAL.htm is not closed as expected.")
end += len("</div>")

replacement = '<div class="controls-wrap">\n' + svg + "\n</div>"
manual = manual[:start] + replacement + manual[end:]

io.open(manual_path, "w", encoding="utf-8", newline="\n").write(manual)

print(f"Embedded {os.path.relpath(svg_path, repo)} into {os.path.basename(manual_path)}")
print(f"  svg    {len(svg) / 1024:6.1f} KB")
print(f"  manual {len(manual) / 1024:6.1f} KB")
