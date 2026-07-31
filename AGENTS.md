# OmniRef Repository Guide

## Scope

This repository contains OmniRef, a Windows 10/11 x64 desktop application built
with C#, WPF, and .NET 10. It is an offline, single-user infinite canvas for
frequently used images, files, folders, text, URLs, and grouping frames.

These instructions apply to the whole repository.

## Required Environment

- Use the SDK pinned by `global.json`.
- Build and run on Windows; the application and integration tests target
  `net10.0-windows`.
- Keep source, XAML, Markdown, and configuration files encoded as UTF-8.
- Do not edit generated content under `bin/`, `obj/`, `TestResults/`,
  `artifacts/`, `.dotnet-home/`, or `.nuget/`.

## Architecture

- `src/OmniRef.Core`
  - Owns domain models, geometry, search, spatial indexing, undo history, and
    platform abstraction interfaces.
  - Must remain independent of WPF and Windows-only APIs.
- `src/OmniRef.Infrastructure.Windows`
  - Owns SQLite persistence and Windows integrations such as Shell thumbnails,
    global hotkeys, tray behavior, file opening, and single-instance IPC.
  - May depend on Core, but Core must never depend on this project.
- `src/OmniRef.App`
  - Owns WPF views, themes, localization, view models, preview caching, and the
    custom virtualized infinite canvas.
  - Keep UI state and commands in view models when practical; reserve
    code-behind for WPF input, window lifetime, and native integration wiring.
- `tests/OmniRef.Tests`
  - Contains deterministic unit and SQLite integration tests.

Keep the dependency direction `Core <- Infrastructure.Windows <- App`.
Package versions belong in `Directory.Packages.props`.

## Product and Data Invariants

- OmniRef is offline and has no telemetry. URL cards must not fetch remote
  metadata in the background.
- Deleting a card must never delete its referenced source file or folder.
- Folders are always external references. Images and ordinary files may be
  external references or embedded copies.
- Preserve both absolute and relative reference paths when they can be
  calculated, and keep missing-reference relinking functional.
- A single embedded asset is limited to 512 MB. Import and export BLOBs with
  streaming APIs rather than loading entire files into memory.
- Treat each `.omniref` file as a self-contained SQLite workspace. Preserve
  transactions, serialized background writes, `DELETE` journaling, migration
  backups, and read-only handling for newer schema versions.
- Schema or payload changes require a versioned migration and integration tests.
  Never silently overwrite an unsupported or corrupt workspace.
- Unsaved work and failed writes must remain recoverable in memory or in the
  recovery area.

## UI and Performance Rules

- Retain the native title bar and the current system/light/dark theme model.
- Preserve Chinese and English localization for user-visible text.
- Keep zoom clamped to 10%–800% and cursor anchored.
- Do not replace the spatially indexed virtualized canvas with one visual per
  workspace item. Inactive workspaces should not retain card visuals.
- Decode images near their displayed size, freeze reusable WPF image objects,
  and keep Shell thumbnail generation on its dedicated STA worker.
- Release large previews when the window is hidden and respect the existing
  memory and disk cache limits.
- Do not add WebView, EF Core, Generic Host, large theme frameworks, or another
  UI runtime without an explicit architectural decision.
- Prefer native Win32 integration over adding a heavyweight dependency for a
  small Windows feature.

## Coding Conventions

- Nullable reference types and warnings-as-errors are enabled repository-wide.
- Follow the existing C# formatting and naming conventions; run
  `dotnet format` rather than manually reformatting unrelated code.
- Keep asynchronous I/O asynchronous end-to-end. Do not block the WPF dispatcher
  with SQLite, file hashing, image decoding, or Shell work.
- Marshal observable UI state changes back to the dispatcher.
- Make cancellation and disposal explicit for background workers, timers,
  native handles, streams, and SQLite connections.
- Keep commands representing a drag or resize merged into a single undo step.
- Avoid unrelated refactors in focused fixes, and add tests for behavioral
  changes where the logic can be exercised without UI automation.

## Validation

Run these commands from the repository root before committing:

```powershell
dotnet restore OmniRef.slnx
dotnet build OmniRef.slnx -c Release --no-restore
dotnet test tests/OmniRef.Tests/OmniRef.Tests.csproj -c Release --no-build --no-restore
dotnet format OmniRef.slnx --verify-no-changes --no-restore
```

For UI or Windows integration changes, also smoke-test the affected flow on
Windows. Relevant checks include drag/drop, clipboard import, text editing,
zoom/pan/selection, autosave, tray restore, global hotkey, topmost mode,
multi-workspace restore, DPI scaling, themes, and both languages.

Use `scripts/publish.ps1` for a release artifact. The expected output is an
untrimmed, non-single-file, self-contained `win-x64` directory and ZIP containing
the documentation and `Samples/Welcome.omniref`.

## Commit Hygiene

- Review `git status` and the staged diff before committing.
- Do not commit local caches, recovery files, generated workspaces, logs, build
  output, or published artifacts.
- Document any intentionally unmet acceptance target, migration risk, or manual
  verification still required in the handoff.
