<#
.SYNOPSIS
    Synchronizes and audits NuGet package versions across all .csproj files, documentation, and legal notices.

.DESCRIPTION
    Scans all C# project files (.csproj) in the solution to discover referenced NuGet packages,
    audits all projects for version consistency across the solution, and synchronizes exact version
    numbers into documentation and legal notice files (NOTICE, DOCUMENTATION.md, README.md, etc.).
    Can also harmonize version drift across project files when requested.

.PARAMETER RootDir
    The root directory of the repository. If not specified, automatically detected.

.PARAMETER DryRun
    If specified, performs a dry run without modifying any files on disk, displaying what would be changed.

.PARAMETER VerboseOutput
    If specified, displays detailed scanning and replacement logs.

.PARAMETER AuditOnly
    If specified, checks cross-project consistency and documentation alignment without modifying any files.
    Exits with a warning or non-zero status if version drift is detected.

.PARAMETER HarmonizeProjects
    If specified, updates any .csproj files that reference older package versions to the highest version
    discovered in the solution.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\_ps_scripts\update-package-versions.ps1
    Audits all projects and updates all documentation files in place.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\_ps_scripts\update-package-versions.ps1 -DryRun
    Previews all detected package versions and planned documentation replacements without writing to disk.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\_ps_scripts\update-package-versions.ps1 -HarmonizeProjects
    Harmonizes package versions across all .csproj files to the latest version found, then syncs documentation.
#>

[CmdletBinding()]
param (
    [Parameter(Position = 0)]
    [string]$RootDir = '',

    [switch]$DryRun,
    [switch]$VerboseOutput,
    [switch]$AuditOnly,
    [switch]$HarmonizeProjects
)

$ErrorActionPreference = 'Stop'

if ($AuditOnly) {
    $DryRun = $true
}

# Auto-detect repository root:
if ([string]::IsNullOrWhiteSpace($RootDir)) {
    if ((Test-Path (Join-Path $PSScriptRoot 'NOTICE')) -or (Test-Path (Join-Path $PSScriptRoot 'DOCUMENTATION.md'))) {
        $RootDir = $PSScriptRoot
    } else {
        $parent = Split-Path -Path $PSScriptRoot -Parent
        if ((Test-Path (Join-Path $parent 'NOTICE')) -or (Test-Path (Join-Path $parent 'DOCUMENTATION.md'))) {
            $RootDir = $parent
        } else {
            $currentLoc = (Get-Location).Path
            if ((Test-Path (Join-Path $currentLoc 'NOTICE')) -or (Test-Path (Join-Path $currentLoc 'DOCUMENTATION.md'))) {
                $RootDir = $currentLoc
            } else {
                $RootDir = $parent
            }
        }
    }
}

$RootDir = [System.IO.Path]::GetFullPath($RootDir)

function Write-Info([string]$msg) {
    Write-Host ('[INFO] ' + $msg) -ForegroundColor Cyan
}

function Write-Success([string]$msg) {
    Write-Host ('[SUCCESS] ' + $msg) -ForegroundColor Green
}

function Write-Notice([string]$msg) {
    Write-Host ('[NOTICE] ' + $msg) -ForegroundColor Yellow
}

function Write-WarningMsg([string]$msg) {
    Write-Host ('[WARNING] ' + $msg) -ForegroundColor Red
}

function Write-Change([string]$msg) {
    Write-Host ('  -> ' + $msg) -ForegroundColor Magenta
}

Write-Host '==================================================================' -ForegroundColor DarkCyan
Write-Host ' Semantic Agent Broker -- Package Version Synchronization Script  ' -ForegroundColor Cyan
Write-Host '==================================================================' -ForegroundColor DarkCyan
Write-Host ('Root Directory: ' + $RootDir)
if ($DryRun) {
    Write-Notice 'DRY RUN / AUDIT MODE ENABLED: No files will be modified on disk.'
}
Write-Host ''

# -----------------------------------------------------------------------------
# 1. Discover and parse all .csproj files
# -----------------------------------------------------------------------------
Write-Info 'Scanning for all .csproj files in solution...'
$csprojFiles = Get-ChildItem -Path $RootDir -Filter '*.csproj' -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|\.vs)[\\/]'
}

if ($csprojFiles.Count -eq 0) {
    Write-Error ('No .csproj files found under ' + $RootDir)
}

Write-Info ('Found ' + $csprojFiles.Count + ' project file(s).')

# Store maps:
# $packageMap: package name -> highest version
# $packageProjects: package name -> version -> array of project names
$packageMap = @{}
$packageProjects = @{}
$projectPackageRefs = @{} # file path -> list of @{ Name; Version; Node }

foreach ($file in $csprojFiles) {
    $projName = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    $projectPackageRefs[$file.FullName] = @()

    if ($VerboseOutput) {
        Write-Host ('  Parsing ' + $file.Name + '...') -ForegroundColor Gray
    }
    
    [xml]$xml = Get-Content -Path $file.FullName -Raw
    
    $packageRefs = $xml.SelectNodes('//PackageReference')
    if ($null -ne $packageRefs) {
        foreach ($pkg in $packageRefs) {
            $name = $pkg.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($name)) {
                $name = $pkg.GetAttribute('Update')
            }
            
            $version = $pkg.GetAttribute('Version')
            if ([string]::IsNullOrWhiteSpace($version)) {
                $versionNode = $pkg.SelectSingleNode('Version')
                if ($null -ne $versionNode) {
                    $version = $versionNode.InnerText
                }
            }

            if (-not [string]::IsNullOrWhiteSpace($name) -and -not [string]::IsNullOrWhiteSpace($version)) {
                $name = $name.Trim()
                $version = $version.Trim()
                
                $projectPackageRefs[$file.FullName] += [PSCustomObject]@{
                    Name = $name
                    Version = $version
                }

                if (-not $packageProjects.ContainsKey($name)) {
                    $packageProjects[$name] = @{}
                }
                if (-not $packageProjects[$name].ContainsKey($version)) {
                    $packageProjects[$name][$version] = @()
                }
                $packageProjects[$name][$version] += $projName

                if (-not $packageMap.ContainsKey($name)) {
                    $packageMap[$name] = $version
                } else {
                    $existingVer = $packageMap[$name]
                    if ($existingVer -ne $version) {
                        # Resolve highest version heuristically
                        try {
                            $v1 = [System.Version]($existingVer -replace '-.*$', '')
                            $v2 = [System.Version]($version -replace '-.*$', '')
                            if ($v2 -gt $v1) {
                                $packageMap[$name] = $version
                            }
                        } catch {
                            if ([string]::Compare($version, $existingVer, [System.StringComparison]::OrdinalIgnoreCase) -gt 0) {
                                $packageMap[$name] = $version
                            }
                        }
                    }
                }
            }
        }
    }
}

# -----------------------------------------------------------------------------
# 2. Audit cross-project version consistency
# -----------------------------------------------------------------------------
Write-Host ''
Write-Info 'Auditing cross-project package version consistency...'
$driftCount = 0
$sortedPackageNames = $packageProjects.Keys | Sort-Object

foreach ($pkgName in $sortedPackageNames) {
    $verDict = $packageProjects[$pkgName]
    if ($verDict.Keys.Count -gt 1) {
        $driftCount++
        Write-WarningMsg ('Version mismatch detected for package ''' + $pkgName + ''':')
        foreach ($v in $verDict.Keys) {
            $projs = ($verDict[$v] -join ', ')
            Write-Host ('   - v' + $v + ' in [' + $projs + ']') -ForegroundColor Yellow
        }
    }
}

if ($driftCount -eq 0) {
    Write-Success ('All ' + $packageProjects.Count + ' referenced NuGet package(s) have consistent versions across all projects!')
} else {
    Write-Notice ('Found ' + $driftCount + ' package(s) with version drift across projects.')
    if ($HarmonizeProjects) {
        Write-Info 'Harmonizing .csproj files to latest version...'
        foreach ($filePath in $projectPackageRefs.Keys) {
            $fileContent = [System.IO.File]::ReadAllText($filePath, [System.Text.Encoding]::UTF8)
            $projModified = $false
            foreach ($item in $projectPackageRefs[$filePath]) {
                $targetVer = $packageMap[$item.Name]
                if ($item.Version -ne $targetVer) {
                    $pattern = '(?i)(<PackageReference\s+Include="' + [regex]::Escape($item.Name) + '"\s+Version=")' + [regex]::Escape($item.Version) + '(")'
                    if ($fileContent -match $pattern) {
                        $fileContent = [regex]::Replace($fileContent, $pattern, ('${1}' + $targetVer + '${2}'))
                        $projModified = $true
                        Write-Change ([System.IO.Path]::GetFileName($filePath) + ': updated ' + $item.Name + ' from ' + $item.Version + ' -> ' + $targetVer)
                    }
                }
            }
            if ($projModified -and -not $DryRun) {
                [System.IO.File]::WriteAllText($filePath, $fileContent, [System.Text.Encoding]::UTF8)
            }
        }
    }
}

Write-Host ''
Write-Info ('Active package version catalog (' + $packageMap.Count + ' packages):')
$sortedKeys = $packageMap.Keys | Sort-Object
foreach ($k in $sortedKeys) {
    Write-Host ('  - ' + $k + ' : ' + $packageMap[$k]) -ForegroundColor Gray
}
Write-Host ''

# -----------------------------------------------------------------------------
# 3. Define target documentation and legal files
# -----------------------------------------------------------------------------
$targetFiles = @(
    (Join-Path $RootDir 'NOTICE'),
    (Join-Path $RootDir 'DOCUMENTATION.md'),
    (Join-Path $RootDir 'README.md')
)

$extraMdFiles = Get-ChildItem -Path $RootDir -Filter '*.md' -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|\.vs)[\\/]' -and
    $targetFiles -notcontains $_.FullName
}

foreach ($extra in $extraMdFiles) {
    $targetFiles += $extra.FullName
}

# -----------------------------------------------------------------------------
# 4. Synchronize package versions in each target file
# -----------------------------------------------------------------------------
$totalFilesChanged = 0
$totalReplacements = 0

foreach ($filePath in $targetFiles) {
    if (-not (Test-Path -Path $filePath)) {
        continue
    }

    $fileName = Split-Path $filePath -Leaf
    $originalContent = [System.IO.File]::ReadAllText($filePath, [System.Text.Encoding]::UTF8)
    $updatedContent = $originalContent
    $fileReplacements = 0

    foreach ($pkgName in $sortedKeys) {
        $newVersion = $packageMap[$pkgName]
        $escapedPkg = [regex]::Escape($pkgName)

        # Pattern 1: Notice format with version: - Package: Microsoft.SemanticKernel (v1.78.0)
        $pattern1 = '(?m)(-\s*Package:\s*' + $escapedPkg + '\s*\(v)([^)]+)(\))'
        $matches1 = [regex]::Matches($updatedContent, $pattern1)
        foreach ($m in $matches1) {
            $oldVer = $m.Groups[2].Value
            if ($oldVer -ne $newVersion) {
                $fileReplacements++
                $totalReplacements++
                $logMsg = '[{0}] Updated notice: {1} (v{2}) -> (v{3})' -f $fileName, $pkgName, $oldVer, $newVersion
                Write-Change $logMsg
                $replacement = $m.Groups[1].Value + $newVersion + $m.Groups[3].Value
                $updatedContent = $updatedContent.Replace($m.Value, $replacement)
            }
        }

        # Pattern 2: Notice format without version: - Package: Microsoft.AspNetCore.Components.WebAssembly$
        $pattern1b = '(?m)^(-\s*Package:\s*' + $escapedPkg + ')\s*$'
        $matches1b = [regex]::Matches($updatedContent, $pattern1b)
        foreach ($m in $matches1b) {
            $fileReplacements++
            $totalReplacements++
            $logMsg = '[{0}] Added version to notice: {1} -> (v{2})' -f $fileName, $pkgName, $newVersion
            Write-Change $logMsg
            $replacement = $m.Groups[1].Value + ' (v' + $newVersion + ')'
            $updatedContent = $updatedContent.Replace($m.Value, $replacement)
        }

        # Pattern 3: Markdown table format with backticks: | `Microsoft.SemanticKernel` | `1.78.0` |
        $pattern2 = '(?m)(\|\s*`' + $escapedPkg + '`\s*\|\s*`)([^`]+)(`\s*\|)'
        $matches2 = [regex]::Matches($updatedContent, $pattern2)
        foreach ($m in $matches2) {
            $oldVer = $m.Groups[2].Value
            if ($oldVer -ne $newVersion) {
                $fileReplacements++
                $totalReplacements++
                $logMsg = '[{0}] Updated table entry: `{1}` | `{2}` -> `{3}`' -f $fileName, $pkgName, $oldVer, $newVersion
                Write-Change $logMsg
                $replacement = $m.Groups[1].Value + $newVersion + $m.Groups[3].Value
                $updatedContent = $updatedContent.Replace($m.Value, $replacement)
            }
        }

        # Pattern 4: Markdown table format without backticks: | Microsoft.SemanticKernel | 1.78.0 |
        $pattern3 = '(?m)(\|\s*' + $escapedPkg + '\s*\|\s*)([0-9a-zA-Z._-]+)(\s*\|)'
        $matches3 = [regex]::Matches($updatedContent, $pattern3)
        foreach ($m in $matches3) {
            $oldVer = $m.Groups[2].Value
            if ($oldVer -match '^[0-9]+(\.[0-9a-zA-Z._-]+)+' -and $oldVer -ne $newVersion) {
                $fileReplacements++
                $totalReplacements++
                $logMsg = '[{0}] Updated plain table: {1} | {2} -> {3}' -f $fileName, $pkgName, $oldVer, $newVersion
                Write-Change $logMsg
                $replacement = $m.Groups[1].Value + $newVersion + $m.Groups[3].Value
                $updatedContent = $updatedContent.Replace($m.Value, $replacement)
            }
        }
    }

    if ($fileReplacements -gt 0) {
        $totalFilesChanged++
        if (-not $DryRun) {
            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($filePath, $updatedContent, $utf8NoBom)
            $succMsg = 'Updated {0} with {1} replacement(s).' -f $fileName, $fileReplacements
            Write-Success $succMsg
        } else {
            $dryMsg = '[DryRun] Would write {0} replacement(s) to {1}.' -f $fileReplacements, $fileName
            Write-Notice $dryMsg
        }
    }
}

Write-Host ''
Write-Host '==================================================================' -ForegroundColor DarkCyan
Write-Host ' Synchronization Summary                                          ' -ForegroundColor Cyan
Write-Host '==================================================================' -ForegroundColor DarkCyan
Write-Host ('Projects Audited:     ' + $csprojFiles.Count)
Write-Host ('Version Drift Found:  ' + $driftCount + ' package(s)')
Write-Host ('Docs Scanned:         ' + $targetFiles.Count)
Write-Host ('Docs Modified:        ' + $totalFilesChanged)
Write-Host ('Total Replacements:   ' + $totalReplacements)
Write-Host ''

if ($driftCount -gt 0 -and $AuditOnly) {
    Write-WarningMsg 'Audit failed: package version drift exists across solution projects.'
    exit 1
}

if ($totalReplacements -eq 0 -and $driftCount -eq 0) {
    Write-Success 'All projects are version-consistent and all documentation versions are up-to-date!'
} elseif ($DryRun) {
    Write-Notice 'Dry run complete. Run without -DryRun to apply changes.'
} else {
    Write-Success 'Synchronization complete! All projects and documentation aligned.'
}
