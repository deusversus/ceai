<#
.SYNOPSIS
    Removes CE AI Suite user data from the local machine after uninstall.

.DESCRIPTION
    Cleans up persisted settings, sessions, logs, recovery files, custom skills,
    and DPAPI-encrypted credentials stored under %LOCALAPPDATA%\CEAISuite.

    This script is intended to be run manually after uninstalling the application,
    or called by an installer's post-uninstall hook.

.PARAMETER Force
    Skip interactive confirmation prompts.

.PARAMETER WhatIf
    Show what would be deleted without actually deleting anything (dry run).

.EXAMPLE
    .\uninstall-cleanup.ps1 -WhatIf
    # Shows what would be removed without touching the file system.

.EXAMPLE
    .\uninstall-cleanup.ps1 -Force
    # Removes all CE AI Suite user data without prompting.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$ceaiRoot = Join-Path $env:LOCALAPPDATA 'CEAISuite'

if (-not (Test-Path $ceaiRoot)) {
    Write-Host "Nothing to clean up - '$ceaiRoot' does not exist."
    exit 0
}

# Enumerate what will be removed
$items = @(
    @{ Path = (Join-Path $ceaiRoot 'settings.json');  Desc = 'Settings and DPAPI-encrypted credentials' }
    @{ Path = (Join-Path $ceaiRoot 'skills');          Desc = 'Custom user skills' }
    @{ Path = (Join-Path $ceaiRoot 'logs');            Desc = 'Application logs' }
    @{ Path = (Join-Path $ceaiRoot 'sessions');        Desc = 'Session data' }
    @{ Path = (Join-Path $ceaiRoot 'recovery');        Desc = 'Crash recovery files' }
    @{ Path = (Join-Path $ceaiRoot 'memory');          Desc = 'Agent memory store' }
)

Write-Host ''
Write-Host 'CE AI Suite - Uninstall Cleanup' -ForegroundColor Cyan
Write-Host '================================' -ForegroundColor Cyan
Write-Host ''
Write-Host "Target directory: $ceaiRoot"
Write-Host ''

# Show what exists
$existingItems = @()
foreach ($item in $items) {
    if (Test-Path $item.Path) {
        $existingItems += $item
        Write-Host "  [FOUND]   $($item.Desc): $($item.Path)" -ForegroundColor Yellow
    } else {
        Write-Host "  [ABSENT]  $($item.Desc): $($item.Path)" -ForegroundColor DarkGray
    }
}

# Also list any other files/directories in the root that aren't in our known list
$knownLeaves = $items | ForEach-Object { Split-Path $_.Path -Leaf }
$extraItems = Get-ChildItem -Path $ceaiRoot -ErrorAction SilentlyContinue |
    Where-Object { $knownLeaves -notcontains $_.Name }
foreach ($extra in $extraItems) {
    $existingItems += @{ Path = $extra.FullName; Desc = "Other: $($extra.Name)" }
    Write-Host "  [FOUND]   Other: $($extra.FullName)" -ForegroundColor Yellow
}

Write-Host ''

if ($existingItems.Count -eq 0) {
    Write-Host 'No files to clean up.' -ForegroundColor Green
    exit 0
}

# Confirm unless -Force
if (-not $Force -and -not $WhatIfPreference) {
    $answer = Read-Host "Remove $($existingItems.Count) item(s) listed above? [y/N]"
    if ($answer -ne 'y' -and $answer -ne 'Y') {
        Write-Host 'Cancelled.' -ForegroundColor Red
        exit 1
    }
}

# Delete individual items first, then the root directory
foreach ($item in $existingItems) {
    if ($PSCmdlet.ShouldProcess($item.Path, "Remove $($item.Desc)")) {
        try {
            Remove-Item -Path $item.Path -Recurse -Force -ErrorAction Stop
            Write-Host "  Removed: $($item.Path)" -ForegroundColor Green
        } catch {
            Write-Warning "  Failed to remove $($item.Path): $_"
        }
    }
}

# Remove the root directory if now empty (or if everything inside was deleted)
if (Test-Path $ceaiRoot) {
    $remaining = Get-ChildItem -Path $ceaiRoot -ErrorAction SilentlyContinue
    if ($remaining.Count -eq 0) {
        if ($PSCmdlet.ShouldProcess($ceaiRoot, 'Remove empty CEAISuite directory')) {
            Remove-Item -Path $ceaiRoot -Force
            Write-Host "  Removed: $ceaiRoot" -ForegroundColor Green
        }
    } else {
        Write-Host ''
        Write-Warning "Directory '$ceaiRoot' still contains $($remaining.Count) item(s) and was not removed."
    }
}

Write-Host ''
Write-Host 'Cleanup complete.' -ForegroundColor Green
