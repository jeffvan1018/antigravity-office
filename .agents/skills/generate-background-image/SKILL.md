---
name: generate-background-image
description: >-
  Generate 16-bit retro point-and-click adventure game background images using a
  standardized structured prompt. Use this skill whenever the user asks to
  create, generate, or design a background image, room, or environment scene
  for this workspace.
---

# Generate Background Image

Use this skill to assemble a structured image-generation prompt for 16-bit point-and-click adventure game backgrounds and generate the image via the `image-generator` subagent.

## Prompt Template

Always construct the prompt using the following exact 9-field structure. Preserve the fixed defaults verbatim, and merge user-provided details into `setting` and `floor` without dropping the required rules.

```text
style: 16-bit pixel art, crisp pixel edges, retro VGA color palette
perspective: flat straight-on elevation view, single-point perspective, classic 2D point-and-click adventure game background
setting: <room type and scene details>, no characters, no NPCs, no foreground furniture
floor: <walkable floor material>, wide unobstructed walkable floor along the lower third, edge to edge
back wall: <back wall surface>
wall fixtures: <wall-mounted shelves, signs, and flush equipment>
side elements: <left/right edges, doors, or windows>
lighting: <lighting mood and palette>
output: environment background only, clean walkable floor space
```

## Field Guidelines

- **Fixed fields (`style`, `perspective`, `output`)**: Keep these exact values unless the user explicitly asks to override them.
- **`setting`**: Start with the user's room type and scene description, then always append `no characters, no NPCs, no foreground furniture`.
- **`floor`**: Start with the user's floor material (or choose a material that fits the setting if omitted), then always append `wide unobstructed walkable floor along the lower third, edge to edge`.
- **`back wall`**, **`wall fixtures`**, **`side elements`**, **`lighting`**: Fill these in from the user's description, or infer cohesive details that match the `setting` while keeping the lower-third floor unobstructed and avoiding foreground furniture.

## Workflow

1. Gather the scene requirements from the user (at minimum the room type/setting and any specific floor, wall, fixture, side, or lighting details).
2. Fill in the 9-field prompt template above.
3. Invoke the `image-generator` subagent (`TypeName: "image-generator"`) with:
   - The assembled 9-field prompt as the brief.
   - The desired output file path (default to `creative/<scene-slug>.png` in the workspace if the user did not specify a path).
   - Any requested aspect ratio or dimensions (e.g., 16:9 widescreen if unspecified for a classic adventure backdrop, or whatever the user specifies).
4. Verify the generated image file exists at the returned path (and optionally inspect it with `read_file` to confirm it matches the clean-floor, empty-scene, 16-bit pixel art constraints).
