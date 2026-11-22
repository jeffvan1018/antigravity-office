#!/usr/bin/env python3
"""Convert a 2-frame chromakey magenta (#FF00FF) character image into an aligned 2-cell transparent PNG sprite sheet."""

import argparse
from collections import deque
from pathlib import Path
from PIL import Image


def is_chromakey_magenta(r: int, g: int, b: int, tolerance: int) -> bool:
    """Return True if RGB is within tolerance of chromakey magenta (#FF00FF)."""
    dist = max(abs(255 - r), g, abs(255 - b))
    if dist <= tolerance:
        return True
    magenta_dominance = min(r, b) - g
    return r >= 150 and b >= 150 and magenta_dominance >= max(60, 140 - tolerance)


def remove_chromakey_background(
    img: Image.Image, tolerance: int = 65, despill: bool = True
) -> Image.Image:
    """Flood-fill from borders and clear enclosed chromakey magenta holes."""
    rgba = img.convert("RGBA")
    width, height = rgba.size
    pixels = rgba.load()

    visited = [[False] * width for _ in range(height)]
    queue: deque[tuple[int, int]] = deque()

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

    hole_tolerance = min(tolerance, 45)
    for y in range(height):
        for x in range(width):
            r, g, b, a = pixels[x, y]
            if a > 0 and is_chromakey_magenta(r, g, b, hole_tolerance):
                pixels[x, y] = (0, 0, 0, 0)

    if despill:
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
                    new_r = min(r, g + 20)
                    new_b = min(b, g + 20)
                    pixels[x, y] = (new_r, g, new_b, a)

    return rgba


def find_split_x(rgba: Image.Image) -> int:
    """Find the vertical split x-coordinate between the two character frames near center."""
    width, height = rgba.size
    pixels = rgba.load()
    mid = width // 2
    search_lo = max(1, int(width * 0.25))
    search_hi = min(width - 1, int(width * 0.75))

    best_x = mid
    best_count = height + 1
    for x in range(search_lo, search_hi):
        opaque_count = sum(1 for y in range(height) if pixels[x, y][3] > 0)
        if opaque_count < best_count or (
            opaque_count == best_count and abs(x - mid) < abs(best_x - mid)
        ):
            best_count = opaque_count
            best_x = x
    return best_x


def build_two_cell_sheet(
    rgba: Image.Image,
    char_height: int = 280,
    cell_width: int | None = None,
    cell_height: int | None = None,
    pad_x: int = 12,
    pad_top: int = 8,
) -> Image.Image:
    """Split into two frames, scale uniformly, center horizontally, and align feet to bottom."""
    split_x = find_split_x(rgba)
    left_half = rgba.crop((0, 0, split_x, rgba.height))
    right_half = rgba.crop((split_x, 0, rgba.width, rgba.height))

    frames = []
    for half in (left_half, right_half):
        bbox = half.getbbox()
        if not bbox:
            raise ValueError("One of the two sprite sheet cells is empty after keying.")
        frames.append(half.crop(bbox))

    max_src_h = max(f.height for f in frames)
    scale = char_height / max_src_h

    scaled_frames = []
    for f in frames:
        new_w = max(1, round(f.width * scale))
        new_h = max(1, round(f.height * scale))
        scaled_frames.append(f.resize((new_w, new_h), Image.Resampling.NEAREST))

    max_w = max(f.width for f in scaled_frames)
    max_h = max(f.height for f in scaled_frames)

    final_cell_w = cell_width if cell_width is not None else max_w + 2 * pad_x
    final_cell_h = cell_height if cell_height is not None else max_h + pad_top

    sheet = Image.new("RGBA", (final_cell_w * 2, final_cell_h), (0, 0, 0, 0))
    for idx, frame in enumerate(scaled_frames):
        x = idx * final_cell_w + (final_cell_w - frame.width) // 2
        y = final_cell_h - frame.height
        sheet.paste(frame, (x, y), frame)

    return sheet


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Convert a 2-frame chromakey character image into a transparent 2-cell sprite sheet."
    )
    parser.add_argument("input", type=Path, help="Path to source chromakey image")
    parser.add_argument("output", type=Path, help="Path to write transparent 2-cell PNG")
    parser.add_argument(
        "--char-height",
        type=int,
        default=280,
        help="Target character pixel height for 1376x768 rooms (default: 280)",
    )
    parser.add_argument(
        "--cell-width",
        type=int,
        default=None,
        help="Optional fixed width per cell in pixels (defaults to widest frame + padding)",
    )
    parser.add_argument(
        "--cell-height",
        type=int,
        default=None,
        help="Optional fixed height per cell in pixels (defaults to char-height + top padding)",
    )
    parser.add_argument(
        "--tolerance",
        type=int,
        default=65,
        help="Color distance tolerance from #FF00FF (default: 65)",
    )
    parser.add_argument(
        "--no-despill",
        action="store_true",
        help="Disable 1px edge magenta despill",
    )
    args = parser.parse_args()

    with Image.open(args.input) as img:
        keyed = remove_chromakey_background(
            img, tolerance=args.tolerance, despill=not args.no_despill
        )

    sheet = build_two_cell_sheet(
        keyed,
        char_height=args.char_height,
        cell_width=args.cell_width,
        cell_height=args.cell_height,
    )

    args.output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(args.output, format="PNG")


if __name__ == "__main__":
    main()
