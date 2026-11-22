---
name: generate-2-cell-sprite-sheet-from-character
description: >-
  Generate a 2-cell transparent 16-bit pixel art action sprite sheet for
  subtle or simple 2-frame actions (such as seated facing forward, seated facing
  backward, seated typing, or drink-coffee) from an existing character idle
  sprite in this workspace. Use this skill whenever the user asks to create a
  2-cell action or pose sprite sheet for an existing character.
---

# Generate 2-Cell Sprite Sheet From Character

Use this skill to generate a 2-cell action sprite sheet (`seated-front`, `seated-back`, `seated-typing`, `drink-coffee`, or other simple 2-frame poses) conditioned on an existing character's idle sprite sheet so face, hair, outfit, palette, and room scale stay identical.

## Supported Actions

- **`seated-front`**: 2-frame front-facing seated idle (`neutral seated posture → subtle breathing/hand shift`), knees bent in seated posture, **no chair drawn** so the character sits cleanly on `150px`-tall chair sprites.
- **`seated-back`**: 2-frame rear-facing seated idle (`neutral seated back view → subtle head/shoulder shift`), showing the back of head, hair, and outfit, **no chair drawn**.
- **`seated-typing`**: 2-frame seated typing pose (`left hand typing / right hand resting → right hand typing / left hand shifted`), hands raised at desk typing height, **no chair or desk drawn**.
- **`drink-coffee`**: 2-frame coffee-sipping pose (`holding coffee mug at chest/waist → raising coffee mug to sip`), matching the character's standing height (or seated if specified).

## Prompt Template

Always pass the character's idle sprite image as a visual reference to `image-generator` and use this exact 8-field prompt structure:

```text
style: 16-bit pixel art, chunky retro VGA pixel density matching the reference character sprite, crisp pixel edges, clean dark pixel outline
perspective: <action-specific elevation view: straight-on front for seated-front/drink-coffee, straight-on rear for seated-back, front or slight profile for seated-typing>, classic 2D point-and-click adventure game character sprite
layout: horizontal 2-frame sprite sheet arranged side by side with generous spacing between frames, identical character scale and proportions in both frames
subject: exact same character from the reference image (<character name/description>, exact same face, hair, skin tone, and outfit), full body head to toe
details: <2-frame action breakdown matching the requested action; soft even overhead interior lighting>
palette: exact same colors as the reference character sprite, avoid magenta or hot pink
background: solid flat pure chromakey magenta (#FF00FF) background, uniform flat color, no room, no floor, no wall, no chair, no desk, no cast ground shadow, no dividing lines, no grid borders, no frame numbers or labels
output: clean 2-frame horizontal sprite sheet only, both frames aligned on the same horizontal ground line
```

## Workflow

1. **Locate & Inspect the Reference Character**:
   - Resolve the character's idle sprite sheet (check `creative/sprites/<character>-idle.png` or `creative/sprites/<character>.png`, or list `creative/sprites/` if needed).
   - Inspect it with `read_file` to note the exact hair, skin tone, outfit colors, and footwear.
2. **Assemble the Prompt**:
   - Fill in the 8-field prompt template above for the requested `<action>`, explicitly referencing `<reference-idle-path>` so `image-generator` preserves the character's identity.
3. **Generate the 2-Frame Strip**:
   - Invoke `image-generator` (`TypeName: "image-generator"`) with `4:3` aspect ratio and the reference image path.
4. **Process & Align with Pillow**:
   - Run [`make_2cell_sheet.py`](./scripts/make_2cell_sheet.py) with `--frames 2` and `--reference-idle` so the output automatically matches the character's cell height and scale:
     ```bash
     # For standing 2-cell actions (e.g., drink-coffee):
     python3 .agents/skills/generate-2-cell-sprite-sheet-from-character/scripts/make_2cell_sheet.py "<artifact-image-path>" "creative/sprites/<character>-<action>.png" --frames 2 --pose-type standing --reference-idle "<reference-idle-path>"

     # For seated 2-cell actions (e.g., seated-front, seated-back, seated-typing):
     python3 .agents/skills/generate-2-cell-sprite-sheet-from-character/scripts/make_2cell_sheet.py "<artifact-image-path>" "creative/sprites/<character>-<action>.png" --frames 2 --pose-type sitting --reference-idle "<reference-idle-path>"
     ```
5. **Verify**:
   - Inspect the final transparent 2-cell PNG with `read_file` and check its dimensions (`2 * cell_w × cell_h`). Confirm identical character identity, crisp 16-bit pixel edges, no magenta halo, clean 2-frame pose difference, horizontal centering in each cell, and feet aligned to the bottom edge.
