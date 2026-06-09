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
$script:FileTypeFilterValidationResult = 'not run'
$script:PrestageReportPath = ''
$script:MultiRootValidationResult = 'not run'
$script:MultiRootPrestageReportPath = ''
$script:ApplyStagePlanValidationResult = 'not run'
$script:PurgeValidationResult = 'not run'
$script:StagePlanPath = ''
$script:QuarantineSessionPath = ''
$script:QuarantineManifestPath = ''
$script:PurgeManifestPath = ''
$script:PurgeLogPath = ''
$script:CleanDbValidationResult = 'not run'
$script:MissingDbValidationResult = 'not run'
$script:EmptyFileBehavior = ''
$script:FailureMessage = ''
$script:ProjectFullPath = ''
$script:LogsRoot = ''
$script:PolishFolderName = -join @('Za', [char]0x017C, [char]0x00F3, [char]0x0142, [char]0x0107, ' g', [char]0x0119, [char]0x015B, 'l', [char]0x0105, ' ja', [char]0x017A, [char]0x0144)
$script:PolishDuplicateFileName = -join @('duplikat_', [char]0x0105, [char]0x0119, [char]0x015B, [char]0x0107, '.txt')
$script:PolishStageFileName = -join @('alpha_stage_', [char]0x0105, [char]0x0119, [char]0x015B, [char]0x0107, '.txt')

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

function Test-StringContains {
    param(
        [string]$Value,
        [string]$Needle,
        [StringComparison]$Comparison = [StringComparison]::Ordinal
    )

    if ($null -eq $Value) {
        return $false
    }

    return $Value.IndexOf($Needle, $Comparison) -ge 0
}

function ConvertTo-TestLiteralPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $Path
    }

    if ($Path.StartsWith('\\?\', [StringComparison]::Ordinal)) {
        return $Path
    }

    if ($Path.StartsWith('\\', [StringComparison]::Ordinal)) {
        return '\\?\UNC\' + $Path.Substring(2)
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return '\\?\' + [System.IO.Path]::GetFullPath($Path)
    }

    return $Path
}

function Get-DefaultParentWorkDir {
    if (-not [string]::IsNullOrWhiteSpace($script:ProjectFullPath)) {
        return (Join-Path (Split-Path -Parent $script:ProjectFullPath) '.codex-work')
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
    return (Test-StringContains -Value $normalized -Needle '\AppData\Local\Temp' -Comparison ([StringComparison]::OrdinalIgnoreCase)) -or
        (Test-StringContains -Value $normalized -Needle '\AppData\Local\Microsoft\Windows' -Comparison ([StringComparison]::OrdinalIgnoreCase))
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

    return (Get-FileHash -LiteralPath (ConvertTo-TestLiteralPath -Path $Path) -Algorithm SHA256).Hash
}

function Write-JsonFile {
    param(
        [string]$Path,
        [object]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth 12
    Set-Content -LiteralPath (ConvertTo-TestLiteralPath -Path $Path) -Value $json -Encoding UTF8
}

function Assert-FileExists {
    param(
        [string]$Path,
        [string]$Message
    )

    Assert-True -Condition ([System.IO.File]::Exists((ConvertTo-TestLiteralPath -Path $Path))) -Message $Message
}

function Assert-FileMissing {
    param(
        [string]$Path,
        [string]$Message
    )

    $literalPath = ConvertTo-TestLiteralPath -Path $Path
    Assert-True -Condition (-not ([System.IO.File]::Exists($literalPath) -or [System.IO.Directory]::Exists($literalPath))) -Message $Message
}

function Get-EntryByOriginalPath {
    param(
        [object]$Manifest,
        [string]$OriginalPath
    )

    foreach ($entry in @($Manifest.entries)) {
        if ([string]::Equals([string]$entry.original_path, $OriginalPath, [StringComparison]::OrdinalIgnoreCase)) {
            return $entry
        }
    }

    return $null
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

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & dotnet @dotnetArgs 2>&1 | ForEach-Object { $_.ToString() } | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

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
        [string[]]$KnownDuplicatePaths,
        [string[]]$AbsentPaths = @()
    )

    Assert-True -Condition (Test-Path -LiteralPath $Path) -Message "Prestage report HTML was not created: $Path"

    $html = Get-Content -LiteralPath $Path -Raw
    $reportDataMatch = [regex]::Match($html, '<script id="report-data" type="application/json">(?<json>.*?)</script>', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    Assert-True -Condition $reportDataMatch.Success -Message 'Prestage report is missing embedded local JSON report data.'
    $reportData = $reportDataMatch.Groups['json'].Value | ConvertFrom-Json
    $reportPaths = @($reportData.groups | ForEach-Object { $_.files } | ForEach-Object { [string]$_.path })

    Assert-True -Condition (Test-StringContains -Value $html -Needle 'duplfinder.stage-plan.v1') -Message 'Prestage report is missing the stage-plan schema marker.'
    Assert-True -Condition (Test-StringContains -Value $html -Needle 'Export stage-plan.json') -Message 'Prestage report is missing the export button text.'
    Assert-True -Condition (Test-StringContains -Value $html -Needle 'Files in duplicate groups') -Message 'Prestage report is missing the files-in-duplicate-groups summary label.'
    Assert-True -Condition (Test-StringContains -Value $html -Needle 'Redundant files') -Message 'Prestage report is missing the redundant files summary label.'
    Assert-True -Condition (Test-StringContains -Value $html -Needle 'This report does not move or delete files. It only exports a stage plan.') -Message 'Prestage report is missing the no move/delete safety warning.'
    Assert-True -Condition (Test-StringContains -Value $html -Needle 'stageAllExceptKeep(group, groupState, index)') -Message 'Prestage report KEEP handler should stage all non-KEEP files in the changed group.'

    foreach ($knownPath in $KnownDuplicatePaths) {
        Assert-True -Condition (Test-PathInList -Paths $reportPaths -Path $knownPath) -Message "Prestage report is missing expected duplicate path: $knownPath"
    }

    foreach ($absentPath in $AbsentPaths) {
        Assert-True -Condition (-not (Test-PathInList -Paths $reportPaths -Path $absentPath)) -Message "Prestage report unexpectedly contains a skipped/non-duplicate path: $absentPath"
    }

    Assert-True -Condition ((Test-StringContains -Value $html -Needle '#0f1115') -or (Test-StringContains -Value $html -Needle '#171a21')) -Message 'Prestage report is missing expected dark theme color markers.'
    Assert-True -Condition (-not (Test-StringContains -Value $html -Needle 'https://' -Comparison ([StringComparison]::OrdinalIgnoreCase))) -Message 'Prestage report should not reference https:// resources.'
    Assert-True -Condition (-not (Test-StringContains -Value $html -Needle 'http://' -Comparison ([StringComparison]::OrdinalIgnoreCase))) -Message 'Prestage report should not reference http:// resources.'
    Assert-True -Condition (-not (Test-StringContains -Value $html -Needle '<script src=' -Comparison ([StringComparison]::OrdinalIgnoreCase))) -Message 'Prestage report should not load external scripts.'
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

    Remove-Item -LiteralPath (ConvertTo-TestLiteralPath -Path $workspaceInfo.FullName) -Recurse -Force
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
    $alpha3 = New-TestFile -Path (Join-Path (Join-Path $datasetRoot $script:PolishFolderName) $script:PolishDuplicateFileName) -Content $alphaContent

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

    Write-Step 'File type filtering'
    $filterRoot = Join-Path $workspaceRoot 'filter-dataset'
    $filterOutput = Join-Path $outputRoot 'filtering'
    New-Item -ItemType Directory -Force -Path $filterOutput | Out-Null

    $filterTxt1 = New-TestFile -Path (Join-Path (Join-Path $filterRoot 'Docs') 'filter-a.txt') -Content 'FILTER TXT exact duplicate payload'
    $filterTxt2 = New-TestFile -Path (Join-Path (Join-Path $filterRoot 'Docs Nested') 'filter-b.txt') -Content 'FILTER TXT exact duplicate payload'
    $filterJpg1 = New-TestFile -Path (Join-Path (Join-Path $filterRoot 'Images') 'filter-a.jpg') -Content 'FILTER JPG exact duplicate payload'
    $filterJpg2 = New-TestFile -Path (Join-Path (Join-Path $filterRoot 'Images Nested') 'filter-b.jpg') -Content 'FILTER JPG exact duplicate payload'
    $filterNoExt1 = New-TestFile -Path (Join-Path (Join-Path $filterRoot 'No Extension') 'filter-noext-a') -Content 'FILTER NO EXTENSION exact duplicate payload'
    $filterNoExt2 = New-TestFile -Path (Join-Path (Join-Path $filterRoot 'No Extension Nested') 'filter-noext-b') -Content 'FILTER NO EXTENSION exact duplicate payload'

    $filterDefaultDb = Join-Path $filterOutput 'filter-default.db'
    $filterDefaultCsv = Join-Path $filterOutput 'filter-default.csv'
    $filterDefaultScan = Invoke-DuplFinder -CliArgs @('scan', $filterRoot, '--db', $filterDefaultDb, '--profile', 'sata-ssd', '--record-skipped') -LogName 'filter-default-scan.log'
    Assert-True -Condition ($filterDefaultScan.ExitCode -eq 0) -Message 'Default filter scan failed.'
    $filterDefaultDuplicates = Invoke-DuplFinder -CliArgs @('duplicates', '--db', $filterDefaultDb, '--export', $filterDefaultCsv) -LogName 'filter-default-duplicates.log'
    Assert-True -Condition ($filterDefaultDuplicates.ExitCode -eq 0) -Message 'Default filter duplicates command failed.'
    $filterDefaultRows = Read-CsvSafe -Path $filterDefaultCsv
    Assert-DuplicateGroup -Rows $filterDefaultRows -ExpectedPaths @($filterTxt1, $filterTxt2) -ExpectedCopies 2 -Name 'default filter txt'
    Assert-DuplicateGroup -Rows $filterDefaultRows -ExpectedPaths @($filterJpg1, $filterJpg2) -ExpectedCopies 2 -Name 'default filter jpg'
    Assert-NoDuplicatePath -Rows $filterDefaultRows -Path $filterNoExt1 -Message 'Default filter reported an extensionless file without --include-no-extension.'
    Assert-NoDuplicatePath -Rows $filterDefaultRows -Path $filterNoExt2 -Message 'Default filter reported an extensionless file without --include-no-extension.'

    $filterTxtDb = Join-Path $filterOutput 'filter-include-txt.db'
    $filterTxtCsv = Join-Path $filterOutput 'filter-include-txt.csv'
    $filterTxtScan = Invoke-DuplFinder -CliArgs @('scan', $filterRoot, '--db', $filterTxtDb, '--profile', 'sata-ssd', '--include-ext', '.txt', '--record-skipped') -LogName 'filter-include-txt-scan.log'
    Assert-True -Condition ($filterTxtScan.ExitCode -eq 0) -Message '--include-ext .txt scan failed.'
    $filterTxtDuplicates = Invoke-DuplFinder -CliArgs @('duplicates', '--db', $filterTxtDb, '--export', $filterTxtCsv) -LogName 'filter-include-txt-duplicates.log'
    Assert-True -Condition ($filterTxtDuplicates.ExitCode -eq 0) -Message '--include-ext .txt duplicates command failed.'
    $filterTxtRows = Read-CsvSafe -Path $filterTxtCsv
    Assert-DuplicateGroup -Rows $filterTxtRows -ExpectedPaths @($filterTxt1, $filterTxt2) -ExpectedCopies 2 -Name 'include-ext txt'
    Assert-NoDuplicatePath -Rows $filterTxtRows -Path $filterJpg1 -Message '--include-ext .txt reported a .jpg file.'
    Assert-NoDuplicatePath -Rows $filterTxtRows -Path $filterJpg2 -Message '--include-ext .txt reported a .jpg file.'
    Assert-NoDuplicatePath -Rows $filterTxtRows -Path $filterNoExt1 -Message '--include-ext .txt reported an extensionless file without --include-no-extension.'
    Assert-NoDuplicatePath -Rows $filterTxtRows -Path $filterNoExt2 -Message '--include-ext .txt reported an extensionless file without --include-no-extension.'

    $filterNoExtDb = Join-Path $filterOutput 'filter-include-txt-noext.db'
    $filterNoExtCsv = Join-Path $filterOutput 'filter-include-txt-noext.csv'
    $filterNoExtScan = Invoke-DuplFinder -CliArgs @('scan', $filterRoot, '--db', $filterNoExtDb, '--profile', 'sata-ssd', '--include-ext', '.txt', '--include-no-extension', '--record-skipped') -LogName 'filter-include-txt-noext-scan.log'
    Assert-True -Condition ($filterNoExtScan.ExitCode -eq 0) -Message '--include-ext .txt --include-no-extension scan failed.'
    $filterNoExtDuplicates = Invoke-DuplFinder -CliArgs @('duplicates', '--db', $filterNoExtDb, '--export', $filterNoExtCsv) -LogName 'filter-include-txt-noext-duplicates.log'
    Assert-True -Condition ($filterNoExtDuplicates.ExitCode -eq 0) -Message '--include-ext .txt --include-no-extension duplicates command failed.'
    $filterNoExtRows = Read-CsvSafe -Path $filterNoExtCsv
    Assert-DuplicateGroup -Rows $filterNoExtRows -ExpectedPaths @($filterTxt1, $filterTxt2) -ExpectedCopies 2 -Name 'include-ext txt with no-extension txt'
    Assert-DuplicateGroup -Rows $filterNoExtRows -ExpectedPaths @($filterNoExt1, $filterNoExt2) -ExpectedCopies 2 -Name 'include-no-extension'
    Assert-NoDuplicatePath -Rows $filterNoExtRows -Path $filterJpg1 -Message '--include-ext .txt --include-no-extension reported a .jpg file.'
    Assert-NoDuplicatePath -Rows $filterNoExtRows -Path $filterJpg2 -Message '--include-ext .txt --include-no-extension reported a .jpg file.'
    $script:FileTypeFilterValidationResult = 'passed: default whitelist, --include-ext, and --include-no-extension behavior validated'

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
    Assert-PrestageReportHtml `
        -Path $prestageReportPath `
        -KnownDuplicatePaths @($alpha1, $alpha2, $alpha3, $beta1, $beta2, $gamma1, $gamma2) `
        -AbsentPaths @($sameSize1, $sameSize2, $sameName1, $sameName2, $skippedExe)
    $script:PrestageReportPath = $prestageReportPath
    $script:PrestageReportValidationResult = "passed: generated local-only HTML report at $prestageReportPath"

    Write-Step 'Multi-root duplicate accumulation'
    $multiRootA = Join-Path $workspaceRoot 'dataset-root-a'
    $multiRootB = Join-Path $workspaceRoot 'dataset-root-b'
    $multiRootContent = "MULTIROOT exact duplicate payload`nshared across scan roots"
    $multiRootAFile = New-TestFile -Path (Join-Path (Join-Path (Join-Path $multiRootA 'Root A Nested') 'Level One') 'shared-root-copy.txt') -Content $multiRootContent
    $multiRootBFile = New-TestFile -Path (Join-Path (Join-Path (Join-Path $multiRootB 'Root B Folder With Spaces') 'Level Two') 'shared-root-copy-renamed.md') -Content $multiRootContent
    $multiRootUnique = New-TestFile -Path (Join-Path (Join-Path $multiRootB 'Root B Folder With Spaces') 'unique-root-file.txt') -Content 'MULTIROOT unique payload that must not be duplicated'

    $multiRootDb = Join-Path $outputRoot 'multi-root.db'
    $multiRootCsv = Join-Path $outputRoot 'duplicates-multi-root.csv'
    $multiRootReportPath = Join-Path $outputRoot 'prestage-report-multi-root.html'

    $scanRootA = Invoke-DuplFinder -CliArgs @('scan', $multiRootA, '--db', $multiRootDb, '--profile', 'sata-ssd') -LogName 'scan-multi-root-a.log'
    Assert-True -Condition ($scanRootA.ExitCode -eq 0) -Message 'First multi-root scan failed.'

    $scanRootB = Invoke-DuplFinder -CliArgs @('scan', $multiRootB, '--db', $multiRootDb, '--profile', 'sata-ssd') -LogName 'scan-multi-root-b.log'
    Assert-True -Condition ($scanRootB.ExitCode -eq 0) -Message 'Second multi-root scan into the same DB failed.'

    $multiRootDuplicates = Invoke-DuplFinder -CliArgs @('duplicates', '--db', $multiRootDb, '--export', $multiRootCsv) -LogName 'duplicates-multi-root.log'
    Assert-True -Condition ($multiRootDuplicates.ExitCode -eq 0) -Message 'duplicates command failed for multi-root DB.'

    $multiRootRows = Read-CsvSafe -Path $multiRootCsv
    Assert-DuplicateGroup -Rows $multiRootRows -ExpectedPaths @($multiRootAFile, $multiRootBFile) -ExpectedCopies 2 -Name 'multi-root cross-root'
    Assert-NoDuplicatePath -Rows $multiRootRows -Path $multiRootUnique -Message 'Unique multi-root file was reported as a duplicate.'

    $multiRootPrestage = Invoke-DuplFinder -CliArgs @('prestage-report', '--db', $multiRootDb, '--out', $multiRootReportPath) -LogName 'prestage-report-multi-root.log'
    Assert-True -Condition ($multiRootPrestage.ExitCode -eq 0) -Message 'prestage-report command failed for multi-root DB.'
    Assert-PrestageReportHtml `
        -Path $multiRootReportPath `
        -KnownDuplicatePaths @($multiRootAFile, $multiRootBFile) `
        -AbsentPaths @($multiRootUnique)
    $script:MultiRootPrestageReportPath = $multiRootReportPath
    $script:MultiRootValidationResult = 'passed: separate scan roots accumulated into one DB and grouped by exact size + SHA-256'

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

    Write-Step 'Apply stage plan and undo quarantine'
    $applyRoot = Join-Path $workspaceRoot 'apply-stage-dataset'
    $applyRootA = Join-Path $applyRoot 'root-a'
    $applyRootB = Join-Path $applyRoot 'root-b'
    $applyStageOutput = Join-Path $outputRoot 'apply-stage'
    New-Item -ItemType Directory -Force -Path $applyStageOutput | Out-Null

    $applyAlphaContent = "APPLY ALPHA exact duplicate payload`nthree files"
    $applyBetaContent = 'APPLY BETA same filename duplicate payload'
    $applyGammaContent = 'APPLY GAMMA different filename duplicate payload'
    $applyCrossRootContent = 'APPLY CROSS ROOT duplicate payload'
    $applyChangedContent = 'APPLY CHANGED duplicate payload before mutation'

    $applyAlphaKeep = New-TestFile -Path (Join-Path (Join-Path $applyRoot 'Alpha Keep') 'alpha-keep.txt') -Content $applyAlphaContent
    $applyAlphaNested = New-TestFile -Path (Join-Path (Join-Path (Join-Path (Join-Path $applyRoot 'Alpha Nested') 'Level 1') 'Level 2') 'Level 3\alpha-stage-nested.md') -Content $applyAlphaContent
    $applyAlphaUnicode = New-TestFile -Path (Join-Path (Join-Path $applyRoot $script:PolishFolderName) $script:PolishStageFileName) -Content $applyAlphaContent

    $applyBetaKeep = New-TestFile -Path (Join-Path (Join-Path $applyRoot 'Beta Keep') 'report.txt') -Content $applyBetaContent
    $applyBetaStage = New-TestFile -Path (Join-Path (Join-Path $applyRoot 'Beta Stage') 'report.txt') -Content $applyBetaContent

    $applyGammaKeep = New-TestFile -Path (Join-Path (Join-Path $applyRoot 'Folder With Spaces') 'different name one.txt') -Content $applyGammaContent
    $applyGammaStage = New-TestFile -Path (Join-Path (Join-Path $applyRoot 'Other Folder') 'completely-renamed.md') -Content $applyGammaContent

    $applyRootAKeep = New-TestFile -Path (Join-Path (Join-Path $applyRootA 'Cross Root A') 'cross-root-keep.txt') -Content $applyCrossRootContent
    $applyRootBStage = New-TestFile -Path (Join-Path (Join-Path (Join-Path $applyRootB 'Cross Root B') 'Nested') 'cross-root-stage-renamed.md') -Content $applyCrossRootContent

    $applySameSize1 = New-TestFileBytes -Path (Join-Path (Join-Path $applyRoot 'Same Size') 'same-size-left.txt') -Bytes ([System.Text.Encoding]::ASCII.GetBytes('1234567890abcdef'))
    $applySameSize2 = New-TestFileBytes -Path (Join-Path (Join-Path $applyRoot 'Same Size') 'same-size-right.txt') -Bytes ([System.Text.Encoding]::ASCII.GetBytes('fedcba0987654321'))
    $applySameName1 = New-TestFile -Path (Join-Path (Join-Path $applyRoot 'Same Name A') 'same-name.txt') -Content 'apply same name unique A'
    $applySameName2 = New-TestFile -Path (Join-Path (Join-Path $applyRoot 'Same Name B') 'same-name.txt') -Content 'apply same name unique B'

    $missingKeepPath = Join-Path (Join-Path $applyRoot 'Missing Keep') 'missing-keep.txt'
    $missingKeepStage = New-TestFile -Path (Join-Path (Join-Path $applyRoot 'Missing Keep') 'stage-stays.txt') -Content 'stage file should stay because keep is missing'

    $changedKeep = New-TestFile -Path (Join-Path (Join-Path $applyRoot 'Changed Hash') 'changed-keep.txt') -Content $applyChangedContent
    $changedStage = New-TestFile -Path (Join-Path (Join-Path $applyRoot 'Changed Hash') 'changed-stage.txt') -Content $applyChangedContent
    $changedGroupHash = Get-FileSha256 -Path $changedKeep
    $changedGroupSize = (Get-Item -LiteralPath $changedKeep).Length
    Set-Content -LiteralPath $changedStage -Value 'changed after plan creation; should be skipped' -Encoding UTF8

    Assert-True -Condition ((Get-Item -LiteralPath $applySameSize1).Length -eq (Get-Item -LiteralPath $applySameSize2).Length) -Message 'Apply-stage same-size files do not have identical byte length.'
    Assert-True -Condition ((Get-FileSha256 -Path $applySameSize1) -ne (Get-FileSha256 -Path $applySameSize2)) -Message 'Apply-stage same-size files unexpectedly have the same SHA-256.'
    Assert-True -Condition ((Get-FileSha256 -Path $applySameName1) -ne (Get-FileSha256 -Path $applySameName2)) -Message 'Apply-stage same-name files unexpectedly have the same SHA-256.'

    $stagePlanPath = Join-Path $applyStageOutput 'stage-plan.json'
    $stagePlan = [ordered]@{
        schema = 'duplfinder.stage-plan.v1'
        created_utc = (Get-Date).ToUniversalTime().ToString('O')
        source_db = 'full-smoke-generated'
        source_report = 'full-smoke-generated'
        generator = 'DuplFinder full smoke'
        groups = @(
            [ordered]@{
                group_number = 1
                size = (Get-Item -LiteralPath $applyAlphaKeep).Length
                hash = Get-FileSha256 -Path $applyAlphaKeep
                keep_path = $applyAlphaKeep
                stage_paths = @($applyAlphaNested, $applyAlphaUnicode)
            },
            [ordered]@{
                group_number = 2
                size = (Get-Item -LiteralPath $applyBetaKeep).Length
                hash = Get-FileSha256 -Path $applyBetaKeep
                keep_path = $applyBetaKeep
                stage_paths = @($applyBetaStage)
            },
            [ordered]@{
                group_number = 3
                size = (Get-Item -LiteralPath $applyGammaKeep).Length
                hash = Get-FileSha256 -Path $applyGammaKeep
                keep_path = $applyGammaKeep
                stage_paths = @($applyGammaStage)
            },
            [ordered]@{
                group_number = 4
                size = (Get-Item -LiteralPath $applyRootAKeep).Length
                hash = Get-FileSha256 -Path $applyRootAKeep
                keep_path = $applyRootAKeep
                stage_paths = @($applyRootBStage)
            },
            [ordered]@{
                group_number = 5
                size = (Get-Item -LiteralPath $missingKeepStage).Length
                hash = Get-FileSha256 -Path $missingKeepStage
                keep_path = $missingKeepPath
                stage_paths = @($missingKeepStage)
            },
            [ordered]@{
                group_number = 6
                size = $changedGroupSize
                hash = $changedGroupHash
                keep_path = $changedKeep
                stage_paths = @($changedStage)
            }
        )
    }
    Write-JsonFile -Path $stagePlanPath -Value $stagePlan
    $script:StagePlanPath = $stagePlanPath

    $expectedMovedPaths = @($applyAlphaNested, $applyAlphaUnicode, $applyBetaStage, $applyGammaStage, $applyRootBStage)
    $keepPaths = @($applyAlphaKeep, $applyBetaKeep, $applyGammaKeep, $applyRootAKeep, $changedKeep)

    $applyDryRun = Invoke-DuplFinder -CliArgs @('apply-stage-plan', '--plan', $stagePlanPath, '--dry-run') -LogName 'apply-stage-plan-dry-run.log'
    Assert-True -Condition ($applyDryRun.ExitCode -eq 0) -Message 'apply-stage-plan --dry-run failed.'
    Assert-True -Condition ($applyDryRun.Output -match 'Mode:\s+dry-run') -Message 'apply-stage-plan dry-run did not report dry-run mode.'
    foreach ($path in $expectedMovedPaths + @($missingKeepStage, $changedStage)) {
        Assert-FileExists -Path $path -Message "Dry-run moved or removed a stage path: $path"
    }
    foreach ($path in $keepPaths) {
        Assert-FileExists -Path $path -Message "Dry-run touched a KEEP path: $path"
    }

    $quarantineRoot = Join-Path $applyStageOutput 'quarantine'
    $applyQuarantine = Invoke-DuplFinder -CliArgs @('apply-stage-plan', '--plan', $stagePlanPath, '--quarantine', $quarantineRoot) -LogName 'apply-stage-plan-quarantine.log'
    Assert-True -Condition ($applyQuarantine.ExitCode -eq 0) -Message 'apply-stage-plan quarantine mode failed.'
    Assert-True -Condition ($applyQuarantine.Output -match 'Mode:\s+quarantine') -Message 'apply-stage-plan quarantine mode did not report quarantine mode.'

    $manifestMatch = [regex]::Match($applyQuarantine.Output, '(?m)^Manifest:\s+(.+duplfinder-quarantine-manifest\.json)\s*$')
    Assert-True -Condition $manifestMatch.Success -Message 'apply-stage-plan quarantine output did not include a manifest path.'
    $manifestPath = $manifestMatch.Groups[1].Value.Trim()
    Assert-FileExists -Path $manifestPath -Message 'apply-stage-plan quarantine did not create the reported manifest file.'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifestEntries = @($manifest.entries)
    Assert-True -Condition ([string]$manifest.schema -eq 'duplfinder.quarantine-manifest.v1') -Message 'Quarantine manifest schema is invalid.'
    Assert-True -Condition ($manifestEntries.Count -eq $expectedMovedPaths.Count) -Message 'Quarantine manifest does not contain one entry per moved file.'
    Assert-True -Condition ([string]$manifest.source_stage_plan_path -eq $stagePlanPath) -Message 'Quarantine manifest does not record the source stage-plan path.'
    $script:QuarantineManifestPath = $manifestPath
    $script:QuarantineSessionPath = [string]$manifest.quarantine_session_path

    foreach ($path in $keepPaths) {
        Assert-FileExists -Path $path -Message "Quarantine mode touched a KEEP path: $path"
    }
    foreach ($path in $expectedMovedPaths) {
        $entry = Get-EntryByOriginalPath -Manifest $manifest -OriginalPath $path
        Assert-True -Condition ($null -ne $entry) -Message "Manifest is missing moved original path: $path"
        Assert-FileMissing -Path $path -Message "Quarantine mode did not move selected stage path: $path"
        Assert-FileExists -Path ([string]$entry.quarantine_path) -Message "Quarantine file is missing for moved path: $path"
    }
    foreach ($path in @($missingKeepStage, $changedStage, $applySameSize1, $applySameSize2, $applySameName1, $applySameName2)) {
        Assert-FileExists -Path $path -Message "Quarantine mode processed a skipped/non-plan path unexpectedly: $path"
        $entry = Get-EntryByOriginalPath -Manifest $manifest -OriginalPath $path
        Assert-True -Condition ($null -eq $entry) -Message "Manifest unexpectedly contains skipped/non-plan path: $path"
    }

    $undoDryRun = Invoke-DuplFinder -CliArgs @('undo-quarantine', '--manifest', $manifestPath, '--dry-run') -LogName 'undo-quarantine-dry-run.log'
    Assert-True -Condition ($undoDryRun.ExitCode -eq 0) -Message 'undo-quarantine --dry-run failed.'
    Assert-True -Condition ($undoDryRun.Output -match 'Mode:\s+dry-run') -Message 'undo-quarantine dry-run did not report dry-run mode.'
    foreach ($path in $expectedMovedPaths) {
        $entry = Get-EntryByOriginalPath -Manifest $manifest -OriginalPath $path
        Assert-FileMissing -Path $path -Message "Undo dry-run restored an original stage path: $path"
        Assert-FileExists -Path ([string]$entry.quarantine_path) -Message "Undo dry-run moved a quarantine path: $([string]$entry.quarantine_path)"
    }

    $collisionEntry = Get-EntryByOriginalPath -Manifest $manifest -OriginalPath $applyBetaStage
    $hashMismatchEntry = Get-EntryByOriginalPath -Manifest $manifest -OriginalPath $applyGammaStage
    Assert-True -Condition ($null -ne $collisionEntry) -Message 'Manifest is missing the collision test entry.'
    Assert-True -Condition ($null -ne $hashMismatchEntry) -Message 'Manifest is missing the hash-mismatch test entry.'
    New-TestFile -Path $applyBetaStage -Content 'collision original created before undo restore' | Out-Null
    Set-Content -LiteralPath (ConvertTo-TestLiteralPath -Path ([string]$hashMismatchEntry.quarantine_path)) -Value 'tampered quarantine content; restore should skip' -Encoding UTF8

    $undoRestore = Invoke-DuplFinder -CliArgs @('undo-quarantine', '--manifest', $manifestPath, '--restore') -LogName 'undo-quarantine-restore.log'
    Assert-True -Condition ($undoRestore.ExitCode -eq 0) -Message 'undo-quarantine --restore failed.'
    Assert-True -Condition ($undoRestore.Output -match 'Mode:\s+restore') -Message 'undo-quarantine restore did not report restore mode.'

    foreach ($restoredPath in @($applyAlphaNested, $applyAlphaUnicode, $applyRootBStage)) {
        $entry = Get-EntryByOriginalPath -Manifest $manifest -OriginalPath $restoredPath
        Assert-FileExists -Path $restoredPath -Message "Undo restore did not restore expected path: $restoredPath"
        Assert-FileMissing -Path ([string]$entry.quarantine_path) -Message "Undo restore left quarantine file behind for restored path: $([string]$entry.quarantine_path)"
    }
    Assert-FileExists -Path $applyBetaStage -Message 'Undo collision path should still exist at original location.'
    Assert-True -Condition (Test-StringContains -Value (Get-Content -LiteralPath (ConvertTo-TestLiteralPath -Path $applyBetaStage) -Raw) -Needle 'collision original') -Message 'Undo collision overwrote the existing original file.'
    Assert-FileExists -Path ([string]$collisionEntry.quarantine_path) -Message 'Undo collision should leave quarantined file in place.'
    Assert-FileMissing -Path $applyGammaStage -Message 'Undo hash mismatch should not restore the tampered quarantine file.'
    Assert-FileExists -Path ([string]$hashMismatchEntry.quarantine_path) -Message 'Undo hash mismatch should leave tampered quarantine file in place.'

    foreach ($path in $keepPaths + @($missingKeepStage, $changedStage, $applySameSize1, $applySameSize2, $applySameName1, $applySameName2)) {
        Assert-FileExists -Path $path -Message "Apply/undo validation unexpectedly lost protected path: $path"
    }

    Write-Step 'Purge quarantine and hostile manifest input'
    $purgeQuarantineRoot = Join-Path $applyStageOutput 'purge-quarantine'
    $purgeApply = Invoke-DuplFinder -CliArgs @('apply-stage-plan', '--plan', $stagePlanPath, '--quarantine', $purgeQuarantineRoot) -LogName 'apply-stage-plan-purge-quarantine.log'
    Assert-True -Condition ($purgeApply.ExitCode -eq 0) -Message 'apply-stage-plan quarantine mode failed for purge validation.'

    $purgeManifestMatch = [regex]::Match($purgeApply.Output, '(?m)^Manifest:\s+(.+duplfinder-quarantine-manifest\.json)\s*$')
    Assert-True -Condition $purgeManifestMatch.Success -Message 'apply-stage-plan purge setup output did not include a manifest path.'
    $purgeBaseManifestPath = $purgeManifestMatch.Groups[1].Value.Trim()
    Assert-FileExists -Path $purgeBaseManifestPath -Message 'apply-stage-plan purge setup did not create the reported manifest file.'
    $purgeBaseManifest = Get-Content -LiteralPath $purgeBaseManifestPath -Raw | ConvertFrom-Json
    $purgeBaseEntries = @($purgeBaseManifest.entries)
    Assert-True -Condition ($purgeBaseEntries.Count -ge 3) -Message 'Purge validation expected at least three freshly quarantined files.'

    $purgeValidEntry = Get-EntryByOriginalPath -Manifest $purgeBaseManifest -OriginalPath $applyAlphaNested
    $purgeMissingEntry = Get-EntryByOriginalPath -Manifest $purgeBaseManifest -OriginalPath $applyAlphaUnicode
    $purgeTamperedEntry = Get-EntryByOriginalPath -Manifest $purgeBaseManifest -OriginalPath $applyRootBStage
    Assert-True -Condition ($null -ne $purgeValidEntry) -Message 'Purge manifest is missing the valid purge entry.'
    Assert-True -Condition ($null -ne $purgeMissingEntry) -Message 'Purge manifest is missing the missing-file entry.'
    Assert-True -Condition ($null -ne $purgeTamperedEntry) -Message 'Purge manifest is missing the tampered-file entry.'

    $purgeSessionPath = [string]$purgeBaseManifest.quarantine_session_path
    $purgeStrayFile = New-TestFile -Path (Join-Path $purgeSessionPath 'stray-not-listed.txt') -Content 'not listed in manifest; purge must not delete this file'
    $purgeNegativeFile = New-TestFile -Path (Join-Path $purgeSessionPath 'negative-size-listed.txt') -Content 'negative size listed file should not be deleted'
    $purgeInvalidHashFile = New-TestFile -Path (Join-Path $purgeSessionPath 'invalid-hash-listed.txt') -Content 'invalid sha listed file should not be deleted'
    $purgeEscapeFile = New-TestFile -Path (Join-Path $applyStageOutput 'escape-target-outside-quarantine.txt') -Content 'escape target should not be deleted'

    Remove-Item -LiteralPath (ConvertTo-TestLiteralPath -Path ([string]$purgeMissingEntry.quarantine_path)) -Force
    Set-Content -LiteralPath (ConvertTo-TestLiteralPath -Path ([string]$purgeTamperedEntry.quarantine_path)) -Value 'tampered before purge; hash mismatch should skip' -Encoding UTF8
    New-TestFile -Path ([string]$purgeValidEntry.original_path) -Content 'original placeholder created before purge; must survive' | Out-Null

    $purgeManifestPath = Join-Path $applyStageOutput 'purge-hostile-manifest.json'
    $purgeManifestForTest = [ordered]@{
        schema = 'duplfinder.quarantine-manifest.v1'
        created_utc = [string]$purgeBaseManifest.created_utc
        source_stage_plan_path = [string]$purgeBaseManifest.source_stage_plan_path
        quarantine_root_path = [string]$purgeBaseManifest.quarantine_root_path
        quarantine_session_path = [string]$purgeBaseManifest.quarantine_session_path
        tool_version = [string]$purgeBaseManifest.tool_version
        entries = @(
            [ordered]@{
                original_path = [string]$purgeValidEntry.original_path
                quarantine_path = [string]$purgeValidEntry.quarantine_path
                size = [int64]$purgeValidEntry.size
                sha256 = [string]$purgeValidEntry.sha256
                group_number = [int]$purgeValidEntry.group_number
                group_hash = [string]$purgeValidEntry.group_hash
                moved_utc = [string]$purgeValidEntry.moved_utc
                status = 'moved'
            },
            [ordered]@{
                original_path = [string]$purgeMissingEntry.original_path
                quarantine_path = [string]$purgeMissingEntry.quarantine_path
                size = [int64]$purgeMissingEntry.size
                sha256 = [string]$purgeMissingEntry.sha256
                group_number = [int]$purgeMissingEntry.group_number
                group_hash = [string]$purgeMissingEntry.group_hash
                moved_utc = [string]$purgeMissingEntry.moved_utc
                status = 'moved'
            },
            [ordered]@{
                original_path = [string]$purgeTamperedEntry.original_path
                quarantine_path = [string]$purgeTamperedEntry.quarantine_path
                size = [int64]$purgeTamperedEntry.size
                sha256 = [string]$purgeTamperedEntry.sha256
                group_number = [int]$purgeTamperedEntry.group_number
                group_hash = [string]$purgeTamperedEntry.group_hash
                moved_utc = [string]$purgeTamperedEntry.moved_utc
                status = 'moved'
            },
            [ordered]@{
                original_path = (Join-Path $applyRoot 'Hostile\escape-original.txt')
                quarantine_path = $purgeEscapeFile
                size = (Get-Item -LiteralPath $purgeEscapeFile).Length
                sha256 = Get-FileSha256 -Path $purgeEscapeFile
                group_number = 9001
                group_hash = Get-FileSha256 -Path $purgeEscapeFile
                moved_utc = (Get-Date).ToUniversalTime().ToString('O')
                status = 'moved'
            },
            [ordered]@{
                original_path = (Join-Path $applyRoot 'Hostile\negative-original.txt')
                quarantine_path = $purgeNegativeFile
                size = -1
                sha256 = Get-FileSha256 -Path $purgeNegativeFile
                group_number = 9002
                group_hash = Get-FileSha256 -Path $purgeNegativeFile
                moved_utc = (Get-Date).ToUniversalTime().ToString('O')
                status = 'moved'
            },
            [ordered]@{
                original_path = (Join-Path $applyRoot 'Hostile\invalid-sha-original.txt')
                quarantine_path = $purgeInvalidHashFile
                size = (Get-Item -LiteralPath $purgeInvalidHashFile).Length
                sha256 = 'not-a-valid-sha256'
                group_number = 9003
                group_hash = Get-FileSha256 -Path $purgeInvalidHashFile
                moved_utc = (Get-Date).ToUniversalTime().ToString('O')
                status = 'moved'
            }
        )
    }
    Write-JsonFile -Path $purgeManifestPath -Value $purgeManifestForTest
    $script:PurgeManifestPath = $purgeManifestPath
    $script:QuarantineSessionPath = $purgeSessionPath

    $purgeDryRun = Invoke-DuplFinder -CliArgs @('purge-quarantine', '--manifest', $purgeManifestPath, '--dry-run') -LogName 'purge-quarantine-dry-run.log'
    Assert-True -Condition ($purgeDryRun.ExitCode -eq 0) -Message 'purge-quarantine --dry-run failed.'
    Assert-True -Condition ($purgeDryRun.Output -match 'Mode:\s+dry-run') -Message 'purge-quarantine dry-run did not report dry-run mode.'
    Assert-True -Condition ($purgeDryRun.Output -match 'Manifest entries:\s+6') -Message 'purge-quarantine dry-run did not report expected manifest entry count.'
    Assert-True -Condition ($purgeDryRun.Output -match 'Eligible entries:\s+6') -Message 'purge-quarantine dry-run did not report expected eligible entry count.'
    Assert-True -Condition ($purgeDryRun.Output -match 'Planned:\s+1') -Message 'purge-quarantine dry-run did not report expected planned count.'
    Assert-FileExists -Path ([string]$purgeValidEntry.quarantine_path) -Message 'Purge dry-run deleted the valid quarantine file.'
    Assert-FileExists -Path ([string]$purgeTamperedEntry.quarantine_path) -Message 'Purge dry-run deleted the tampered quarantine file.'
    Assert-FileExists -Path $purgeStrayFile -Message 'Purge dry-run deleted a file not listed in the manifest.'
    Assert-FileExists -Path $purgeEscapeFile -Message 'Purge dry-run deleted an escaping manifest path.'

    $purgeBothFlags = Invoke-DuplFinder -CliArgs @('purge-quarantine', '--manifest', $purgeManifestPath, '--dry-run', '--confirm-purge') -LogName 'purge-quarantine-conflicting-flags.log'
    Assert-True -Condition ($purgeBothFlags.ExitCode -ne 0) -Message 'purge-quarantine accepted --dry-run with --confirm-purge.'

    $invalidSchemaManifestPath = Join-Path $applyStageOutput 'purge-invalid-schema.json'
    Write-JsonFile -Path $invalidSchemaManifestPath -Value ([ordered]@{ schema = 'invalid'; quarantine_root_path = $purgeQuarantineRoot; quarantine_session_path = $purgeSessionPath; entries = @() })
    $invalidSchema = Invoke-DuplFinder -CliArgs @('purge-quarantine', '--manifest', $invalidSchemaManifestPath, '--dry-run') -LogName 'purge-invalid-schema.log'
    Assert-True -Condition ($invalidSchema.ExitCode -ne 0) -Message 'purge-quarantine accepted an invalid manifest schema.'

    $missingFieldsManifestPath = Join-Path $applyStageOutput 'purge-missing-fields.json'
    Write-JsonFile -Path $missingFieldsManifestPath -Value ([ordered]@{ schema = 'duplfinder.quarantine-manifest.v1' })
    $missingFields = Invoke-DuplFinder -CliArgs @('purge-quarantine', '--manifest', $missingFieldsManifestPath, '--dry-run') -LogName 'purge-missing-fields.log'
    Assert-True -Condition ($missingFields.ExitCode -ne 0) -Message 'purge-quarantine accepted a manifest with missing required fields.'

    $malformedManifestPath = Join-Path $applyStageOutput 'purge-malformed.json'
    Set-Content -LiteralPath $malformedManifestPath -Value '{ malformed json' -Encoding UTF8
    $malformed = Invoke-DuplFinder -CliArgs @('purge-quarantine', '--manifest', $malformedManifestPath, '--dry-run') -LogName 'purge-malformed-json.log'
    Assert-True -Condition ($malformed.ExitCode -ne 0) -Message 'purge-quarantine accepted malformed JSON.'

    $purgeConfirm = Invoke-DuplFinder -CliArgs @('purge-quarantine', '--manifest', $purgeManifestPath, '--confirm-purge') -LogName 'purge-quarantine-confirm.log'
    Assert-True -Condition ($purgeConfirm.ExitCode -eq 0) -Message 'purge-quarantine --confirm-purge failed.'
    Assert-True -Condition ($purgeConfirm.Output -match 'Mode:\s+purge') -Message 'purge-quarantine confirm did not report purge mode.'
    Assert-True -Condition ($purgeConfirm.Output -match 'Purged:\s+1') -Message 'purge-quarantine confirm did not purge exactly one validated quarantine file.'
    Assert-True -Condition ($purgeConfirm.Output -match 'Skipped:\s+5') -Message 'purge-quarantine confirm did not skip the expected hostile/missing/tampered entries.'
    $script:PurgeLogPath = $purgeConfirm.LogPath

    Assert-FileMissing -Path ([string]$purgeValidEntry.quarantine_path) -Message 'Purge confirm did not delete the validated quarantine file.'
    Assert-FileExists -Path ([string]$purgeValidEntry.original_path) -Message 'Purge confirm deleted or touched original_path.'
    Assert-True -Condition (Test-StringContains -Value (Get-Content -LiteralPath (ConvertTo-TestLiteralPath -Path ([string]$purgeValidEntry.original_path)) -Raw) -Needle 'original placeholder') -Message 'Purge confirm modified original_path.'
    Assert-FileExists -Path ([string]$purgeTamperedEntry.quarantine_path) -Message 'Purge confirm deleted the tampered quarantine file.'
    Assert-FileExists -Path $purgeNegativeFile -Message 'Purge confirm deleted the negative-size manifest file.'
    Assert-FileExists -Path $purgeInvalidHashFile -Message 'Purge confirm deleted the invalid-sha manifest file.'
    Assert-FileExists -Path $purgeStrayFile -Message 'Purge confirm deleted a file not listed in the manifest.'
    Assert-FileExists -Path $purgeEscapeFile -Message 'Purge confirm deleted an escaping manifest path.'
    foreach ($path in $keepPaths) {
        Assert-FileExists -Path $path -Message "Purge confirm touched a KEEP path: $path"
    }

    $script:ApplyStagePlanValidationResult = 'passed: dry-run, quarantine move, manifest, undo dry-run, restore, collision skip, and hash-mismatch skip validated'
    $script:PurgeValidationResult = 'passed: dry-run, confirm purge, hostile manifest skips, original-path protection, and non-listed file protection validated'

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
    Write-Host "File type filter validation result: $script:FileTypeFilterValidationResult"
    Write-Host "Prestage report validation result: $script:PrestageReportValidationResult"
    Write-Host "Multi-root validation result: $script:MultiRootValidationResult"
    Write-Host "Apply stage plan validation result: $script:ApplyStagePlanValidationResult"
    Write-Host "Purge quarantine validation result: $script:PurgeValidationResult"
    Write-Host "Prestage report: $script:PrestageReportPath"
    Write-Host "Multi-root prestage report: $script:MultiRootPrestageReportPath"
    Write-Host "Stage plan: $script:StagePlanPath"
    Write-Host "Quarantine session: $script:QuarantineSessionPath"
    Write-Host "Quarantine manifest: $script:QuarantineManifestPath"
    Write-Host "Purge manifest: $script:PurgeManifestPath"
    Write-Host "Purge log: $script:PurgeLogPath"
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
    Write-Host "File type filter validation result: $script:FileTypeFilterValidationResult"
    Write-Host "Prestage report validation result: $script:PrestageReportValidationResult"
    Write-Host "Multi-root validation result: $script:MultiRootValidationResult"
    Write-Host "Apply stage plan validation result: $script:ApplyStagePlanValidationResult"
    Write-Host "Purge quarantine validation result: $script:PurgeValidationResult"
    Write-Host "Prestage report: $script:PrestageReportPath"
    Write-Host "Multi-root prestage report: $script:MultiRootPrestageReportPath"
    Write-Host "Stage plan: $script:StagePlanPath"
    Write-Host "Quarantine session: $script:QuarantineSessionPath"
    Write-Host "Quarantine manifest: $script:QuarantineManifestPath"
    Write-Host "Purge manifest: $script:PurgeManifestPath"
    Write-Host "Purge log: $script:PurgeLogPath"
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
