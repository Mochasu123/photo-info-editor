# One-shot build + publish + git push for Photo Info Editor.
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\build-push.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

Write-Host '==> dotnet build (Debug, for the desktop dev shortcut)'
dotnet build .\PhotoLocationEditor.sln -c Debug

Write-Host '==> dotnet test'
dotnet test .\tests\PhotoLocationEditor.App.Tests\PhotoLocationEditor.App.Tests.csproj -c Debug --no-build

Write-Host '==> dotnet publish (self-contained Release)'
dotnet publish .\src\PhotoLocationEditor.App\PhotoLocationEditor.App.Modern.csproj `
    -c Release -r win-x64 --self-contained true `
    -o .\dist\publish

Write-Host '==> Package zip'
New-Item -ItemType Directory -Force -Path .\dist | Out-Null
Compress-Archive -Path .\dist\publish\* -DestinationPath .\dist\PhotoInfoEditor-0.3.0-win-x64.zip -Force

Write-Host '==> Desktop shortcut check'
& .\scripts\check-shortcut.ps1

Write-Host '==> Git identity check'
if (-not (git config user.email)) { git config user.email "dev@local" }
if (-not (git config user.name)) { git config user.name "Photo Info Editor Dev" }

Write-Host '==> Git commit and push'
git add -A
git commit -m "Modernize UI; remove backup mode; harden perf and safety"
git push origin main

Write-Host 'Done.'
