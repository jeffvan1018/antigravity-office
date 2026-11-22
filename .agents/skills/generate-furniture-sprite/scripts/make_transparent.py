#!/usr/bin/env python3
"""Convert a chromakey magenta (#FF00FF) sprite image to a transparent PNG."""

import argparse
from collections import deque
from pathlib import Path
from PIL import Image


def is_chromakey_magenta(r: int, g: int, b: int, tolerance: int) -> bool:
    """Return True if RGB is within tolerance of chromakey magenta (#FF00FF)."""
    # Check both Euclidean-style channel distance from (255, 0, 255)
    # and strong magenta dominance (high R & B, low G) to handle slight compression/shading.
    dist = max(abs(255 - r), g, abs(255 - b))
    if dist <= tolerance:
        return True
    magenta_dominance = min(r, b) - g
    return r >= 150 and b >= 150 and magenta_dominance >= max(60, 140 - tolerance)


def remove_chromakey_background(
    img: Image.Image, tolerance: int = 65, despill: bool = True
) -> Image.Image:
    """Flood-fill from image borders to remove chromakey magenta and optionally despill edges."""
    rgba = img.convert("RGBA")
    width, height = rgba.size
    pixels = rgba.load()

    visited = [[False] * width for _ in range(height)]
    queue: deque[tuple[int, int]] = deque()

    # Seed flood-fill from all border pixels that match chromakey magenta
    for x in range(width):
        for y in (0, height - 1):
            r, g, b, a = pixels[x, y]
            if a > 0 and is_chromakey_magenta(r, g, b, tolerance):
                visited[y][x] = True
                queue.append((x, y))
    for y in range(height):
        for x in (0, width - 1):
            if not visited[y][x]:
                r, g, b, a = pixels[x, y]
                if a > 0 and is_chromakey_magenta(r, g, b, tolerance):
                    visited[y][x] = True
                    queue.append((x, y))

    while queue:
        cx, cy = queue.popleft()
        pixels[cx, cy] = (0, 0, 0, 0)
        for nx, ny in ((cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1)):
            if 0 <= nx < width and 0 <= ny < height and not visited[ny][nx]:
                r, g, b, a = pixels[nx, ny]
                if a > 0 and is_chromakey_magenta(r, g, b, tolerance):
                    visited[ny][nx] = True
                    queue.append((nx, ny))

    # Also clear any enclosed chromakey magenta holes (e.g., gaps between table/chair legs)
    hole_tolerance = min(tolerance, 45)
    for y in range(height):
        for x in range(width):
            r, g, b, a = pixels[x, y]
            if a > 0 and is_chromakey_magenta(r, g, b, hole_tolerance):
                pixels[x, y] = (0, 0, 0, 0)

    if despill:
        # Remove faint magenta fringing on opaque pixels immediately adjacent to transparent pixels
        for y in range(height):
            for x in range(width):
                r, g, b, a = pixels[x, y]
                if a == 0:
                    continue
                touches_transparent = any(
                    0 <= nx < width and 0 <= ny < height and pixels[nx, ny][3] == 0
                    for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1))
                )
                if touches_transparent and min(r, b) > g + 25:
                    # Clamp magenta tint down toward neutral/green channel on 1px edge
                    new_r = min(r, g + 20)
                    new_b = min(b, g + 20)
                    pixels[x, y] = (new_r, g, new_b, a)

    return rgba


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Remove chromakey magenta background from a sprite image."
    )
    parser.add_argument("input", type=Path, help="Path to source chromakey image")
    parser.add_argument("output", type=Path, help="Path to write transparent PNG")
    parser.add_argument(
        "--tolerance",
        type=int,
        default=65,
        help="Color distance tolerance from #FF00FF (default: 65)",
    )
    parser.add_argument(
        "--trim",
        action="store_true",
        help="Trim transparent margins around the sprite (leaves 4px padding)",
    )
    parser.add_argument(
        "--no-despill",
        action="store_true",
        help="Disable 1px edge magenta despill",
    )
    parser.add_argument(
        "--target-height",
        type=int,
        default=None,
        help="Resize final sprite proportionally to this pixel height using NEAREST resampling",
    )
    parser.add_argument(
        "--target-width",
        type=int,
        default=None,
        help="Resize final sprite proportionally to this pixel width using NEAREST resampling",
    )
    args = parser.parse_args()

    with Image.open(args.input) as img:
        result = remove_chromakey_background(
            img, tolerance=args.tolerance, despill=not args.no_despill
        )

    if args.trim:
        bbox = result.getbbox()
        if bbox:
            pad = 4
            left = max(0, bbox[0] - pad)
            top = max(0, bbox[1] - pad)
            right = min(result.width, bbox[2] + pad)
            bottom = min(result.height, bbox[3] + pad)
            result = result.crop((left, top, right, bottom))

    if args.target_height is not None or args.target_width is not None:
        w, h = result.size
        if args.target_height is not None and args.target_width is not None:
            new_size = (args.target_width, args.target_height)
        elif args.target_height is not None:
            new_w = max(1, round(w * (args.target_height / h)))
            new_size = (new_w, args.target_height)
        else:
            new_h = max(1, round(h * (args.target_width / w)))
            new_size = (args.target_width, new_h)
        result = result.resize(new_size, Image.Resampling.NEAREST)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    result.save(args.output, format="PNG")


if __name__ == "__main__":
    main()
