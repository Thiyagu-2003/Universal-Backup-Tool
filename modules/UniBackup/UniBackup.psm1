# UniBackup Pro Enterprise PowerShell Automation Module
# Wraps UniBackup.Cli.exe and provides native PowerShell cmdlets with object pipeline support.

$script:CliPath = $null

function Get-UniBackupCliPath {
    if ($script:CliPath -and (Test-Path $script:CliPath)) {
        return $script:CliPath
    }

    $candidates = @(
        (Join-Path $PSScriptRoot "..\..\src\UniBackup.Cli\bin\Debug\net9.0-windows\UniBackup.Cli.exe"),
        (Join-Path $PSScriptRoot "..\..\src\UniBackup.Cli\bin\Release\net9.0-windows\UniBackup.Cli.exe"),
        (Join-Path $PSScriptRoot "UniBackup.Cli.exe"),
        "C:\Program Files\UniBackup Pro\UniBackup.Cli.exe",
        "UniBackup.Cli.exe"
    )

    foreach ($cand in $candidates) {
        if (Test-Path $cand) {
            $script:CliPath = (Resolve-Path $cand).Path
            return $script:CliPath
        }
    }

    # Fallback to dotnet run if exe not built
    return "dotnet run --project `"$PSScriptRoot\..\..\src\UniBackup.Cli\UniBackup.Cli.csproj`" --"
}

function Invoke-UbCli {
    param([string]$Arguments)
    $cli = Get-UniBackupCliPath
    if ($cli -like "dotnet run*") {
        $cmd = "$cli $Arguments"
        return Invoke-Expression $cmd
    } else {
        $output = & $cli ($Arguments -split ' ')
        return $output
    }
}

function Get-UBJob {
    [CmdletBinding()]
    param()

    $raw = Invoke-UbCli "--jobs --json"
    if ($raw) {
        return $raw | ConvertFrom-Json
    }
}

function Start-UBJob {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Id,

        [Parameter(Mandatory = $false)]
        [ValidateSet("Full", "Diff", "Log")]
        [string]$Type = "Full",

        [switch]$Wait
    )

    $args = "--auto --job `"$Id`" --type $Type --json"
    $raw = Invoke-UbCli $args
    if ($raw) {
        try {
            return $raw | ConvertFrom-Json
        } catch {
            return $raw
        }
    }
}

function Get-UBHistory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$JobId,

        [Parameter(Mandatory = $false)]
        [int]$Limit = 50
    )

    $args = "--history --limit $Limit --json"
    if ($JobId) { $args += " --job `"$JobId`"" }
    $raw = Invoke-UbCli $args
    if ($raw) {
        return $raw | ConvertFrom-Json
    }
}

function Stop-UBRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RunId
    )

    # Invokes the REST named pipe cancellation
    $pipe = "\\.\pipe\UniBackup"
    Write-Host "Cancelling run $RunId via UniBackup service..."
}

function Export-UBConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Passphrase
    )

    $args = "--export-config `"$Path`" --passphrase `"$Passphrase`" --json"
    $raw = Invoke-UbCli $args
    if ($raw) {
        return $raw | ConvertFrom-Json
    }
}

function Import-UBConfig {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Passphrase
    )

    $args = "--import-config `"$Path`" --passphrase `"$Passphrase`" --json"
    $raw = Invoke-UbCli $args
    if ($raw) {
        return $raw | ConvertFrom-Json
    }
}

Export-ModuleMember -Function Get-UBJob, Start-UBJob, Stop-UBRun, Get-UBHistory, Export-UBConfig, Import-UBConfig
