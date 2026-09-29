# HikariZenTuner build script (ASCII only).
# Builds ZenStates-Core (net20) from the pinned upstream commit, then the helper (net48, x64),
# runs the offline self-test and copies the two binaries into dist\ with their SHA256.
param(
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$zsc = Join-Path $root 'third_party\ZenStates-Core'
$pinnedCommit = '8979d2790467f31cdf032fbf19f89fbf9693ff08'

git -C $root submodule update --init --recursive
if ($LASTEXITCODE -ne 0) { throw 'git submodule update failed.' }

$head = (git -C $zsc rev-parse HEAD).Trim()
if ($head -ne $pinnedCommit) { throw "ZenStates-Core is at $head, expected $pinnedCommit." }
# Untracked files count too: the SDK-style project compiles every *.cs under its folder.
$dirty = git -C $zsc status --porcelain --untracked-files=all
if ($dirty) { throw 'ZenStates-Core has local changes or extra files; build from the pinned commit only.' }
$shallow = (git -C $zsc rev-parse --is-shallow-repository).Trim()
if ($shallow -eq 'true') { throw 'ZenStates-Core is a shallow clone; its version number comes from the full commit count.' }

dotnet build (Join-Path $zsc 'ZenStates-Core.csproj') -c Release -f net20 -nologo
if ($LASTEXITCODE -ne 0) { throw 'ZenStates-Core build failed.' }

dotnet build (Join-Path $root 'HikariZenTuner.csproj') -c $Configuration -nologo
if ($LASTEXITCODE -ne 0) { throw 'HikariZenTuner build failed.' }

$out = Join-Path $root "bin\$Configuration"
$selfTest = & (Join-Path $out 'HikariZenTuner.exe') --self-test
if ($LASTEXITCODE -ne 0) { throw "Self-test failed: $selfTest" }
Write-Host $selfTest

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
foreach ($name in @('HikariZenTuner.exe', 'ZenStates-Core.dll')) {
  Copy-Item -LiteralPath (Join-Path $out $name) -Destination (Join-Path $dist $name) -Force
}
$lines = foreach ($name in @('HikariZenTuner.exe', 'ZenStates-Core.dll')) {
  $file = Join-Path $dist $name
  $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
  '{0}  {1}  {2}' -f $hash, (Get-Item -LiteralPath $file).Length, $name
}
$lines | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
$lines | Write-Host
