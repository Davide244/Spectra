# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Spectra Engine is a high-performance, cross-platform game engine in C#/.NET 10.0. It combines scene-graph and BSP tree structures for rendering/spatial management, uses SILK.NET with a custom shader language for multi-pipeline compilation (Vulkan, DirectX, OpenGL), and targets an editor inspired by HAMMER and Roblox Studio.

## Build Commands

```bash
dotnet build                          # Build the solution
dotnet build -c Release               # Release build
dotnet publish -c Release -r <rid>    # Publish (e.g. win-x64, linux-x64)
```

Solution file: `Spectra.slnx` (modern text-based format, requires VS 2024+ or recent `dotnet` SDK).

## Project Structure

- **`SpectraEngine.Core/`** — Core engine library (`SpectraEngine.Core` namespace). Currently contains foundational types like `EngineInfo` with engine and asset format versioning.

## Key Constraints

- **AOT compilation**: The engine targets Ahead-of-Time compilation for performance and platform compliance (consoles, Android). All code must be AOT-compatible — avoid reflection-heavy patterns, `dynamic`, unconstrained generics over reference types, and runtime code generation.
- **Nullable reference types** are enabled — all code must be null-safe.
- **No external dependencies yet** — the project is in early development.
