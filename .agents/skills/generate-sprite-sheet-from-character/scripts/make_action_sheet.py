#!/usr/bin/env python3
"""Convert an N-frame chromakey magenta (#FF00FF) character action image into an aligned transparent sprite sheet."""

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


def find_split_xs(rgba: Image.Image, num_frames: int) -> list[int]:
    """Find N-1 vertical split x-coordinates separating N frames."""
    width, height = rgba.size
    pixels = rgba.load()
    splits: list[int] = []
    window = max(4, int(width / (num_frames * 3)))

    for i in range(1, num_frames):
        target = round(width * i / num_frames)
        lo = max(1, target - window)
        hi = min(width - 1, target + window)
        if splits:
            lo = max(lo, splits[-1] + 1)

        best_x = target
        best_count = height + 1
        for x in range(lo, hi):
            opaque_count = sum(1 for y in range(height) if pixels[x, y][3] > 0)
            if opaque_count < best_count or (
                opaque_count == best_count and abs(x - target) < abs(best_x - target)
            ):
                best_count = opaque_count
                best_x = x
        splits.append(best_x)

    return splits


def measure_reference_idle(ref_path: Path) -> tuple[int, int, int]:
    """Return (cell_w, cell_h, char_height) from a 2-cell idle sprite sheet."""
    with Image.open(ref_path) as ref:
        rgba = ref.convert("RGBA")
        cell_w = rgba.width // 2
        cell_h = rgba.height
        left = rgba.crop((0, 0, cell_w, cell_h)).getbbox()
        right = rgba.crop((cell_w, 0, rgba.width, cell_h)).getbbox()
        heights = [b[3] - b[1] for b in (left, right) if b is not None]
        char_h = max(heights) if heights else cell_h - 8
        return cell_w, cell_h, char_h


def build_action_sheet(
    rgba: Image.Image,
    num_frames: int = 4,
    target_char_height: int = 280,
    cell_width: int | None = None,
    cell_height: int | None = None,
    pad_x: int = 12,
    pad_top: int = 8,
) -> Image.Image:
    """Split into N frames, scale uniformly, center horizontally, and align feet to bottom."""
    splits = find_split_xs(rgba, num_frames)
    bounds = [0, *splits, rgba.width]

    frames: list[Image.Image] = []
    for i in range(num_frames):
        half = rgba.crop((bounds[i], 0, bounds[i + 1], rgba.height))
        bbox = half.getbbox()
        if not bbox:
            raise ValueError(f"Frame {i + 1} of {num_frames} is empty after keying.")
        frames.append(half.crop(bbox))

    max_src_h = max(f.height for f in frames)
    scale = target_char_height / max_src_h

    scaled_frames = [
        f.resize(
            (max(1, round(f.width * scale)), max(1, round(f.height * scale))),
            Image.Resampling.NEAREST,
        )
        for f in frames
    ]

    max_w = max(f.width for f in scaled_frames)
    max_h = max(f.height for f in scaled_frames)

    final_cell_w = cell_width if cell_width is not None else max_w + 2 * pad_x
    final_cell_h = cell_height if cell_height is not None else max_h + pad_top

    sheet = Image.new("RGBA", (final_cell_w * num_frames, final_cell_h), (0, 0, 0, 0))
    for idx, frame in enumerate(scaled_frames):
        x = idx * final_cell_w + (final_cell_w - frame.width) // 2
        y = final_cell_h - frame.height
        sheet.paste(frame, (x, y), frame)

    return sheet


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Convert an N-frame chromakey character action image into a transparent sprite sheet."
    )
    parser.add_argument("input", type=Path, help="Path to source chromakey image")
    parser.add_argument("output", type=Path, help="Path to write transparent PNG sheet")
    parser.add_argument(
        "--frames",
        type=int,
        default=4,
        help="Number of horizontal frames in the sprite sheet (default: 4)",
    )
    parser.add_argument(
        "--reference-idle",
        type=Path,
        default=None,
        help="Optional 2-cell idle sprite sheet to match cell height and character scale",
    )
    parser.add_argument(
        "--pose-type",
        choices=("standing", "sitting"),
        default="standing",
        help="Pose scaling mode: 'standing' matches reference height; 'sitting' scales to ~0.74x standing height",
    )
    parser.add_argument(
        "--char-height",
        type=int,
        default=None,
        help="Override target character pixel height (defaults to reference idle height or 280)",
    )
    parser.add_argument(
        "--cell-width",
        type=int,
        default=None,
        help="Optional fixed width per cell in pixels",
    )
    parser.add_argument(
        "--cell-height",
        type=int,
        default=None,
        help="Optional fixed height per cell in pixels (defaults to reference cell height)",
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

    ref_cell_w = None
    ref_cell_h = None
    ref_char_h = 280
    if args.reference_idle is not None:
        ref_cell_w, ref_cell_h, ref_char_h = measure_reference_idle(args.reference_idle)

    if args.char_height is not None:
        target_h = args.char_height
    elif args.pose_type == "sitting":
        target_h = round(ref_char_h * 0.74)
    else:
        target_h = ref_char_h

    cell_h = args.cell_height if args.cell_height is not None else ref_cell_h

    with Image.open(args.input) as img:
        keyed = remove_chromakey_background(
            img, tolerance=args.tolerance, despill=not args.no_despill
        )

    sheet = build_action_sheet(
        keyed,
        num_frames=args.frames,
        target_char_height=target_h,
        cell_width=args.cell_width,
        cell_height=cell_h,
    )

    args.output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(args.output, format="PNG")


if __name__ == "__main__":
    main()
