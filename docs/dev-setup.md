# Dev Setup

## Prerequisites
- **.NET 8 SDK** (LTS). Verified working with `10.0.400`'s bundled 8.0.425 SDK — any 8.0.4xx
  SDK works, since the SDK version used to *drive* the build can be newer than the
  `net8.0`/`net8.0-windows10.0.19041.0` target frameworks the projects compile against.
- **Windows 10/11 SDK 10.0.19041.0 or later** — this machine has `10.0.26100.0` installed
  (under `C:\Program Files (x86)\Windows Kits\10`), which covers it.
- **Visual Studio 2026** (v18) for day-to-day editing/debugging. The **Windows App SDK C#**
  component (`Microsoft.VisualStudio.Component.WindowsAppSdkSupport.CSharp`) is installed on
  this machine as of 2026-09-16, giving proper WinUI/XAML editing support (IntelliSense, XAML
  hot reload, project templates). Note: this component is *not* required for the CLI build
  steps below, which work with just the `.NET Desktop Development` workload + the .NET SDK —
  confirmed by building before the component was installed.
- No MSIX/packaging tooling is required — the app is unpackaged (`WindowsPackageType=None`).

## Solution layout
See `research/02-implementation-plan.md` for the full rationale. Quick reference:
- `src/WinUIDesigner.Document` — pure .NET, no WinUI dependency.
- `src/WinUIDesigner.CodeGen` — pure .NET, Roslyn-based.
- `src/WinUIDesigner.Core` — WinUI-dependent (targets `net8.0-windows10.0.19041.0`).
- `src/WinUIDesigner.App` — the WinUI 3 unpackaged app shell.
- `tests/WinUIDesigner.Document.Tests`, `tests/WinUIDesigner.CodeGen.Tests` — MSTest.

## Building from the command line
The solution file is `WinUIDesigner.sln` (classic format — see note below on why, not
`.slnx`). Build the whole solution with an explicit `Platform` (the WinUI-targeting projects,
`Core` and `App`, don't support `AnyCPU`):

```
dotnet build WinUIDesigner.sln -p:Configuration=Debug -p:Platform=x64
```

Individual projects also build standalone the same way:

```
dotnet build src/WinUIDesigner.Document/WinUIDesigner.Document.csproj
dotnet build src/WinUIDesigner.CodeGen/WinUIDesigner.CodeGen.csproj
dotnet build src/WinUIDesigner.Core/WinUIDesigner.Core.csproj -p:Platform=x64
dotnet build src/WinUIDesigner.App/WinUIDesigner.App.csproj -p:Platform=x64
```

Test projects build/run normally as part of the solution (no platform required):

```
dotnet test tests/WinUIDesigner.Document.Tests/WinUIDesigner.Document.Tests.csproj
dotnet test tests/WinUIDesigner.CodeGen.Tests/WinUIDesigner.CodeGen.Tests.csproj
```

**Note on `.sln` vs `.slnx`**: `dotnet new sln` on this SDK defaults to the newer `.slnx`
format, which we used initially. Opening that in VS2026 produced project-configuration
errors ("specifies a project configuration ... that does not exist") for `Core` and `App`,
because the minimal `.slnx` didn't carry per-project platform mappings the way `.sln` does —
same root cause as the CLI's `MSB4126` error building the `.slnx` at the solution level.
Regenerated as classic `.sln` (`dotnet new sln --format sln`), which auto-populates the
`ProjectConfigurationPlatforms` mappings correctly and fixed both the VS error and the CLI
solution-level build.

## Running the app
```
dotnet build src/WinUIDesigner.App/WinUIDesigner.App.csproj -p:Platform=x64
./src/WinUIDesigner.App/bin/x64/Debug/net8.0-windows10.0.19041.0/WinUIDesigner.App.exe
```
(`dotnet run` also works from inside `src/WinUIDesigner.App` once `Platform` is set, but the
explicit build + launch above is what was verified during M0.)

## Package versions pinned so far
Picked as the latest stable (non-preview) NuGet versions as of 2026-09-16:
- `Microsoft.WindowsAppSDK` 2.3.1
- `Microsoft.Windows.SDK.BuildTools` 10.0.28000.2705
- `Microsoft.CodeAnalysis.CSharp` 5.9.0
- `Microsoft.NET.Test.Sdk` 18.10.1
- `MSTest.TestAdapter` / `MSTest.TestFramework` 4.4.0
