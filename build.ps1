# Builds "dist/WARP 新开关/" and the release zip. Needs the .NET SDK (any version 6 or newer).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$Version = '2.1.0'
$AppName = 'WARP 新开关'

& ./fetch-deps.ps1

dotnet build src/WarpSwitchNext.csproj -c Release -o build/app "-p:Version=$Version" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$dist = Join-Path 'dist' $AppName
if (Test-Path 'dist') { Remove-Item -Recurse -Force 'dist' }
New-Item -ItemType Directory -Force -Path (Join-Path $dist 'core') | Out-Null

Copy-Item 'build/app/WarpSwitchNext.exe' (Join-Path $dist "$AppName.exe")
Copy-Item 'build/app/WarpSwitchNext.exe.config' (Join-Path $dist "$AppName.exe.config")
Copy-Item 'build/core/usque.exe', 'build/core/sing-box.exe' (Join-Path $dist 'core')
Copy-Item 'LICENSE' (Join-Path $dist 'LICENSE.txt')
Copy-Item 'USQUE-LICENSE.txt' $dist
# sing-box is GPL-3.0: ship its license and say where its exact source is.
if (Test-Path 'build/vendor/sing-box/LICENSE') { Copy-Item 'build/vendor/sing-box/LICENSE' (Join-Path $dist 'SING-BOX-LICENSE.txt') }
$notice = "core\usque.exe    usque 4.2.1, MIT license (USQUE-LICENSE.txt)`r`n  https://github.com/Diniboy1123/usque/releases/tag/v4.2.1`r`n`r`n" +
          "core\sing-box.exe sing-box 1.14.2, GPL-3.0-or-later (SING-BOX-LICENSE.txt), built unmodified from`r`n  https://github.com/SagerNet/sing-box/tree/v1.14.2`r`n" +
          "  go build -trimpath -ldflags `"-s -w`" ./cmd/sing-box (GOOS=windows GOARCH=amd64 CGO_ENABLED=0)`r`n"
[IO.File]::WriteAllText((Join-Path (Resolve-Path $dist) 'THIRD-PARTY.txt'), $notice, (New-Object Text.UTF8Encoding $false))

# Plain-text guide with a BOM and Windows line endings so every Notepad version shows the Chinese correctly.
$guide = (Get-Content -Raw -Encoding utf8 'docs/使用说明.txt') -replace "`r?`n", "`r`n"
[IO.File]::WriteAllText((Join-Path (Resolve-Path $dist) '使用说明.txt'), $guide, (New-Object Text.UTF8Encoding $true))

$zip = "dist/WARP-Switch-Next-Windows-$Version.zip"
Compress-Archive -Path $dist -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Built $zip"
Write-Host "SHA-256 $hash"
# Tell the GitHub workflow which version was built (used to name the Release).
if ($env:GITHUB_ENV) { "APP_VERSION=$Version" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8 }
