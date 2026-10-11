<#
.SYNOPSIS
    Cleans up 'bin' and 'obj' build artifact directories for all C# projects referenced in SemanticAgentBroker.slnx.

.DESCRIPTION
    Parses the solution file (SemanticAgentBroker.slnx) to discover all referenced .csproj projects,
    identifies their respective 'bin' and 'obj' output directories, and deletes them to free disk space
    and ensure a completely clean state before rebuilding.

.PARAMETER SlnxPath
    Optional explicit path to the SemanticAgentBroker.slnx file. If omitted, auto-detected.

.PARAMETER RootDir
    Optional repository root directory. If omitted, auto-detected.

.PARAMETER DryRun
    If specified, performs a simulation run showing which directories and files would be deleted
    along with estimated disk space reclaimed, without modifying the filesystem.

.PARAMETER Force
    If specified, suppresses confirmations and forces deletion of read-only files.

.PARAMETER VerboseOutput
    If specified, displays detailed logs for each project and directory scanned.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\_ps_scripts\clean-bin-obj.ps1
    Deletes all 'bin' and 'obj' folders across all projects in the solution.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\_ps_scripts\clean-bin-obj.ps1 -DryRun
    Previews all 'bin' and 'obj' directories that would be removed and displays the total space that would be freed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\_ps_scripts\clean-bin-obj.ps1 -VerboseOutput
    Deletes 'bin' and 'obj' folders with verbose per-item logging.
#>

[CmdletBinding()]
param (
    [Parameter(Position = 0)]
    [string]$SlnxPath = '',

    [Parameter(Position = 1)]
    [string]$RootDir = '',

    [switch]$DryRun,
    [switch]$Force,
    [switch]$VerboseOutput
)

$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# Logging helper functions
# -----------------------------------------------------------------------------
function Write-Info([string]$msg) {
    Write-Host ('[INFO] ' + $msg) -ForegroundColor Cyan
}

function Write-Success([string]$msg) {
    Write-Host ('[SUCCESS] ' + $msg) -ForegroundColor Green
}

function Write-Notice([string]$msg) {
    Write-Host ('[NOTICE] ' + $msg) -ForegroundColor Yellow
}

function Write-Warn([string]$msg) {
    Write-Host ('[WARNING] ' + $msg) -ForegroundColor DarkYellow
}

function Write-Delete([string]$msg) {
    Write-Host ('  [x] ' + $msg) -ForegroundColor Red
}

function Format-Bytes([int64]$bytes) {
    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    if ($bytes -ge 1GB) {
        return ($bytes / 1GB).ToString('0.00', $inv) + ' GB'
    } elseif ($bytes -ge 1MB) {
        return ($bytes / 1MB).ToString('0.00', $inv) + ' MB'
    } elseif ($bytes -ge 1KB) {
        return ($bytes / 1KB).ToString('0.00', $inv) + ' KB'
    } else {
        return $bytes.ToString($inv) + ' B'
    }
}

function Get-FolderStats([string]$folderPath) {
    $stats = [PSCustomObject]@{
        FileCount = 0
        ByteSize  = [int64]0
    }
    if (Test-Path -Path $folderPath) {
        try {
            $items = Get-ChildItem -Path $folderPath -Recurse -File -Force -ErrorAction SilentlyContinue
            if ($null -ne $items) {
                $stats.FileCount = ($items | Measure-Object).Count
                $measure = $items | Measure-Object -Property Length -Sum
                if ($null -ne $measure -and $null -ne $measure.Sum) {
                    $stats.ByteSize = [int64]$measure.Sum
                }
            }
        } catch {
            # Ignore access or lock errors during stats collection
        }
    }
    return $stats
}

function Remove-DirectorySafely([string]$folderPath) {
    if (-not (Test-Path -Path $folderPath)) {
        return $true
    }
    try {
        # First remove ReadOnly attribute if any files/subdirs are marked ReadOnly
        Get-ChildItem -Path $folderPath -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Attributes -band [System.IO.FileAttributes]::ReadOnly) {
                $_.Attributes = $_.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
            }
        }
        Remove-Item -Path $folderPath -Recurse -Force -ErrorAction Stop
        return $true
    } catch {
        # Fallback to .NET System.IO.Directory::Delete if Remove-Item encountered PowerShell provider glitch
        try {
            [System.IO.Directory]::Delete($folderPath, $true)
            return $true
        } catch {
            Write-Warn ('Failed to delete folder: ' + $folderPath + ' (' + $_.Exception.Message + ')')
            return $false
        }
    }
}

Write-Host '==================================================================' -ForegroundColor DarkCyan
Write-Host ' Semantic Agent Broker -- Clean Bin & Obj Build Artifacts         ' -ForegroundColor Cyan
Write-Host '==================================================================' -ForegroundColor DarkCyan

# -----------------------------------------------------------------------------
# Auto-detect repository root & solution file
# -----------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($RootDir)) {
    $candidates = @(
        $PSScriptRoot,
        (Split-Path -Path $PSScriptRoot -Parent),
        (Get-Location).Path,
        (Split-Path -Path (Get-Location).Path -Parent)
    )
    foreach ($cand in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($cand) -and (Test-Path -Path $cand)) {
            if ((Test-Path (Join-Path $cand 'SemanticAgentBroker.slnx')) -or
                (Test-Path (Join-Path $cand 'SemanticAgentBroker\SemanticAgentBroker.slnx')) -or
                (Test-Path (Join-Path $cand 'DOCUMENTATION.md'))) {
                $RootDir = $cand
                break
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($RootDir)) {
        $RootDir = (Split-Path -Path $PSScriptRoot -Parent)
    }
}

$RootDir = [System.IO.Path]::GetFullPath($RootDir)

if ([string]::IsNullOrWhiteSpace($SlnxPath)) {
    $slnxCandidates = @(
        (Join-Path $RootDir 'SemanticAgentBroker\SemanticAgentBroker.slnx'),
        (Join-Path $RootDir 'SemanticAgentBroker.slnx'),
        (Join-Path $PSScriptRoot '..\SemanticAgentBroker\SemanticAgentBroker.slnx'),
        (Join-Path $PSScriptRoot '..\SemanticAgentBroker.slnx')
    )
    foreach ($candidate in $slnxCandidates) {
        if (Test-Path -Path $candidate) {
            $SlnxPath = $candidate
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($SlnxPath) -or -not (Test-Path -Path $SlnxPath)) {
        $found = Get-ChildItem -Path $RootDir -Filter 'SemanticAgentBroker.slnx' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $found) {
            $SlnxPath = $found.FullName
        }
    }
}

if ([string]::IsNullOrWhiteSpace($SlnxPath) -or -not (Test-Path -Path $SlnxPath)) {
    Write-Error ('Could not find SemanticAgentBroker.slnx in or under ' + $RootDir + '. Please specify -SlnxPath explicitly.')
}

$SlnxPath = [System.IO.Path]::GetFullPath($SlnxPath)
$slnxDir = Split-Path -Path $SlnxPath -Parent

Write-Host ('Solution File:  ' + $SlnxPath)
Write-Host ('Root Directory: ' + $RootDir)
if ($DryRun) {
    Write-Notice 'DRY RUN MODE ENABLED: No files or folders will be deleted on disk.'
}
Write-Host ''

# -----------------------------------------------------------------------------
# 1. Parse SemanticAgentBroker.slnx for project references
# -----------------------------------------------------------------------------
Write-Info 'Parsing solution file for project references...'

[xml]$slnxXml = Get-Content -Path $SlnxPath -Raw
$projectNodes = $slnxXml.SelectNodes('//Project')

if ($null -eq $projectNodes -or $projectNodes.Count -eq 0) {
    Write-Error ('No <Project> elements found in ' + $SlnxPath)
}

$projects = @()
foreach ($node in $projectNodes) {
    $relPath = $node.GetAttribute('Path')
    if ([string]::IsNullOrWhiteSpace($relPath)) {
        continue
    }

    if ($relPath.EndsWith('.csproj', [System.StringComparison]::OrdinalIgnoreCase)) {
        $fullPath = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($slnxDir, $relPath))
        $projDir = [System.IO.Path]::GetDirectoryName($fullPath)
        $projName = [System.IO.Path]::GetFileNameWithoutExtension($fullPath)

        $projects += [PSCustomObject]@{
            Name         = $projName
            RelativePath = $relPath
            CsprojPath   = $fullPath
            ProjectDir   = $projDir
            Exists       = (Test-Path -Path $fullPath)
        }
    }
}

Write-Info ('Discovered ' + $projects.Count + ' C# project(s) in solution.')
Write-Host ''

# -----------------------------------------------------------------------------
# 2. Discover and Clean 'bin' and 'obj' directories for each project
# -----------------------------------------------------------------------------
$totalFoldersFound = 0
$totalFoldersDeleted = 0
$totalFilesCleaned = 0
$totalBytesCleaned = [int64]0
$projectsWithArtifacts = 0
$deletionErrors = 0

foreach ($proj in $projects) {
    if (-not $proj.Exists) {
        Write-Warn ('Project file not found on disk: ' + $proj.CsprojPath)
        continue
    }

    $binDir = Join-Path $proj.ProjectDir 'bin'
    $objDir = Join-Path $proj.ProjectDir 'obj'

    $targets = @()
    if (Test-Path -Path $binDir) {
        $targets += @{ Name = 'bin'; Path = $binDir }
    }
    if (Test-Path -Path $objDir) {
        $targets += @{ Name = 'obj'; Path = $objDir }
    }

    if ($targets.Count -gt 0) {
        $projectsWithArtifacts++
        Write-Host ('[' + $proj.Name + ']') -ForegroundColor White

        foreach ($t in $targets) {
            $totalFoldersFound++
            $folderPath = $t.Path
            $folderType = $t.Name
            $stats = Get-FolderStats -folderPath $folderPath

            $totalFilesCleaned += $stats.FileCount
            $totalBytesCleaned += $stats.ByteSize

            $relDirDisplay = $folderPath.Substring($RootDir.Length).TrimStart('\', '/')

            if ($DryRun) {
                Write-Host ('  [DryRun] Would delete: ' + $relDirDisplay + ' (' + (Format-Bytes $stats.ByteSize) + ', ' + $stats.FileCount + ' files)') -ForegroundColor Yellow
                $totalFoldersDeleted++
            } else {
                $deleted = Remove-DirectorySafely -folderPath $folderPath
                if ($deleted) {
                    Write-Delete ('Deleted: ' + $relDirDisplay + ' (' + (Format-Bytes $stats.ByteSize) + ' freed, ' + $stats.FileCount + ' files)')
                    $totalFoldersDeleted++
                } else {
                    $deletionErrors++
                }
            }
        }
    } elseif ($VerboseOutput) {
        Write-Host ('[' + $proj.Name + '] (clean - no bin/obj folders)') -ForegroundColor Gray
    }
}

# -----------------------------------------------------------------------------
# 3. Summary Output
# -----------------------------------------------------------------------------
Write-Host ''
Write-Host '==================================================================' -ForegroundColor DarkCyan
Write-Host ' Cleanup Summary                                                  ' -ForegroundColor Cyan
Write-Host '==================================================================' -ForegroundColor DarkCyan
Write-Host ('Projects Scanned:           ' + $projects.Count)
Write-Host ('Projects with Artifacts:    ' + $projectsWithArtifacts)
Write-Host ('Directories Cleaned:        ' + $totalFoldersDeleted + ' of ' + $totalFoldersFound)
Write-Host ('Files Cleaned:              ' + $totalFilesCleaned)
Write-Host ('Total Disk Space Freed:     ' + (Format-Bytes $totalBytesCleaned))
Write-Host ''

if ($totalFoldersFound -eq 0) {
    Write-Success 'All projects in SemanticAgentBroker.slnx are already completely clean! No bin/obj directories found.'
} elseif ($DryRun) {
    Write-Notice ('Dry run complete. Run without -DryRun to delete {0} folder(s) and free {1}.' -f $totalFoldersFound, (Format-Bytes $totalBytesCleaned))
} elseif ($deletionErrors -gt 0) {
    Write-Warn ('Cleanup completed with ' + $deletionErrors + ' error(s). Some files or folders may be locked by running processes (e.g. Visual Studio or dotnet).')
} else {
    Write-Success ('Cleanup successfully completed! Freed ' + (Format-Bytes $totalBytesCleaned) + ' across ' + $projectsWithArtifacts + ' project(s).')
}
