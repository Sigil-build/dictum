"""Renders dictum.ico and the PNG icons from the geometry of dictum.svg (Pillow; run from the repo root).

The outputs are committed: builds and CI need no Python. Run it again after changing the mark.
"""
from pathlib import Path

from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
BACKGROUND = (0x1E, 0x1F, 0x22, 255)
MARK = (0x7A, 0x6B, 0xFF, 255)
WAVE = [((7, 16), (10, 16), (10.5, 9), (13, 9)), ((13, 9), (15.5, 9), (16.5, 23), (19, 23)), ((19, 23), (21.5, 23), (22, 16), (25, 16))]


def bezier(p0, p1, p2, p3, steps=64):
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        yield (u**3 * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0],
               u**3 * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1])


def render(size):
    """Draws at 8x and scales down, for smooth edges at every size."""
    big = size * 8
    unit = big / 32
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((0, 0, big - 1, big - 1), radius=7 * unit, fill=BACKGROUND)

    def at(x, y):
        # The mark sits at 0.8 scale, offset 3.2 (dictum.svg's transform).
        return ((3.2 + 0.8 * x) * unit, (3.2 + 0.8 * y) * unit)

    stroke = 3 * 0.8 * unit
    centre = at(16, 16)
    radius = 13 * 0.8 * unit
    draw.ellipse((centre[0] - radius, centre[1] - radius, centre[0] + radius, centre[1] + radius), outline=MARK, width=round(stroke))
    # A round pen stamped densely along the curve: Pillow's polyline joints leave jagged ticks on a curve this tight.
    for x, y in (at(px, py) for segment in WAVE for px, py in bezier(*segment, steps=600)):
        draw.ellipse((x - stroke / 2, y - stroke / 2, x + stroke / 2, y + stroke / 2), fill=MARK)
    return image.resize((size, size), Image.Resampling.LANCZOS)


def main():
    render(512).save(HERE / "dictum-512.png")
    render(256).save(HERE / "dictum-256.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 256]
    largest = render(256)
    largest.save(HERE / "dictum.ico", sizes=[(s, s) for s in sizes], append_images=[render(s) for s in sizes[:-1]])
    print("wrote dictum.ico, dictum-256.png, dictum-512.png")


if __name__ == "__main__":
    main()
