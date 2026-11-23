$ErrorActionPreference = 'Stop'
$name = 'GatherBuddyReborn'
$version = $env:PLUGIN_VERSION
$dll = Get-Item -LiteralPath "build/$name.dll"
if ([Reflection.AssemblyName]::GetAssemblyName($dll.FullName).Version.ToString() -cne $version) {
    throw 'Plugin assembly version differs from the release tag.'
}

$manifest = Get-Content -Raw -LiteralPath manifest.json | ConvertFrom-Json -AsHashtable
if ($manifest.InternalName -cne $name) { throw 'Wrong plugin manifest.' }
$manifest.RepoUrl = "https://github.com/$env:GITHUB_REPOSITORY"
$manifest.AssemblyVersion = $version
$manifest.TestingAssemblyVersion = $version
$manifest | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8NoBOM -LiteralPath "build/$name.json"

$null = New-Item -ItemType Directory -Path publish -Force
Copy-Item -LiteralPath "build/$name.json" -Destination "publish/$name.json"
# The Dalamud SDK creates a second manifest and latest.zip under this directory.
# Both belong to the SDK package and would duplicate the versioned release payload.
$sdkPackage = "build/$name"
if (Test-Path -LiteralPath "$sdkPackage/latest.zip") { Remove-Item -Recurse -LiteralPath $sdkPackage }
Get-ChildItem -LiteralPath build -File -Filter '*.pdb' | Remove-Item

foreach ($file in "$name.dll", "$name.json", 'raphael-cli.exe', 'LICENSE.raphael-rs.txt',
        'LICENSE.GatherBuddyReborn.txt', 'LICENSE.RebornMaterial-LGPL-3.0.txt',
        'LICENSE.RebornMaterial-GPL-3.0.txt', 'RebornMaterial-NOTICE.txt') {
    if ((Get-Item -LiteralPath "build/$file").Length -eq 0) { throw "Empty release component: $file" }
}
$zip = "publish/$name-$version.zip"
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path build).Path, $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($zip))
Get-FileHash -Algorithm SHA256 -LiteralPath $zip, "publish/$name.json"
