# DuplFinder

DuplFinder is a Windows-focused .NET 8 CLI tool for finding exact duplicate files.

It scans a selected drive or folder, stores file metadata and SHA-256 hashes in SQLite, and reports files that have the same size and the same full hash.

## Safety

DuplFinder only reports duplicates. It does not delete, move, rename, sort, upload, or modify user files.

For the MVP, duplicate detection is exact only:

1. same file size
2. same SHA-256 hash

File name, folder, and extension are not used to decide whether two files are duplicates.

## What This Is Not

DuplFinder is not:

- a file sorter
- an automatic cleanup or delete tool
- a forensic scanner
- a perceptual image/audio/video similarity tool
- a fuzzy matching or "DNA" fingerprint tool
- a cloud scanner
- a GUI app

## Requirements

- Windows
- .NET 8 SDK for building from source

The code targets Windows usage, but most of the implementation is plain .NET and remains reasonably portable where practical.

## Build

```powershell
dotnet restore
dotnet build .\DuplicateFinder.csproj -c Release
```

## Run

Use `scan` first to create and populate a SQLite database:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- scan "D:" --db duplicates.db
```

You can scan any drive or folder:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- scan "C:\Users\You\Pictures" --db pictures.db --profile nvme
dotnet run --project .\DuplicateFinder.csproj -- scan ".\SomeFolder" --db local.db
```

Then print duplicate groups:

```powershell
dotnet run --project .\DuplicateFinder.csproj -- duplicates --db duplicates.db
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
```

Performance profiles set scan defaults. Explicit CLI flags are applied after the profile, so they override it.

```powershell
dotnet run --project .\DuplicateFinder.csproj -- scan "D:" --db duplicates.db --profile hdd
dotnet run --project .\DuplicateFinder.csproj -- scan "D:" --db duplicates.db --profile sata-ssd
dotnet run --project .\DuplicateFinder.csproj -- scan "D:" --db duplicates.db --profile nvme
dotnet run --project .\DuplicateFinder.csproj -- scan "C:\Users\You\Pictures" --db pictures.db --profile nvme --threads 4
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

If the database path is wrong, the program exits with a clear error instead of silently creating an empty database.

## Duplicate Detection

The final source of truth is SQLite grouping by full hash:

```sql
GROUP BY size, hash
HAVING COUNT(*) > 1
```

The stored hash prefix is only a helper field for possible future optimization. It must never be used by itself to report duplicates.

## Filtering

The MVP scans a default whitelist of common document, image, video, and audio extensions. It skips common technical/system/cache extensions and skips reparse points unless `--follow-reparse-points` is used.

Custom filtering flags are not part of the current README because they are not part of the current MVP CLI.

## Validation

Run the same checks used by the project workflow:

```powershell
dotnet restore
dotnet build .\DuplicateFinder.csproj -c Release
.\scripts\smoke-test.ps1 -ProjectPath .\DuplicateFinder.csproj
```

GitHub Actions also builds, runs the smoke test, publishes a self-contained `win-x64` single-file executable, and uploads it as an artifact.
