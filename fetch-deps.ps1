# Gets the two programs the app runs, the same ones the reference project (warp-masque-bypass) uses:
#   usque 4.2.1    - downloaded from its GitHub release and checked against a pinned SHA-256
#   sing-box 1.14.2 - built from source (needs Go 1.25+); only the parts the app uses, about 36 MB
#                     instead of 82 MB for the official build
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force -Path 'build/vendor', 'build/core' | Out-Null

$UsqueVersion = '4.2.1'
$UsqueSha = 'f6f7f0a1a2bc9bcc15cf563ec1f892d00690a92c086b23ed3211b802209099e7'
if (-not (Test-Path 'build/core/usque.exe')) {
    $name = "usque_${UsqueVersion}_windows_amd64.zip"
    $zip = "build/vendor/$name"
    Invoke-WebRequest -Uri "https://github.com/Diniboy1123/usque/releases/download/v$UsqueVersion/$name" -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $UsqueSha) { throw "usque SHA-256 verification failed: $hash" }
    Expand-Archive -Path $zip -DestinationPath 'build/vendor/usque' -Force
    Copy-Item 'build/vendor/usque/usque.exe' 'build/core/usque.exe' -Force
    Write-Host "Verified usque $UsqueVersion"
}

$SingBoxVersion = '1.14.2'
if (-not (Test-Path 'build/core/sing-box.exe')) {
    $src = 'build/vendor/sing-box'
    if (-not (Test-Path $src)) {
        git clone --quiet --depth 1 --branch "v$SingBoxVersion" https://github.com/SagerNet/sing-box.git $src
        if ($LASTEXITCODE -ne 0) { throw 'sing-box download failed' }
    }
    $out = (Join-Path (Resolve-Path 'build/core') 'sing-box.exe')
    Push-Location $src
    try {
        $env:CGO_ENABLED = '0'; $env:GOOS = 'windows'; $env:GOARCH = 'amd64'
        go build -trimpath -ldflags "-s -w -buildid= -X github.com/sagernet/sing-box/constant.Version=$SingBoxVersion" -o $out ./cmd/sing-box
        if ($LASTEXITCODE -ne 0) { throw 'sing-box build failed' }
    }
    finally { Pop-Location }
    Write-Host "Built sing-box $SingBoxVersion ($([int]((Get-Item $out).Length / 1MB)) MB)"
}
