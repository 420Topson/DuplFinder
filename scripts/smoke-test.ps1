param(
    [string]$ProjectPath = './DuplicateFinder.csproj'
)

$ErrorActionPreference = 'Stop'

function Run-Checked {
    param(
        [string]$Label,
        [scriptblock]$Command
    )

    Write-Host "\n===== $Label ====="
    $output = & $Command 2>&1 | Out-String
    Write-Host $output

    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE"
    }

    return $output
}

$baseTemp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$root = Join-Path $baseTemp ("df-smoke-" + [Guid]::NewGuid().ToString('N'))
$sub = Join-Path $root 'sub'
New-Item -ItemType Directory -Force -Path $sub | Out-Null

$db = Join-Path $root 'smoke.db'
$csv = Join-Path $root 'dups.csv'

# Two real duplicates.
[IO.File]::WriteAllBytes((Join-Path $root 'a.txt'), [Text.Encoding]::UTF8.GetBytes('same-payload'))
[IO.File]::WriteAllBytes((Join-Path $sub 'b.txt'), [Text.Encoding]::UTF8.GetBytes('same-payload'))

# Same size as the duplicate files, but different content. This must NOT become a duplicate.
[IO.File]::WriteAllBytes((Join-Path $root 'same_size_different.txt'), [Text.Encoding]::UTF8.GetBytes('diff-payload'))

# Excluded by default extension list. With --record-skipped it should be stored as skipped, not hashed as OK.
[IO.File]::WriteAllBytes((Join-Path $root 'ignored.exe'), [Text.Encoding]::UTF8.GetBytes('same-payload'))

$scan1 = Run-Checked 'scan #1' {
    dotnet run --project $ProjectPath -- scan $root --db $db --threads 2 --batch-size 2 --channel-capacity 5 --buffer-size 64KB --large-file-parallelism 1 --record-skipped
}

$dups1 = Run-Checked 'duplicates #1' {
    dotnet run --project $ProjectPath -- duplicates --db $db --export $csv
}

if ($dups1 -notmatch 'Copies:\s+2') {
    throw 'Smoke test expected exactly one duplicate group with Copies: 2.'
}

if ($dups1 -match 'same_size_different\.txt') {
    throw 'Smoke test failed: same-size different-content file was reported as a duplicate.'
}

if (-not (Test-Path $csv)) {
    throw 'Smoke test failed: duplicates CSV was not created.'
}

$csvText = Get-Content -Raw -Path $csv
if ($csvText -notmatch 'a\.txt' -or $csvText -notmatch 'b\.txt') {
    throw 'Smoke test failed: CSV does not contain both duplicate files.'
}

$scan2 = Run-Checked 'scan #2 cache check' {
    dotnet run --project $ProjectPath -- scan $root --db $db --threads 2 --batch-size 2 --channel-capacity 5 --buffer-size 64KB --large-file-parallelism 1 --record-skipped
}

if ($scan2 -notmatch 'Hashed:\s+0') {
    throw 'Smoke test failed: second scan should use cache and hash 0 files.'
}

$stats = Run-Checked 'stats' {
    dotnet run --project $ProjectPath -- stats --db $db
}

if ($stats -notmatch 'Duplicate groups:\s+1') {
    throw 'Smoke test failed: stats should report exactly one duplicate group.'
}

Remove-Item -Force (Join-Path $sub 'b.txt')

$clean = Run-Checked 'clean-db' {
    dotnet run --project $ProjectPath -- clean-db --db $db --batch-size 2
}

$dupsAfterClean = Run-Checked 'duplicates after clean-db' {
    dotnet run --project $ProjectPath -- duplicates --db $db
}

if ($dupsAfterClean -notmatch 'Nie znaleziono grup duplikatów') {
    throw 'Smoke test failed: duplicate group should disappear after deleting one copy and running clean-db.'
}

Write-Host "\nSmoke test passed. Test root: $root"
