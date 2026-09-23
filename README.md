# FabWinUI Designer

A standalone visual form designer for WinUI 3 — a WinForms/WPF-designer-style tool (toolbox,
drag-to-place, property grid, live XAML source view) for a platform that doesn't ship one of
its own.

![FabWinUI Designer screenshot](docs/screenshot.png)

## Goal

WinUI 3 has no built-in visual designer comparable to the WinForms or WPF ones — Visual Studio's
XAML editor is text-first, with only a passive preview. This project is a "simple," standalone
app to place controls, edit their properties, and generate/round-trip real, hand-editable
`.xaml` files, in the spirit of those older designers.

## What it does

- Renders a real, live WinUI 3 tree on the design surface (via `XamlReader.Load`), not a drawn
  approximation.
- Round-trips genuine hand-editable `.xaml` files (`XDocument`-based) — no proprietary project
  format.
- Click-to-add toolbox, drag-to-move, 8-handle resize, and a reflection-driven property grid,
  all backed by an in-app undo/redo stack.
- A syntax-highlighted XAML source view sits alongside the design surface: edit either one and
  the other follows (typed XAML is applied after a short pause), with inline error reporting for
  invalid XAML and a one-click reformat.
- Event wiring: name a handler in the Events tab and a matching stub is generated into the
  page's `.xaml.cs` code-behind on save (Roslyn-based).
- A Visual Studio-style shell: main menu and toolbar, an Explorer panel for the opened folder, and
  a status bar.

## What's supported

| Area | Support |
|---|---|
| Controls | `Button`, `TextBlock`, `TextBox`, `CheckBox`, `ComboBox`, `Image`, `StackPanel`, `Grid` |
| Layout | Canvas-based absolute positioning (`Canvas.Left`/`Top`, `Width`/`Height`) for v1, optional snap-to-grid. The root element stays in place (resizable from its right/bottom edges only) |
| Selection | Single-select, move, 8-handle resize |
| Properties | Property grid with a Properties and an Events tab, grouped by category or alphabetical. Per-control property/event lists come from JSON metadata files next to the app, since WinUI controls carry no design-time metadata to discover them automatically |
| XAML source | Editable, syntax-highlighted, applied after a typing pause; the caret selects the element it's in on the design surface; inline error reporting; Format Document |
| Code-behind | Event-handler stubs generated into the paired `.xaml.cs` on save |
| File I/O | New/Open/Save/Save As, Recent Files/Folders, Explorer panel (search, refresh, file-type icons, `.xaml.cs` nested under its `.xaml`). Asks to reload when the open file is changed by another program |
| Shell | Main menu (File/Edit/View/Help) with Ctrl+N/O/Shift+O/S/Z/Y shortcuts, toolbar, status bar (last action, file path, caret line/column), unsaved `*` in the window title |
| Undo/Redo | Whole-document snapshots |
| Not yet | Several files open in tabs (planned next — see below), multi-select, data binding UI, more controls |

## Requirements

- .NET 10 SDK
- Windows 10/11 SDK 10.0.19041.0 or later
- Visual Studio 2026 (recommended for day-to-day editing; not required for the CLI build below)

See [`docs/dev-setup.md`](docs/dev-setup.md) for full setup details, including the `.sln` vs
`.slnx` note and pinned package versions.

## Building and running

```
dotnet build FabWinUIDesigner.sln -p:Configuration=Debug -p:Platform=x64
dotnet build src/FabWinUIDesigner.App/FabWinUIDesigner.App.csproj -p:Platform=x64
./src/FabWinUIDesigner.App/bin/x64/Debug/net10.0-windows10.0.19041.0/FabWinUIDesigner.App.exe
```

```
dotnet test tests/FabWinUIDesigner.Document.Tests/FabWinUIDesigner.Document.Tests.csproj
dotnet test tests/FabWinUIDesigner.CodeGen.Tests/FabWinUIDesigner.CodeGen.Tests.csproj
```

## Project layout

- `src/FabWinUIDesigner.Document` — the XAML document model (`XDocument`-backed), no WinUI
  dependency.
- `src/FabWinUIDesigner.CodeGen` — Roslyn-based code generation, no WinUI dependency.
- `src/FabWinUIDesigner.Core` — WinUI-dependent designer logic (live-tree correlation, property
  grid schema, preview loading).
- `src/FabWinUIDesigner.App` — the WinUI 3 application shell.
- `tests/` — MSTest projects for the two pure-.NET layers.
- `samples/` — example `.xaml` files used by the app and its tests.

## Syntax highlighting

The XAML source view uses [TextControlBox-WinUI](https://github.com/FrozenAssassine/TextControlBox-WinUI)
(MIT) with a custom highlighting scheme matching Visual Studio's classic XML/XAML editor
palette.

## What's next

- Several XAML files open at the same time, one per tab.
- A Toolbox ready for many more controls (groups, search, alphabetical view), then a much wider
  control set.
- Multi-select, alignment guides, and a Grid row/column editor.

## License

MIT — see [`LICENSE`](LICENSE).
