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
