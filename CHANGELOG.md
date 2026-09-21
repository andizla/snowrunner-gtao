# Changelog

## [1.0.0] 2026-09-22

- Ground truth ambient occlusion replaces both SSAO generation shaders of the game (full and half resolution): 4 screen directions, 8 steps per side, 1.5 m radius.
- Installer with Install and Remove. It finds the game through Steam, Epic Games and the Microsoft Store, keeps a backup of `shader.pak`, checks every file it writes before it replaces anything, and refuses to touch a file it does not recognise.
