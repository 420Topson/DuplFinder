# DuplFinder

DuplFinder is a Windows-focused .NET 8 tool for finding exact duplicate files.

It scans a selected drive or folder, stores file metadata and SHA-256 hashes in SQLite, and reports files that have the same size and the same full hash.

The CLI remains the core engine. The optional v1.1.0 GUI MVP is a WPF wrapper that builds and runs the same CLI commands with safer workflow gating and a visible command preview.

## Safety

DuplFinder starts as a report-only exact duplicate finder. The normal review flow is explicit and staged:

```text
scan
-> duplicates
-> prestage-report
-> export stage-plan.json
-> apply-stage-plan --dry-run
-> apply-stage-plan --quarantine
-> undo-quarantine --dry-run / --restore
-> purge-quarantine --dry-run / --confirm-purge
```

`prestage-report` is read-only and does not move or delete files. `stage-plan.json` is only a plan exported from the local HTML report.

`apply-stage-plan` defaults to dry-run. Quarantine mode moves only selected `stage_paths` from the stage plan into a DuplFinder quarantine session. `KEEP` files are never moved or modified.

`undo-quarantine` can restore quarantined files using the quarantine manifest.

`purge-quarantine` only deletes files already inside the quarantine session and listed in the manifest. It never deletes `original_path`, `keep_path`, or any original duplicate file location.

Plan and manifest files are treated as untrusted input. Destructive actions validate schema, paths, quarantine containment, size, and SHA-256 before acting. Users should inspect dry-run output before quarantine or purge.

For the MVP, duplicate detection is exact only:

1. same file size
2. same SHA-256 hash

File name, folder, and extension are not used to decide whether two files are duplicates.

## What This Is Not

DuplFinder is not:

- a file sorter
- an automatic cleanup or delete tool
- an antivirus or malware scanner
- a forensic scanner
- a perceptual image/audio/video similarity tool
- a fuzzy matching or "DNA" fingerprint tool
- a cloud scanner
- a replacement for the CLI engine

## Requirements

- Windows
- .NET 8 SDK for building from source

The code targets Windows usage, but most of the implementation is plain .NET and remains reasonably portable where practical.

## Build

```powershell
dotnet restore
dotnet build .\DuplicateFinder.csproj -c Release
dotnet build .\DuplFinder.Gui\DuplFinder.Gui.csproj -c Release
```

The GUI project is:

```text
.\DuplFinder.Gui\DuplFinder.Gui.csproj
```

It targets `net8.0-windows` and uses WPF.

## Run

Use `scan` first to create and populate a SQLite database:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- scan "D:" --db duplicates.db
```

The GUI can be launched from its build output:

```powershell
.\DuplFinder.Gui\bin\Release\net8.0-windows\DuplFinder.Gui.exe
```

The GUI does not replace the CLI. It shows a read-only output pane, drive/folder selection, file type checkboxes, workflow controls, and the exact command preview. Commands run only after clicking `Run`, and safety-sensitive actions require confirmation.

You can scan any drive or folder:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- scan "C:\Users\You\Pictures" --db pictures.db --profile nvme
dotnet run --project .\DuplicateFinder.csproj -- scan ".\SomeFolder" --db local.db
```

Then print duplicate groups:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- duplicates --db duplicates.db
```

Optionally generate a local review report before any future staging workflow:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- prestage-report --db duplicates.db --out prestage-report.html
```

After exporting `stage-plan.json` from the report, inspect the dry-run before moving anything:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- apply-stage-plan --plan stage-plan.json --dry-run
dotnet run --project .\DuplicateFinder.csproj -- apply-stage-plan --plan stage-plan.json --quarantine "D:\DuplFinder-Quarantine"
```

If needed, restore quarantined files with the manifest:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- undo-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --dry-run
dotnet run --project .\DuplicateFinder.csproj -- undo-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --restore
```

Only after review, purge validated files from the quarantine session:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- purge-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --dry-run
dotnet run --project .\DuplicateFinder.csproj -- purge-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --confirm-purge
```

## Commands

### `scan`

Scans a drive or folder, hashes allowed files, and writes results to SQLite.

```powershell
dotnet run --project .\DuplicateFinder.csproj -- scan <path> --db duplicates.db --profile sata-ssd
```

Useful options:

```text
--db <file>                      Database path, default duplicates.db
--profile hdd|sata-ssd|nvme      Performance profile, default sata-ssd
--threads auto|1|2|4|8           Worker count, default comes from profile
--low-resource                   Alias for --profile hdd; explicit flags still override defaults
--batch-size <n>                 SQLite records per transaction
--channel-capacity <n>           Work queue capacity
--buffer-size <512KB|1MB|...>    File read buffer size
--large-file-parallelism <n>     Limit parallel hashing for large files
--large-file-threshold <512MB>   Size where large-file parallelism limit applies
--follow-reparse-points          Follow symlinks/junctions; off by default
--record-skipped                 Store skipped files/directories in the DB
--include-ext .jpg,.png,.mp4     Scan only selected extensions
--include-no-extension           Include files with no extension; skipped by default
```

Performance profiles set scan defaults. Explicit CLI flags are applied after the profile, so they override it.

```powershell
dotnet run --project .\DuplicateFinder.csproj -- scan "D:" --db duplicates.db --profile hdd
dotnet run --project .\DuplicateFinder.csproj -- scan "D:" --db duplicates.db --profile sata-ssd
dotnet run --project .\DuplicateFinder.csproj -- scan "D:" --db duplicates.db --profile nvme
dotnet run --project .\DuplicateFinder.csproj -- scan "C:\Users\You\Pictures" --db pictures.db --profile nvme --threads 4
dotnet run --project .\DuplicateFinder.csproj -- scan "D:\Photos" --db photos.db --include-ext .jpg,.jpeg,.png --include-no-extension
```

Profiles:

```text
hdd       HDD / USB / older PCs / conservative I/O
sata-ssd  default balanced profile
nvme      NVMe / modern CPU / more RAM / aggressive queues and buffers
```

### `duplicates`

Reads an existing database and prints exact duplicate groups.

```powershell
dotnet run --project .\DuplicateFinder.csproj -- duplicates --db duplicates.db
dotnet run --project .\DuplicateFinder.csproj -- duplicates --db duplicates.db --min-size 1MB
dotnet run --project .\DuplicateFinder.csproj -- duplicates --db duplicates.db --min-size 1MB --export duplicates.csv
```

### `prestage-report`

Generates a self-contained dark themed HTML review report for exact duplicate groups.

```powershell
dotnet run --project .\DuplicateFinder.csproj -- prestage-report --db duplicates.db --out prestage-report.html
dotnet run --project .\DuplicateFinder.csproj -- prestage-report --db duplicates.db --out prestage-report.html --force
dotnet run --project .\DuplicateFinder.csproj -- prestage-report --db C:\TEST\duplicates.db --out C:\TEST\prestage-report.html
```

The report works from `file://`, uses no external CDN or network calls, and lets you choose one `KEEP` file plus optional `STAGE` candidates per exact duplicate group. It exports `stage-plan.json` using schema `duplfinder.stage-plan.v1`.

The command only writes the HTML report to `--out`. The report only downloads/exports a JSON stage plan. Neither the command nor the report moves, deletes, renames, sorts, uploads, or modifies user files.

Options:

```text
--db <file>   Existing database path, default duplicates.db
--out <html>  Output HTML report path, required
--force       Overwrite the output HTML report if it already exists
```

### `apply-stage-plan`

Reads `stage-plan.json`, validates it as untrusted input, and reports or quarantines selected stage candidates.

```powershell
dotnet run --project .\DuplicateFinder.csproj -- apply-stage-plan --plan stage-plan.json --dry-run
dotnet run --project .\DuplicateFinder.csproj -- apply-stage-plan --plan stage-plan.json --quarantine "D:\DuplFinder-Quarantine"
```

Default mode is dry-run/report only. Quarantine mode creates a unique `session-*` folder and writes `duplfinder-quarantine-manifest.json` for rollback.

Before moving any file, DuplFinder verifies:

- stage-plan schema is `duplfinder.stage-plan.v1`
- paths are fully-qualified local paths
- selected `stage_paths` are not the `keep_path`
- `KEEP` exists and still matches the group size + SHA-256
- each staged file still matches the group size + SHA-256
- reparse points, symlinks, and junctions are not followed

`KEEP` files are never moved or modified.

### `undo-quarantine`

Restores quarantined files using the quarantine manifest.

```powershell
dotnet run --project .\DuplicateFinder.csproj -- undo-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --dry-run
dotnet run --project .\DuplicateFinder.csproj -- undo-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --restore
```

Default mode is dry-run/report only. `--restore` is required to move files back.

Undo validates manifest schema, quarantine root/session containment, size, and SHA-256 before restoring. It never overwrites an existing original path and does not delete files.

### `purge-quarantine`

Purges quarantined files after review.

```powershell
dotnet run --project .\DuplicateFinder.csproj -- purge-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --dry-run
dotnet run --project .\DuplicateFinder.csproj -- purge-quarantine --manifest "D:\DuplFinder-Quarantine\session-...\duplfinder-quarantine-manifest.json" --confirm-purge
```

Default mode is dry-run/report only. `--confirm-purge` is required to delete anything.

Purge validates manifest schema, quarantine root/session containment, local-only paths, size, and SHA-256 immediately before deletion. It deletes only validated files already inside the quarantine session and listed in the manifest with status `moved`.

Purge never deletes:

- `original_path`
- `keep_path`
- original duplicate locations
- files not listed in the manifest
- folders or directories

### `stats`

Shows database totals and estimated potential saving.

```powershell
dotnet run --project .\DuplicateFinder.csproj -- stats --db duplicates.db
```

### `clean-db`

Removes database records for files that no longer exist on disk. It does not delete files.

```powershell
dotnet run --project .\DuplicateFinder.csproj -- clean-db --db duplicates.db
```

## Database Behavior

Only `scan` creates a database.

These read-oriented commands require the database file to already exist:

- `duplicates`
- `stats`
- `clean-db`
- `prestage-report`

If the database path is wrong, the program exits with a clear error instead of silently creating an empty database.

`apply-stage-plan`, `undo-quarantine`, and `purge-quarantine` do not use SQLite directly. They validate JSON plan/manifest files as untrusted input before acting.

## Duplicate Detection

The final source of truth is SQLite grouping by full hash:

```sql
GROUP BY size, hash
HAVING COUNT(*) > 1
```

The stored hash prefix is only a helper field for possible future optimization. It must never be used by itself to report duplicates.

## Filtering

DuplFinder scans a default whitelist of common document, image, video, and audio extensions. It skips common technical/system/cache extensions and skips reparse points unless `--follow-reparse-points` is used.

Files with no extension are skipped by default. Use `--include-no-extension` only when you intentionally want extensionless files included.

Use `--include-ext` to scan a specific comma-separated extension list:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- scan "D:\Photos" --db photos.db --include-ext .jpg,.jpeg,.png
dotnet run --project .\DuplicateFinder.csproj -- scan "D:\Mixed" --db mixed.db --include-ext .txt,.pdf --include-no-extension
```

Extension filtering only decides which files are scanned. Duplicate identity remains exact `size + full SHA-256` after files are hashed.

Windows/system folders are skipped by default, including root Windows folders such as `C:\Windows` and `<drive>:\Windows`, plus protected names such as `$Recycle.Bin` and `System Volume Information`.

## GUI MVP

The v1.1.0 GUI MVP is a Windows WPF wrapper around the CLI.

It provides:

- read-only console output pane
- drive list with multi-select checkboxes
- `Choose folder...` picker
- selected scan targets list with remove/clear controls
- scan profile selection
- real file type checkboxes backed by CLI options
- `Files with no extension` checkbox, unchecked by default
- real-time command preview
- minimum duplicate file size slider for `duplicates --min-size`
- workflow gating for DB, report, stage-plan, manifest, dry-run, quarantine, undo, and purge steps
- confirmation prompts for quarantine, restore, purge, and clean-db actions

If multiple scan targets are selected, the GUI runs sequential `scan` commands into the same DB. It does not run multiple scans in parallel into one DB.

The GUI uses `ProcessStartInfo.ArgumentList` and does not run commands through `cmd.exe /c`.

The GUI never changes duplicate identity, never implements fuzzy matching, and never directly deletes original duplicate files. The safe CLI quarantine workflow remains the source of truth.

## Validation

Run the same checks used by the project workflow:

```powershell
dotnet restore
dotnet build .\DuplicateFinder.csproj -c Release
dotnet build .\DuplFinder.Gui\DuplFinder.Gui.csproj -c Release
.\scripts\smoke-test.ps1 -ProjectPath .\DuplicateFinder.csproj
.\scripts\full-smoke-test.ps1 -ProjectPath .\DuplicateFinder.csproj -Configuration Release
```

GitHub Actions also builds, runs the smoke test, publishes a self-contained `win-x64` single-file executable, and uploads it as an artifact.
