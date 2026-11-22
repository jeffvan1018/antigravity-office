---
name: generate-sprite-sheet-from-character
description: >-
  Generate a 4-cell transparent 16-bit pixel art action sprite sheet (such as
  walk-up, walk-down, walk-right, walk-left, or sitting) from an existing
  character idle sprite in this workspace. Use this skill whenever the user
  asks to create an action or animation sprite sheet for an existing character.
---

# Generate Sprite Sheet From Character

Use this skill to generate a 4-cell action sprite sheet (`walk-up`, `walk-down`, `walk-right`, `walk-left`, `sitting`) conditioned on an existing character's idle sprite sheet so face, hair, outfit, palette, and room scale stay identical.

## Supported Actions

- **`walk-down`**: 4-frame front-facing walk cycle (`left step → passing → right step → passing`), facing straight toward the viewer.
- **`walk-up`**: 4-frame rear-facing walk cycle (`left step → passing → right step → passing`), showing the back of the character's head, hair, and outfit.
- **`walk-right`**: 4-frame right-facing side-profile walk cycle (`contact → passing → opposite contact → passing`), facing right.
- **`walk-left`**: 4-frame left-facing side-profile walk cycle (can also be produced by flipping an existing `walk-right` sheet cell-by-cell if the outfit is symmetrical).
- **`sitting`**: 4-frame seated idle cycle (e.g., subtle breathing, hand/arm shift, slight head nod) with knees bent in a seated posture and **no chair drawn** so the character sits cleanly on `150px`-tall chair sprites.

## Prompt Template

Always pass the character's idle sprite image as a visual reference to `image-generator` and use this exact 8-field prompt structure:

```text
style: 16-bit pixel art, chunky retro VGA pixel density matching the reference character sprite, crisp pixel edges, clean dark pixel outline
perspective: <action-specific elevation view: straight-on front for walk-down/sitting, straight-on rear for walk-up, flat side-profile facing right/left for walk-right/walk-left>, classic 2D point-and-click adventure game character sprite
layout: horizontal 4-frame sprite sheet arranged side by side in a single row with generous even spacing between frames, identical character scale and proportions across all 4 frames
subject: exact same character from the reference image (<character name/description>, exact same face, hair, skin tone, and outfit), full body head to toe
details: <4-frame action breakdown matching the requested action; soft even overhead interior lighting>
palette: exact same colors as the reference character sprite, avoid magenta or hot pink
background: solid flat pure chromakey magenta (#FF00FF) background, uniform flat color, no room, no floor, no wall, no chair, no cast ground shadow, no dividing lines, no grid borders, no frame numbers or labels
output: clean 4-frame horizontal sprite sheet only, all 4 frames aligned on the same horizontal ground line
```

## Workflow

1. **Locate & Inspect the Reference Character**:
   - Resolve the character's idle sprite sheet (check `creative/sprites/<character>-idle.png` or `creative/sprites/<character>.png`, or list `creative/sprites/` if needed).
   - Inspect it with `read_file` to note the exact hair, skin tone, outfit colors, and footwear.
2. **Assemble the Prompt**:
   - Fill in the 8-field prompt template above for the requested `<action>`, explicitly referencing `<reference-idle-path>` so `image-generator` preserves the character's identity.
3. **Generate the 4-Frame Strip**:
   - Invoke `image-generator` (`TypeName: "image-generator"`) with `16:9` aspect ratio and the reference image path.
4. **Process & Align with Pillow**:
   - Run [`make_action_sheet.py`](./scripts/make_action_sheet.py) with `--reference-idle` so the output automatically matches the character's standing height and cell height:
     ```bash
     # For walking actions (standing height):
     python3 .agents/skills/generate-sprite-sheet-from-character/scripts/make_action_sheet.py "<artifact-image-path>" "creative/sprites/<character>-<action>.png" --frames 4 --reference-idle "<reference-idle-path>"

     # For sitting (scales to ~0.74x standing height so head/torso match and fit 150px chairs):
     python3 .agents/skills/generate-sprite-sheet-from-character/scripts/make_action_sheet.py "<artifact-image-path>" "creative/sprites/<character>-sitting.png" --frames 4 --pose-type sitting --reference-idle "<reference-idle-path>"
     ```
5. **Verify**:
   - Inspect the final transparent 4-cell PNG with `read_file` and check its dimensions (`4 * cell_w × cell_h`). Confirm identical character identity, crisp 16-bit pixel edges, no magenta halo, clean 4-frame progression, horizontal centering in each cell, and feet aligned to the bottom edge.
