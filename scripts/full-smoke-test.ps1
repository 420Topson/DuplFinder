param(
    [string]$ProjectPath = '.\DuplicateFinder.csproj',
    [string]$Configuration = 'Release',
    [string]$WorkDir,
    [switch]$KeepWorkDir,
    [switch]$VerboseOutput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:FilesCreated = 0
$script:ProfileResults = New-Object System.Collections.Generic.List[string]
$script:FirstScanResult = 'not run'
$script:SecondScanResult = 'not run'
$script:DuplicateValidationResult = 'not run'
$script:PrestageReportValidationResult = 'not run'
$script:CleanDbValidationResult = 'not run'
$script:MissingDbValidationResult = 'not run'
$script:EmptyFileBehavior = ''
$script:FailureMessage = ''
$script:ProjectFullPath = ''
$script:LogsRoot = ''

function Write-Step {
    param([string]$Message)

    Write-Host ''
    Write-Host "===== $Message ====="
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-DefaultParentWorkDir {
    if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        return $env:RUNNER_TEMP
    }

    $tempPath = [System.IO.Path]::GetTempPath()
    if (-not (Test-IsDefaultScanExcludedPath -Path $tempPath)) {
        return $tempPath
    }

    $localAppData = $env:LOCALAPPDATA
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    }

    if (-not [string]::IsNullOrWhiteSpace($localAppData)) {
        return (Join-Path $localAppData 'DuplFinderSmoke')
    }

    return $tempPath
}

function Test-IsDefaultScanExcludedPath {
    param([string]$Path)

    $normalized = $Path.Replace([System.IO.Path]::AltDirectorySeparatorChar, [System.IO.Path]::DirectorySeparatorChar).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    return $normalized.Contains('\AppData\Local\Temp', [StringComparison]::OrdinalIgnoreCase) -or
        $normalized.Contains('\AppData\Local\Microsoft\Windows', [StringComparison]::OrdinalIgnoreCase)
}

function New-TestFileBytes {
    param(
        [string]$Path,
        [byte[]]$Bytes
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    [System.IO.File]::WriteAllBytes($Path, $Bytes)
    $script:FilesCreated++
    return (Get-Item -LiteralPath $Path).FullName
}

function New-TestFile {
    param(
        [string]$Path,
        [string]$Content
    )

    $encoding = New-Object System.Text.UTF8Encoding -ArgumentList $false
    return New-TestFileBytes -Path $Path -Bytes $encoding.GetBytes($Content)
}

function Get-FileSha256 {
    param([string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Invoke-DuplFinder {
    param(
        [string[]]$CliArgs,
        [string]$LogName
    )

    $dotnetArgs = @(
        'run',
        '--project',
        $script:ProjectFullPath,
        '--configuration',
        $Configuration,
        '--'
    ) + $CliArgs

    $output = & dotnet @dotnetArgs 2>&1 | Out-String
    $exitCode = $LASTEXITCODE

    $logPath = Join-Path $script:LogsRoot $LogName
    $logText = @(
        "Command: dotnet $($dotnetArgs -join ' ')",
        "ExitCode: $exitCode",
        '',
        $output
    ) -join [Environment]::NewLine
    Set-Content -LiteralPath $logPath -Value $logText -Encoding UTF8

    if ($VerboseOutput -or $exitCode -ne 0) {
        Write-Host $output
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
        LogPath = $logPath
    }
}

function Read-CsvSafe {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    return @(Import-Csv -LiteralPath $Path)
}

function Test-PathInList {
    param(
        [string[]]$Paths,
        [string]$Path
    )

    foreach ($candidate in $Paths) {
        if ([string]::Equals($candidate, $Path, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-CsvContainsPath {
    param(
        [object[]]$Rows,
        [string]$Path
    )

    foreach ($row in $Rows) {
        if ([string]::Equals([string]$row.path, $Path, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Find-DuplicateGroup {
    param(
        [object[]]$Rows,
        [string[]]$ExpectedPaths,
        [int]$ExpectedCopies
    )

    $groups = @($Rows | Group-Object -Property group)
    foreach ($group in $groups) {
        $paths = @($group.Group | ForEach-Object { [string]$_.path })
        if ($paths.Count -ne $ExpectedCopies) {
            continue
        }

        $allPresent = $true
        foreach ($expectedPath in $ExpectedPaths) {
            if (-not (Test-PathInList -Paths $paths -Path $expectedPath)) {
                $allPresent = $false
                break
            }
        }

        if ($allPresent) {
            return $true
        }
    }

    return $false
}

function Assert-DuplicateGroup {
    param(
        [object[]]$Rows,
        [string[]]$ExpectedPaths,
        [int]$ExpectedCopies,
        [string]$Name
    )

    Assert-True `
        -Condition (Find-DuplicateGroup -Rows $Rows -ExpectedPaths $ExpectedPaths -ExpectedCopies $ExpectedCopies) `
        -Message "$Name duplicate group is missing or has the wrong copy count. Expected copies: $ExpectedCopies."
}

function Assert-NoDuplicatePath {
    param(
        [object[]]$Rows,
        [string]$Path,
        [string]$Message
    )

    Assert-True -Condition (-not (Test-CsvContainsPath -Rows $Rows -Path $Path)) -Message $Message
}

function Assert-EmptyFileBehavior {
    param(
        [object[]]$Rows,
        [string[]]$EmptyPaths
    )

    $hasEmptyGroup = Find-DuplicateGroup -Rows $Rows -ExpectedPaths $EmptyPaths -ExpectedCopies 2
    $emptyOneAppears = Test-CsvContainsPath -Rows $Rows -Path $EmptyPaths[0]
    $emptyTwoAppears = Test-CsvContainsPath -Rows $Rows -Path $EmptyPaths[1]

    Assert-True `
        -Condition (($hasEmptyGroup -and $emptyOneAppears -and $emptyTwoAppears) -or (-not $emptyOneAppears -and -not $emptyTwoAppears)) `
        -Message 'Empty files have inconsistent duplicate output.'

    $behavior = 'empty files are not reported as duplicates by current logic'
    if ($hasEmptyGroup) {
        $behavior = 'empty files are reported as an exact duplicate group'
    }

    if ([string]::IsNullOrWhiteSpace($script:EmptyFileBehavior)) {
        $script:EmptyFileBehavior = $behavior
    }
    else {
        Assert-True `
            -Condition ([string]::Equals($script:EmptyFileBehavior, $behavior, [StringComparison]::Ordinal)) `
            -Message 'Empty file behavior changed across profile scans.'
    }
}

function Validate-DuplicateRows {
    param(
        [object[]]$Rows,
        [hashtable]$CasePaths,
        [int]$AlphaCopies,
        [string]$Context
    )

    Assert-DuplicateGroup -Rows $Rows -ExpectedPaths $CasePaths.Alpha -ExpectedCopies $AlphaCopies -Name "$Context Alpha"
    Assert-DuplicateGroup -Rows $Rows -ExpectedPaths $CasePaths.Beta -ExpectedCopies 2 -Name "$Context Beta"
    Assert-DuplicateGroup -Rows $Rows -ExpectedPaths $CasePaths.Gamma -ExpectedCopies 2 -Name "$Context different-name-same-content"
    Assert-EmptyFileBehavior -Rows $Rows -EmptyPaths $CasePaths.Empty

    foreach ($path in $CasePaths.SameSizeDifferent) {
        Assert-NoDuplicatePath -Rows $Rows -Path $path -Message "$Context same-size-different-content file was reported as a duplicate: $path"
    }

    foreach ($path in $CasePaths.SameNameDifferent) {
        Assert-NoDuplicatePath -Rows $Rows -Path $path -Message "$Context same-name-different-content file was reported as a duplicate: $path"
    }

    Assert-NoDuplicatePath -Rows $Rows -Path $CasePaths.SkippedExe -Message "$Context skipped .exe file was reported as an OK duplicate."
}

function Assert-PrestageReportHtml {
    param(
        [string]$Path,
        [string]$KnownDuplicatePath
    )

    Assert-True -Condition (Test-Path -LiteralPath $Path) -Message "Prestage report HTML was not created: $Path"

    $html = Get-Content -LiteralPath $Path -Raw
    Assert-True -Condition ($html.Contains('duplfinder.stage-plan.v1', [StringComparison]::Ordinal)) -Message 'Prestage report is missing the stage-plan schema marker.'
    Assert-True -Condition ($html.Contains('Export stage-plan.json', [StringComparison]::Ordinal)) -Message 'Prestage report is missing the export button text.'
    Assert-True -Condition ($html.Contains('Files in duplicate groups', [StringComparison]::Ordinal)) -Message 'Prestage report is missing the files-in-duplicate-groups summary label.'
    Assert-True -Condition ($html.Contains('Redundant files', [StringComparison]::Ordinal)) -Message 'Prestage report is missing the redundant files summary label.'
    Assert-True -Condition ($html.Contains('This report does not move or delete files. It only exports a stage plan.', [StringComparison]::Ordinal)) -Message 'Prestage report is missing the no move/delete safety warning.'
    $jsonEscapedKnownPath = $KnownDuplicatePath.Replace('\', '\\')
    Assert-True -Condition (($html.Contains($KnownDuplicatePath, [StringComparison]::Ordinal)) -or ($html.Contains($jsonEscapedKnownPath, [StringComparison]::Ordinal))) -Message 'Prestage report is missing a known duplicate path from the generated dataset.'
    Assert-True -Condition (($html.Contains('#0f1115', [StringComparison]::Ordinal)) -or ($html.Contains('#171a21', [StringComparison]::Ordinal))) -Message 'Prestage report is missing expected dark theme color markers.'
    Assert-True -Condition (-not $html.Contains('https://', [StringComparison]::OrdinalIgnoreCase)) -Message 'Prestage report should not reference https:// resources.'
    Assert-True -Condition (-not $html.Contains('http://', [StringComparison]::OrdinalIgnoreCase)) -Message 'Prestage report should not reference http:// resources.'
    Assert-True -Condition (-not $html.Contains('<script src=', [StringComparison]::OrdinalIgnoreCase)) -Message 'Prestage report should not load external scripts.'
    Assert-True -Condition (-not [regex]::IsMatch($html, '<link\b', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) -Message 'Prestage report should not depend on external linked CSS/resources.'
    Assert-True -Condition (-not [regex]::IsMatch($html, '@import\b', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) -Message 'Prestage report should not use external CSS imports.'
}

function Get-FirstMetric {
    param(
        [string]$Text,
        [string]$Label
    )

    $match = [regex]::Match($Text, [regex]::Escape($Label) + ':\s+(\d+)')
    Assert-True -Condition $match.Success -Message "Could not find metric '$Label' in CLI output."
    return [int64]$match.Groups[1].Value
}

function Get-LastInteger {
    param([string]$Text)

    $matches = [regex]::Matches($Text, '\d+')
    Assert-True -Condition ($matches.Count -gt 0) -Message 'Could not find an integer in CLI output.'
    return [int64]$matches[$matches.Count - 1].Value
}

function Remove-GeneratedWorkspace {
    param(
        [string]$WorkspacePath,
        [string]$ParentPath
    )

    if ([string]::IsNullOrWhiteSpace($WorkspacePath) -or -not (Test-Path -LiteralPath $WorkspacePath)) {
        return
    }

    $workspaceInfo = Get-Item -LiteralPath $WorkspacePath
    $parentInfo = Get-Item -LiteralPath $ParentPath

    Assert-True -Condition $workspaceInfo.PSIsContainer -Message 'Refusing to remove a non-directory workspace.'
    Assert-True -Condition $workspaceInfo.Name.StartsWith('duplfinder-full-smoke-', [StringComparison]::Ordinal) -Message 'Refusing to remove a workspace without the generated smoke-test prefix.'
    Assert-True -Condition ([string]::Equals($workspaceInfo.Parent.FullName, $parentInfo.FullName, [StringComparison]::OrdinalIgnoreCase)) -Message 'Refusing to remove a workspace outside the selected parent WorkDir.'

    Remove-Item -LiteralPath $workspaceInfo.FullName -Recurse -Force
}

$projectPathCandidate = $ProjectPath
Assert-True -Condition (Test-Path -LiteralPath $projectPathCandidate) -Message "Project file not found: $ProjectPath"
$script:ProjectFullPath = (Resolve-Path -LiteralPath $projectPathCandidate).Path

$parentInput = $WorkDir
if ([string]::IsNullOrWhiteSpace($parentInput)) {
    $parentInput = Get-DefaultParentWorkDir
}

New-Item -ItemType Directory -Force -Path $parentInput | Out-Null
$parentPath = (Resolve-Path -LiteralPath $parentInput).Path
$workspaceName = 'duplfinder-full-smoke-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$workspaceRoot = Join-Path $parentPath $workspaceName
$datasetRoot = Join-Path $workspaceRoot 'dataset'
$outputRoot = Join-Path $workspaceRoot 'output'
$script:LogsRoot = Join-Path $workspaceRoot 'logs'

$exitCode = 1

try {
    New-Item -ItemType Directory -Force -Path $datasetRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $script:LogsRoot | Out-Null

    Write-Step 'Create controlled dataset'

    $alphaContent = "ALPHA exact duplicate payload`nline two"
    $betaContent = "BETA exact duplicate payload"
    $gammaContent = "GAMMA different filename same content"

    $alpha1 = New-TestFile -Path (Join-Path (Join-Path $datasetRoot 'Alpha One') 'alpha-copy-one.txt') -Content $alphaContent
    $alpha2 = New-TestFile -Path (Join-Path (Join-Path (Join-Path (Join-Path $datasetRoot 'Nested') 'Level 1') 'Level 2') 'Level 3\renamed-alpha.md') -Content $alphaContent
    $alpha3 = New-TestFile -Path (Join-Path (Join-Path $datasetRoot 'Zażółć gęślą jaźń') 'duplikat_ąęść.txt') -Content $alphaContent

    $beta1 = New-TestFile -Path (Join-Path (Join-Path $datasetRoot 'Beta A') 'report.txt') -Content $betaContent
    $beta2 = New-TestFile -Path (Join-Path (Join-Path $datasetRoot 'Beta B') 'report.txt') -Content $betaContent

    $gamma1 = New-TestFile -Path (Join-Path (Join-Path $datasetRoot 'Folder With Spaces') 'different name one.txt') -Content $gammaContent
    $gamma2 = New-TestFile -Path (Join-Path (Join-Path $datasetRoot 'Other Folder') 'completely-renamed.md') -Content $gammaContent

    $sameSize1 = New-TestFileBytes -Path (Join-Path (Join-Path $datasetRoot 'Same Size') 'same-size-left.txt') -Bytes ([System.Text.Encoding]::ASCII.GetBytes('ABCDEFGHIJKLMNOP'))
    $sameSize2 = New-TestFileBytes -Path (Join-Path (Join-Path $datasetRoot 'Same Size') 'same-size-right.txt') -Bytes ([System.Text.Encoding]::ASCII.GetBytes('PONMLKJIHGFEDCBA'))

    $sameName1 = New-TestFile -Path (Join-Path (Join-Path $datasetRoot 'Same Name A') 'same-name.txt') -Content 'same file name but unique payload A'
    $sameName2 = New-TestFile -Path (Join-Path (Join-Path $datasetRoot 'Same Name B') 'same-name.txt') -Content 'same file name but unique payload B'

    $skippedExe = New-TestFile -Path (Join-Path (Join-Path $datasetRoot 'Filtered') 'skipped_tool.exe') -Content $alphaContent

    $empty1 = New-TestFileBytes -Path (Join-Path (Join-Path $datasetRoot 'Empty Files') 'empty-one.txt') -Bytes ([byte[]]@())
    $empty2 = New-TestFileBytes -Path (Join-Path (Join-Path (Join-Path $datasetRoot 'Empty Files') 'Nested Empty') 'empty two.txt') -Bytes ([byte[]]@())

    Assert-True -Condition ((Get-Item -LiteralPath $sameSize1).Length -eq (Get-Item -LiteralPath $sameSize2).Length) -Message 'Same-size test files do not have identical byte length.'
    Assert-True -Condition ((Get-FileSha256 -Path $sameSize1) -ne (Get-FileSha256 -Path $sameSize2)) -Message 'Same-size test files unexpectedly have the same SHA-256.'
    Assert-True -Condition ((Get-FileSha256 -Path $sameName1) -ne (Get-FileSha256 -Path $sameName2)) -Message 'Same-name test files unexpectedly have the same SHA-256.'

    $casePaths = @{
        Alpha = @($alpha1, $alpha2, $alpha3)
        Beta = @($beta1, $beta2)
        Gamma = @($gamma1, $gamma2)
        SameSizeDifferent = @($sameSize1, $sameSize2)
        SameNameDifferent = @($sameName1, $sameName2)
        SkippedExe = $skippedExe
        Empty = @($empty1, $empty2)
    }

    Write-Host "Workspace: $workspaceRoot"
    Write-Host "Dataset:   $datasetRoot"
    Write-Host "Output:    $outputRoot"
    Write-Host "Logs:      $script:LogsRoot"

    Write-Step 'Missing DB safety'
    $missingDb = Join-Path $outputRoot 'missing.db'
    $missing = Invoke-DuplFinder -CliArgs @('stats', '--db', $missingDb) -LogName 'missing-db-stats.log'
    Assert-True -Condition ($missing.ExitCode -ne 0) -Message 'stats against a missing DB unexpectedly succeeded.'
    Assert-True -Condition (-not (Test-Path -LiteralPath $missingDb)) -Message 'Read-only command created the missing DB file.'
    $script:MissingDbValidationResult = 'passed: read-only command failed non-zero and did not create DB'

    Write-Step 'Profile scans and duplicate correctness'
    $profileSpecs = @(
        @{ Label = 'hdd'; Profile = 'hdd'; Threads = '' },
        @{ Label = 'sata-ssd'; Profile = 'sata-ssd'; Threads = '' },
        @{ Label = 'nvme'; Profile = 'nvme'; Threads = '' },
        @{ Label = 'nvme-threads-1'; Profile = 'nvme'; Threads = '1' }
    )

    $primaryDb = Join-Path $outputRoot 'profile-sata-ssd.db'
    $primaryScanOutput = ''

    foreach ($spec in $profileSpecs) {
        $dbName = 'profile-{0}.db' -f $spec.Label
        $csvName = 'duplicates-{0}.csv' -f $spec.Label
        $dbPath = Join-Path $outputRoot $dbName
        $csvPath = Join-Path $outputRoot $csvName

        $scanArgs = @('scan', $datasetRoot, '--db', $dbPath, '--profile', $spec.Profile, '--record-skipped')
        if (-not [string]::IsNullOrWhiteSpace($spec.Threads)) {
            $scanArgs += @('--threads', $spec.Threads)
        }

        $scan = Invoke-DuplFinder -CliArgs $scanArgs -LogName ('scan-{0}.log' -f $spec.Label)
        Assert-True -Condition ($scan.ExitCode -eq 0) -Message "Profile scan failed: $($spec.Label)"
        Assert-True -Condition ($scan.Output -match ('Profile:\s+' + [regex]::Escape($spec.Profile))) -Message "Profile output did not contain expected profile: $($spec.Profile)"

        if ($spec.Label -eq 'nvme-threads-1') {
            Assert-True -Condition ($scan.Output -match 'Threads:\s+1') -Message '--profile nvme --threads 1 did not preserve the explicit thread override.'
        }

        $duplicates = Invoke-DuplFinder -CliArgs @('duplicates', '--db', $dbPath, '--export', $csvPath) -LogName ('duplicates-{0}.log' -f $spec.Label)
        Assert-True -Condition ($duplicates.ExitCode -eq 0) -Message "duplicates command failed for profile: $($spec.Label)"

        $rows = Read-CsvSafe -Path $csvPath
        Validate-DuplicateRows -Rows $rows -CasePaths $casePaths -AlphaCopies 3 -Context $spec.Label

        $script:ProfileResults.Add(('{0}: scan ok, duplicates ok' -f $spec.Label))

        if ($spec.Label -eq 'sata-ssd') {
            $primaryDb = $dbPath
            $primaryScanOutput = $scan.Output
        }
    }

    $hashedFirst = Get-FirstMetric -Text $primaryScanOutput -Label 'Hashed'
    $script:FirstScanResult = "passed: sata-ssd first scan hashed $hashedFirst files"
    $script:DuplicateValidationResult = 'passed: Alpha, Beta, different-name-same-content, non-duplicates, skipped files, and empty-file behavior validated'

    Write-Step 'Skipped entry stats'
    $stats = Invoke-DuplFinder -CliArgs @('stats', '--db', $primaryDb) -LogName 'stats-primary.log'
    Assert-True -Condition ($stats.ExitCode -eq 0) -Message 'stats command failed for primary DB.'
    $skippedCount = Get-FirstMetric -Text $stats.Output -Label 'Skipped'
    Assert-True -Condition ($skippedCount -ge 1) -Message 'Expected at least one skipped/filtered entry with --record-skipped.'

    Write-Step 'Cache behavior'
    $cacheScan = Invoke-DuplFinder -CliArgs @('scan', $datasetRoot, '--db', $primaryDb, '--profile', 'sata-ssd', '--record-skipped') -LogName 'scan-cache.log'
    Assert-True -Condition ($cacheScan.ExitCode -eq 0) -Message 'Second scan for cache behavior failed.'
    $cacheHits = Get-FirstMetric -Text $cacheScan.Output -Label 'Cache'
    $hashedSecond = Get-FirstMetric -Text $cacheScan.Output -Label 'Hashed'
    Assert-True -Condition ($cacheHits -gt 0) -Message 'Second scan did not report cache hits.'
    Assert-True -Condition ($hashedSecond -eq 0) -Message 'Second scan should hash 0 unchanged files.'
    $script:SecondScanResult = "passed: cache hits $cacheHits, hashed $hashedSecond"

    $cacheCsv = Join-Path $outputRoot 'duplicates-after-cache.csv'
    $cacheDuplicates = Invoke-DuplFinder -CliArgs @('duplicates', '--db', $primaryDb, '--export', $cacheCsv) -LogName 'duplicates-after-cache.log'
    Assert-True -Condition ($cacheDuplicates.ExitCode -eq 0) -Message 'duplicates command after cache scan failed.'
    Validate-DuplicateRows -Rows (Read-CsvSafe -Path $cacheCsv) -CasePaths $casePaths -AlphaCopies 3 -Context 'after cache'

    Write-Step 'Prestage report behavior'
    $prestageReportPath = Join-Path $outputRoot 'prestage-report.html'
    $prestageReport = Invoke-DuplFinder -CliArgs @('prestage-report', '--db', $primaryDb, '--out', $prestageReportPath) -LogName 'prestage-report.log'
    Assert-True -Condition ($prestageReport.ExitCode -eq 0) -Message 'prestage-report command failed.'
    Assert-PrestageReportHtml -Path $prestageReportPath -KnownDuplicatePath $alpha1
    $script:PrestageReportValidationResult = "passed: generated local-only HTML report at $prestageReportPath"

    Write-Step 'Clean DB behavior'
    Remove-Item -LiteralPath $alpha3 -Force
    $clean = Invoke-DuplFinder -CliArgs @('clean-db', '--db', $primaryDb, '--batch-size', '2') -LogName 'clean-db.log'
    Assert-True -Condition ($clean.ExitCode -eq 0) -Message 'clean-db command failed.'
    $removedRecords = Get-LastInteger -Text $clean.Output
    Assert-True -Condition ($removedRecords -ge 1) -Message 'clean-db did not report removing any file records.'

    $afterCleanCsv = Join-Path $outputRoot 'duplicates-after-clean.csv'
    $afterClean = Invoke-DuplFinder -CliArgs @('duplicates', '--db', $primaryDb, '--export', $afterCleanCsv) -LogName 'duplicates-after-clean.log'
    Assert-True -Condition ($afterClean.ExitCode -eq 0) -Message 'duplicates command after clean-db failed.'
    $afterCleanRows = Read-CsvSafe -Path $afterCleanCsv
    $afterCleanCasePaths = @{
        Alpha = @($alpha1, $alpha2)
        Beta = $casePaths.Beta
        Gamma = $casePaths.Gamma
        SameSizeDifferent = $casePaths.SameSizeDifferent
        SameNameDifferent = $casePaths.SameNameDifferent
        SkippedExe = $casePaths.SkippedExe
        Empty = $casePaths.Empty
    }
    Validate-DuplicateRows -Rows $afterCleanRows -CasePaths $afterCleanCasePaths -AlphaCopies 2 -Context 'after clean-db'
    Assert-NoDuplicatePath -Rows $afterCleanRows -Path $alpha3 -Message 'Deleted Alpha file is still present in duplicate output after clean-db.'
    $script:CleanDbValidationResult = "passed: clean-db removed $removedRecords record(s), Alpha group changed from 3 to 2"

    Write-Step 'Summary'
    Write-Host "Generated workspace path: $workspaceRoot"
    Write-Host "Parent WorkDir/base path: $parentPath"
    Write-Host "Fake dataset files created: $script:FilesCreated"
    Write-Host 'Expected duplicate groups: Alpha=3, Beta=2, different-name-same-content=2'
    Write-Host "Empty file behavior: $script:EmptyFileBehavior"
    Write-Host "Profile checks result: $($script:ProfileResults -join '; ')"
    Write-Host "First scan result: $script:FirstScanResult"
    Write-Host "Second scan/cache result: $script:SecondScanResult"
    Write-Host "Duplicate validation result: $script:DuplicateValidationResult"
    Write-Host "Prestage report validation result: $script:PrestageReportValidationResult"
    Write-Host "Clean DB validation result: $script:CleanDbValidationResult"
    Write-Host "Missing DB validation result: $script:MissingDbValidationResult"
    Write-Host 'Final PASS'

    $exitCode = 0
}
catch {
    $script:FailureMessage = $_.Exception.Message
    Write-Host ''
    Write-Host "Final FAIL: $script:FailureMessage"
    Write-Host "Generated workspace path: $workspaceRoot"
    Write-Host "Parent WorkDir/base path: $parentPath"
    Write-Host "Fake dataset files created: $script:FilesCreated"
    Write-Host "Profile checks result: $($script:ProfileResults -join '; ')"
    Write-Host "First scan result: $script:FirstScanResult"
    Write-Host "Second scan/cache result: $script:SecondScanResult"
    Write-Host "Duplicate validation result: $script:DuplicateValidationResult"
    Write-Host "Prestage report validation result: $script:PrestageReportValidationResult"
    Write-Host "Clean DB validation result: $script:CleanDbValidationResult"
    Write-Host "Missing DB validation result: $script:MissingDbValidationResult"
    $exitCode = 1
}
finally {
    if ($KeepWorkDir) {
        Write-Host "Keeping generated workspace: $workspaceRoot"
    }
    else {
        Remove-GeneratedWorkspace -WorkspacePath $workspaceRoot -ParentPath $parentPath
        Write-Host "Removed generated workspace: $workspaceRoot"
    }
}

exit $exitCode
