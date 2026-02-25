# NugetUtil

NugetUtil is a small CLI that discovers packable `.csproj` files, builds them with `dotnet build`, regenerates `.nuspec` files, packs with `dotnet pack`, and optionally pushes packages with `dotnet nuget push`.

## What it does

Given a repo root, NugetUtil will:

1. Discover `*.csproj` recursively
2. Identify package projects (`PackageId` + `Version` + not `IsPackable=false`)
3. Build each package project with `dotnet build -c <Configuration>`
4. Regenerate `<PackageId>.nuspec` in each package project folder
5. Pack with `dotnet pack` (from nuspec)
6. Optionally push with `dotnet nuget push`

By default, NugetUtil computes filesystem fingerprints and only regenerates/packs packages that changed.

Nuspec mode is the default behavior and is required when a package project directly references non-packable projects.

## Requirements

- .NET SDK installed (`dotnet` on PATH)

## Usage

```bash
nugetutil "<rootPath>" [options]
```

Examples:

```bash
nugetutil "C:\Dev\myproject" -push
```

```bash
nugetutil "C:\Dev\myproject" -push -source "MyFeed"
```

```bash
nugetutil "C:\Dev\myproject" -auto-bump -bump-level patch
```

```bash
nugetutil "C:\Dev\myproject" -force
```

## Parameters

- `-push`
  - Push generated `.nupkg` files after packing.
  - Default: `false`.

- `-source "<name>"`
  - NuGet source name configured on your machine (for `dotnet nuget push --source`).
  - Must also exist in config `sources` to resolve API key.
  - Default: `defaultSource` from config.

- `-configuration Release|Debug`
  - Build configuration for `dotnet build`.
  - Default: `Release`.

- `-output "<folder>"`
  - Output folder for generated `.nupkg` files.
  - Relative paths are resolved under `<rootPath>`.
  - Default: `artifacts\nuget` (or config override).

- `-skip-duplicate`
  - Adds `-SkipDuplicate` when pushing.
  - Default behavior comes from config (`behavior.skipDuplicate`, default `true`).

- `-verbose-build`
  - Prints full `dotnet build` output for each package.
  - Default: concise build output (`- Build: succeeded` or build errors).

- `-force`
  - Processes all discovered packages, ignoring fingerprint change detection.
  - Useful when you want to regenerate/repack everything.

- `-auto-bump`
  - Uses filesystem fingerprints to detect which packages need a version bump.
  - Bumps changed packages and dependent packages, then packs only bumped packages.

- `-bump-level patch|minor|major`
  - Version bump level used with `-auto-bump`.
  - Default: `patch`.

- `-whatif`
  - Dry-run mode. Commands are printed but not executed.

- `-yes`
  - Non-interactive push confirmation.

- `-include "<glob>"`
  - Repeatable include glob for project discovery.
  - If omitted, all discovered projects are considered (subject to excludes).

- `-exclude "<glob>"`
  - Repeatable exclude glob for project discovery.
  - Combined with config `behavior.excludeGlobs`.

## Config file

Location:

- `%APPDATA%\NugetUtil\config.json`

Auto-bump state file:

- `%APPDATA%\NugetUtil\state.json`

State is used for both default changed-package detection and `-auto-bump`.

Behavior summary:

- default: fingerprint-based changed packages only
- `-force`: all discovered packages
- `-auto-bump`: bump changed packages and dependent packages, then pack bumped packages

On first run, if missing, NugetUtil creates a starter config automatically.

Example:

```json
{
  "defaultSource": "MyFeed",
  "sources": {
    "MyFeed": {
      "apiKey": "AZURE_DEVOPS_PAT_OR_NUGET_KEY"
    }
  },
  "behavior": {
    "skipDuplicate": true,
    "outputFolder": "artifacts\\nuget",
    "excludeGlobs": [
      "**\\bin\\**",
      "**\\obj\\**",
      "**\\**Tests**\\**"
    ]
  }
}
```

Notes:

- API keys are stored in plain text by design.
- `-source` / `defaultSource` uses the NuGet source name configured on your machine.
- NugetUtil masks API keys in command logs/output.

## Nuspec generation notes

- Generates `<projectFolder>\<PackageId>.nuspec` from scratch every run.
- Uses package metadata from the package `.csproj`.
- Includes dependencies from package and referenced non-packable project package references.
- Resolves `$(PropertyName)` versions from project properties and `Directory.Build.props` up the directory tree.
- For internal package dependencies discovered in the same run, NugetUtil uses the latest discovered package version.
- Embeds non-packable referenced project outputs into `lib\<packageTfm>` via `<files>` entries.

## Auto bump behavior

- Tracks package input fingerprints in `%APPDATA%\NugetUtil\state.json`.
- Detects changes from filesystem content (no git integration required).
- Marks changed packages for bump.
- Propagates bumps to dependent packages.
- Updates `<Version>` in `.csproj`, regenerates nuspec, and packs bumped packages.

## Exit codes

- `0` success
- `2` invalid arguments/config
- `3` build failed
- `4` pack failed
- `5` push failed

The CLI prints:

- `Job finished successfully.` on success
- `Job failed with exit code: <code>` on failure

## Help

- `nugetutil`
- `nugetutil -h`
- `nugetutil --help`

Help/usage invocations exit with code `0`.
