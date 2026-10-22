# Build instructions

## Prerequisites

Install:

1. Visual Studio Community with the **.NET Desktop Development** workload.
2. The **.NET 8 SDK**.

The project uses the NuGet package:

- `Microsoft.Web.WebView2` version `1.0.3537.50`

Visual Studio restores this package automatically when the solution is opened.

## Build from source

1. Clone or download this repository.
2. Open `AutoLootPlusFilterCompanion.sln` in Visual Studio.
3. Allow NuGet package restore if prompted.
4. Set the build configuration to `Release`.
5. Select **Build > Build Solution**.

A normal build output is created under:

`bin\Release\net8.0-windows\`

## Publish the Nexus release build

The Nexus release is published as a **framework-dependent Windows x64 build**.

In Visual Studio:

1. Right-click the `AutoLootPlusFilterCompanion` project.
2. Select **Publish**.
3. Choose a **Folder** target.
4. Set:
   - Configuration: `Release`
   - Target framework: `net8.0-windows`
   - Deployment mode: `Framework-dependent`
   - Target runtime: `win-x64`
   - Produce single file: enabled
   - ReadyToRun: disabled
5. Publish.

Users of the framework-dependent build need the Microsoft .NET 8 Desktop Runtime x64 installed.

The application also requires the Microsoft Edge WebView2 Runtime.

## Source contents

The source required to reproduce the application consists of:

- `AutoLootPlusFilterCompanion.sln`
- `AutoLootPlusFilterCompanion.csproj`
- `Program.cs`
- `AutoLootPlusFilterCompanion.ico`
- `wwwroot/`

Build outputs, caches, executables, and WebView2 user-data folders are intentionally excluded from source control.
