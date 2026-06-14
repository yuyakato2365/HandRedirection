# VR Color Palette Setup

This system adds a touch-style color palette for objects placed in VR.
It supports both continuous gradient picking and fixed preset swatches.

## Components

- `ColorPaletteTarget`
  - Add to any object whose renderer color should be changed.
  - If `targetRenderers` is empty, renderers are collected from the object and children.

- `VRColorPalettePanel`
  - Add to an empty GameObject placed in front of the user.
  - At runtime it creates a small 3D panel with a hue/saturation gradient, a brightness slider, preset swatches, and colliders.
  - In the editor, it also builds the visible child objects so the palette can be placed on the desk before pressing Play.
  - Target buttons are shown at the top of the palette for objects such as `Usagi` and `teapot`.
  - Configure those buttons in `Target Button Entries`: set `Label` for the button text and drag the scene object into `Target Object`.
  - Assign `leftPinch` and `rightPinch` if auto-detection by hand object name is not enough.
  - Assign `target` manually, or leave `autoUseLatestTarget` on to use the newest object spawned by `ResultPlacer`.

- `VRColorPaletteSceneSetup`
  - Editor utility under `Tools > VR Color Palette > Create Or Update Scene Palette`.
  - Creates a persistent `VRColorPalettePanel` object in the active scene, parents it to a desk anchor when available, and links it to `ResultPlacer`.

## Interaction

- Touch a target button at the top to select which object will receive color changes.
- Touch the large gradient area to choose hue and saturation.
- Touch the vertical slider to adjust brightness.
- Touch a preset swatch to jump to a common color.
- Turn on `requirePinchToSelect` if color selection should require a pinch gesture instead of simple touch proximity.

- `ResultPlacer`
  - Newly placed placeholder objects automatically receive `ColorPaletteTarget`.
  - If `colorPalettePanel` is assigned, the new object becomes the active palette target.

## Recommended Scene Setup

1. Create an empty GameObject named `VRColorPalettePanel`.
2. Add `VRColorPalettePanel`.
3. Place it around 40-70 cm in front of the user, scaled at `1,1,1`.
4. Assign the existing left/right `PinchProvider` components.
5. In `ResultPlacer`, assign the panel to `colorPalettePanel`.
6. In `Target Button Entries`, add entries such as `Label = Usagi` with `Target Object = Usagi`, and `Label = Teapot` with `Target Object = teapot`.

Alternatively, run `Tools > VR Color Palette > Create Or Update Scene Palette` to create and link the panel automatically.

## Target Fields

- `Target` is the currently selected color target. It changes when a target button is touched.
- `Target Button Entries` is the list of buttons shown on the palette.
- Removing an entry from `Target Button Entries` removes that button. It will not be recreated during normal palette rebuilds.
- `Auto Discover Target Names` is only used by the editor setup menu to seed default entries when the list is empty.

## Assets

No required external assets. The gradient texture, panel, color swatches, colliders, and touch probes are generated at runtime from Unity primitives.

Optional assets:

- A dark semi-transparent material for `panelMaterial`.
- A simple white/emissive material for `selectedFrameMaterial`.
- A small translucent material for `touchProbeMaterial`.
