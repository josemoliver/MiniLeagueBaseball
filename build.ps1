param(
    [Parameter(Position=0, Mandatory=$true)]
    [ValidateSet("Run","Build","Restore","Clean")]
    [string]$Action,

    [Parameter(Position=1, Mandatory=$false)]
    [int]$Port = 0
)

function Show-Usage {
    Write-Host "Usage: .\build.ps1 -Action <Run|Build|Restore|Clean> [port]"
    Write-Host "Examples:"
    Write-Host "  .\build.ps1 -Action Run 5001    # runs app on http://localhost:5001"
    Write-Host "  .\build.ps1 -Action Build      # builds the solution"
    exit 1
}

# WHY: a previous "Run" (or the dev server started from an IDE) often still holds the port, and Kestrel then
# fails with "address already in use". Free the port when the owner is this game; never touch another program.
function Stop-GameOnPort {
    param([int[]]$Ports)

    foreach ($p in $Ports) {
        $owners = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique
        foreach ($id in $owners) {
            $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $id" -ErrorAction SilentlyContinue
            if (-not $proc) { continue }

            # NOTE: the server runs either as MiniLeague.Web.exe or as "dotnet ...MiniLeague.Web.dll", so the
            # process name alone is not enough; the command line is what identifies this game.
            $isGame = $proc.Name -like 'MiniLeague.Web*' -or $proc.CommandLine -match 'MiniLeague\.Web'
            if ($isGame) {
                Write-Host "Port $p is in use by another instance of the game (PID $id, $($proc.Name)). Stopping it."
                Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
                # Give the OS a moment to release the socket before Kestrel binds it again.
                for ($i = 0; $i -lt 20 -and (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue); $i++) {
                    Start-Sleep -Milliseconds 250
                }
            }
            else {
                Write-Error "Port $p is in use by $($proc.Name) (PID $id), which is not this game. Close it or pick another port: .\build.ps1 -Action Run <port>"
                exit 3
            }
        }
    }
}

# The ports "dotnet run" will use: the one asked for, else the ones in the launch profile.
function Get-RunPorts {
    param([int]$Requested, [string]$ProjectPath)

    if ($Requested -gt 0) { return @($Requested) }
    $settings = Join-Path $ProjectPath 'Properties\launchSettings.json'
    if (-not (Test-Path $settings)) { return @() }
    $urls = (Get-Content $settings -Raw | ConvertFrom-Json).profiles.PSObject.Properties.Value |
        ForEach-Object { $_.applicationUrl } | Where-Object { $_ }
    return @($urls -split ';' | ForEach-Object { if ($_ -match ':(\d+)') { [int]$Matches[1] } } | Select-Object -Unique)
}

if (-not $Action) { Show-Usage }

switch ($Action.ToLower()) {
    'run' {
        if ($Port -gt 0) {
            $url = "http://localhost:$Port"
            Write-Host "Starting MiniLeague.Web on $url"
            $env:ASPNETCORE_URLS = $url
        }
        else {
            Write-Host "Starting MiniLeague.Web on default URLs"
        }

        $projectPath = Join-Path -Path $PSScriptRoot -ChildPath 'src\MiniLeague.Web'
        if (-not (Test-Path $projectPath)) {
            Write-Error "Project folder not found: $projectPath"
            exit 2
        }

        Stop-GameOnPort -Ports (Get-RunPorts -Requested $Port -ProjectPath $projectPath)

        Push-Location $projectPath
        try {
            dotnet run
        }
        finally {
            Pop-Location
        }
    }

    'build' {
        Write-Host "Building solution..."
        dotnet build "${PSScriptRoot}\MiniLeagueBaseball.slnx"
    }

    'restore' {
        Write-Host "Restoring packages..."
        dotnet restore "${PSScriptRoot}\MiniLeagueBaseball.slnx"
    }

    'clean' {
        Write-Host "Cleaning solution..."
        dotnet clean "${PSScriptRoot}\MiniLeagueBaseball.slnx"
    }

    default {
        Show-Usage
    }
}
