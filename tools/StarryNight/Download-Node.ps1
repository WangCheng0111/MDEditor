param([ValidateSet('win-x64','win-x86','win-arm64')][string[]]$Architectures = @('win-x64','win-x86','win-arm64'))
$ErrorActionPreference = 'Stop'
$version = 'v22.23.3'
$base = "https://nodejs.org/dist/$version"
$sums = Invoke-RestMethod "$base/SHASUMS256.txt"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\MDEditor\Assets\StarryNight'))
foreach ($architecture in $Architectures) {
    $target = Join-Path $root "runtimes\$architecture"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $relative = "$architecture/node.exe"
    $matches = [regex]::Match($sums, "(?m)^([a-f0-9]{64})\s+$([regex]::Escape($relative))\r?$")
    if (!$matches.Success) { throw "Official Node checksum is missing: $relative" }
    $executable = Join-Path $target 'node.exe'
    if (!(Test-Path -LiteralPath $executable) -or
        (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $matches.Groups[1].Value) {
        Invoke-WebRequest "$base/$relative" -OutFile $executable
    }
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $matches.Groups[1].Value) {
        throw "Node download failed SHA256 verification: $architecture"
    }
    Write-Output "${architecture} Node ${version}: SHA256 verified"
}
Invoke-WebRequest "https://raw.githubusercontent.com/nodejs/node/$version/LICENSE" -OutFile (Join-Path $root 'NODE-LICENSE.txt')
