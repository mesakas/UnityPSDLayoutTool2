# UnityPSDLayoutTool2

`UnityPSDLayoutTool2` is a maintained fork of **UnityPSDLayoutTool**.

This project is explicitly based on the original `UnityPSDLayoutTool` and adds compatibility fixes and workflow improvements for newer Unity versions.

## Unity Version Support

- Verified working on: **Unity 6000.3.7f1**
- The original plugin targets much older Unity versions; this fork adds fixes to run on current Unity.

## What Was Changed Compared to UnityPSDLayoutTool

The following changes were added on top of the original `UnityPSDLayoutTool`:

1. Updated API compatibility for modern Unity editor versions.
2. Fixed PSD Unicode string decoding so Chinese layer names/text import correctly.
3. Added Chinese-friendly font fallback strategy for text import.
4. Added deterministic render ordering to reduce angle-dependent layer overlap issues.
5. Added configurable output folder behavior for generated assets.
6. Added configurable prefab output mode:
   - default: prefab saved as a sibling of the generated output folder
   - optional: prefab saved inside the generated output folder
7. Renamed plugin folder and namespace for this fork:
   - Folder: `Assets/PSDLayoutTool2`
   - Namespace: `PsdLayoutTool2`

## Installation

Copy the folder below into your Unity project:

- `Assets/PSDLayoutTool2`

## Usage

1. Put a `.psd` file under your Unity project's `Assets` directory.
2. Select the PSD file in the Project window.
3. In Inspector, use **PSD Layout Tool 2** options and buttons.

Main options include:

- `Maximum Depth`
- `Pixels to Unity Units`
- `Use Unity UI`
- `Output Mode`
- `Output Folder Name`
- `Prefab Output`

Actions:

- `Export Layers as Textures`
- `Layout in Current Scene`
- `Generate Prefab`

## Special Tags (same as original behavior)

### Group Layer Tags

- `|Animation` : create sprite animation from child layers
- `|FPS=##` : set animation FPS (default 30)
- `|Button` : create button from tagged child layers

### Art Layer Tags

- `|Disabled`
- `|Highlighted`
- `|Pressed`
- `|Default`
- `|Enabled`
- `|Normal`
- `|Up`
- `|Text`

## License

MIT License. See [LICENSE.md](LICENSE.md).

## Credit

This project is based on the original **UnityPSDLayoutTool** and keeps the same MIT license model.
