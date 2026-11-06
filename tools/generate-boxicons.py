"""Render Boxicons 2.1.4 font glyphs as portable PNG icons.

Run with the unpacked npm package directory as the first argument. Requires Pillow.
"""
import re
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

package = Path(sys.argv[1])
output = Path(__file__).resolve().parent.parent / 'assets' / 'icon-packs' / 'Boxicons'
output.mkdir(parents=True, exist_ok=True)
font = ImageFont.truetype(str(package / 'fonts' / 'boxicons.ttf'), 40)
css = (package / 'css' / 'boxicons.css').read_text(encoding='utf-8')
for name, code in re.findall(r'\.(bx[sl]?-[\w-]+):before\s*\{\s*content:\s*"\\([0-9a-f]+)"', css):
    image = Image.new('RGBA', (48, 48))
    draw = ImageDraw.Draw(image)
    char = chr(int(code, 16))
    box = draw.textbbox((0, 0), char, font=font)
    draw.text(((48 - box[2] - box[0]) / 2, (48 - box[3] - box[1]) / 2), char, font=font, fill='#60a5fa')
    bounds = image.getbbox()
    if bounds:
        glyph = image.crop(bounds)
        scale = min(42 / glyph.width, 42 / glyph.height)
        glyph = glyph.resize((round(glyph.width * scale), round(glyph.height * scale)), Image.Resampling.LANCZOS)
        image = Image.new('RGBA', (48, 48))
        image.alpha_composite(glyph, ((48 - glyph.width) // 2, (48 - glyph.height) // 2))
    image.save(output / f'{name}.png')
(output / 'LICENSE.txt').write_text((package / 'LICENSE').read_text(encoding='utf-8'), encoding='utf-8')
