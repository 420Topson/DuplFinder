# DuplFinder

DuplFinder is a .NET 8 command-line tool that scans a folder or drive, records file hashes in a SQLite database, and reports groups of duplicate files.

Safety note: DuplFinder only reports duplicates. It never deletes, moves, or modifies your files.

## Install

Install the .NET 8 SDK, then restore and build the project:

```powershell
dotnet restore
dotnet build -c Release
```

Run it from the repository:

```powershell
dotnet run -c Release -- scan "D:\" --db duplicates.db
```

Or publish a single Windows executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## Usage

```text
DuplicateFinder, .NET 8, SQLite, SHA-256

Commands:
  scan <path> [options]
  duplicates [options]
  stats [options]
  clean-db [options]
```

### Scan

```powershell
dotnet run -c Release -- scan "D:\" --db duplicates.db --threads auto
```

Useful scan options:

```text
--db <file>                      SQLite database path, default duplicates.db
--threads auto|1|2|4|8           Hashing worker count, default max(1, CPU-1)
--low-resource                   Smaller queues and conservative defaults
--batch-size <n>                 SQLite records per transaction
--channel-capacity <n>           Work queue capacity
--buffer-size <512KB|1MB|...>    File read buffer size
--large-file-parallelism <n>     Parallel hashing limit for large files
--follow-reparse-points          Follow symlinks/junctions; off by default
--record-skipped                 Store skipped files/directories in the DB
--all-files                      Scan every file extension instead of the default whitelist
--include-ext <.pdf,.jpg>        Custom extension whitelist; can be repeated
--exclude-ext <.tmp,.bak>        Extensions to skip; can be repeated and takes priority
```

By default, `scan` uses a document/media extension whitelist and skips common technical folders such as `.git`, `node_modules`, Windows system paths, temp/cache folders, and reparse points. Use `--all-files` to disable the extension whitelist, or `--include-ext` to replace it with your own list. `--exclude-ext` always wins.

### Duplicates

Print duplicate groups from an existing database:

```powershell
dotnet run -c Release -- duplicates --db duplicates.db
```

Filter by minimum file size and export CSV:

```powershell
dotnet run -c Release -- duplicates --db duplicates.db --min-size 1MB --export duplicates.csv
```

### Stats

Show database totals and potential savings:

```powershell
dotnet run -c Release -- stats --db duplicates.db
```

### Clean DB

Remove database rows for files that no longer exist on disk:

```powershell
dotnet run -c Release -- clean-db --db duplicates.db --batch-size 1000
```

`duplicates`, `stats`, and `clean-db` require the database file to already exist. Run `scan` first or pass `--db` to an existing database.

## Examples

Low-resource scan:

```powershell
dotnet run -c Release -- scan "D:\" --db duplicates.db --low-resource --threads 2 --batch-size 500 --channel-capacity 1000 --large-file-parallelism 1
```

Only scan selected extensions:

```powershell
dotnet run -c Release -- scan "D:\Photos" --db photos.db --include-ext .jpg,.jpeg,.png,.heic
```

Scan all extensions except archives and backup files:

```powershell
dotnet run -c Release -- scan "D:\Archive" --db archive.db --all-files --exclude-ext .zip,.7z,.bak
```
