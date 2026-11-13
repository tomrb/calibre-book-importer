param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\CalibreBookImporter\CalibreBookImporter.csproj"
$projectDirectory = Split-Path -Parent $project
$skillSource = Join-Path $repositoryRoot "skills\calibre-ebook-workflow\SKILL.md"
$appRoot = Join-Path $env:APPDATA "com.github.githubapp"
$helperDestination = Join-Path $appRoot "calibre-book-importer"
$skillDirectory = Join-Path $appRoot "app-skills\calibre-ebook-workflow"
$skillDestination = Join-Path $skillDirectory "SKILL.md"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$nonce = [Guid]::NewGuid().ToString("N")
$staging = Join-Path $appRoot "calibre-book-importer.staging-$nonce"
$helperBackup = Join-Path $appRoot "calibre-book-importer.backup-$stamp-$nonce"
$skillBackup = Join-Path $skillDirectory "SKILL.md.backup-$stamp-$nonce"
$skillStaging = Join-Path $skillDirectory "SKILL.md.installing-$nonce"
$helperWasMoved = $false
$skillExisted = Test-Path -LiteralPath $skillDestination

if (-not (Test-Path -LiteralPath $project)) {
    throw "Importer project not found: $project"
}
if (-not (Test-Path -LiteralPath $skillSource)) {
    throw "Copilot workflow skill not found: $skillSource"
}

New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
New-Item -ItemType Directory -Path $skillDirectory -Force | Out-Null

try {
    dotnet publish $project --configuration $Configuration --output $staging --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
    Copy-Item -LiteralPath $project -Destination $staging
    Get-ChildItem -LiteralPath $projectDirectory -Filter "*.cs" -File |
        Copy-Item -Destination $staging

    Copy-Item -LiteralPath $skillSource -Destination $skillStaging
    if ($skillExisted) {
        Copy-Item -LiteralPath $skillDestination -Destination $skillBackup
    }

    if (Test-Path -LiteralPath $helperDestination) {
        Move-Item -LiteralPath $helperDestination -Destination $helperBackup
        $helperWasMoved = $true
    }
    Move-Item -LiteralPath $staging -Destination $helperDestination
    Move-Item -LiteralPath $skillStaging -Destination $skillDestination -Force

    Write-Output "Installed .NET helper: $helperDestination\CalibreBookImporter.csproj"
    Write-Output "Installed Copilot skill: $skillDestination"
    if ($helperWasMoved) {
        Write-Output "Previous helper retained at: $helperBackup"
    }
    if ($skillExisted) {
        Write-Output "Previous skill retained at: $skillBackup"
    }
}
catch {
    if (Test-Path -LiteralPath $helperDestination) {
        Remove-Item -LiteralPath $helperDestination -Recurse -Force
    }
    if ($helperWasMoved -and (Test-Path -LiteralPath $helperBackup)) {
        Move-Item -LiteralPath $helperBackup -Destination $helperDestination
    }
    if ($skillExisted -and (Test-Path -LiteralPath $skillBackup)) {
        Copy-Item -LiteralPath $skillBackup -Destination $skillDestination -Force
    }
    elseif (-not $skillExisted -and (Test-Path -LiteralPath $skillDestination)) {
        Remove-Item -LiteralPath $skillDestination -Force
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    if (Test-Path -LiteralPath $skillStaging) {
        Remove-Item -LiteralPath $skillStaging -Force
    }
}
