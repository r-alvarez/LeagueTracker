<#
.SYNOPSIS
  Build the agent's ScreenRecorderLib.dll: upstream v6.6.0 plus our HDR
  tone-map patch (hdr-tonemap.patch, see README.md next to it).

.DESCRIPTION
  Clones sskodje/ScreenRecorderLib at the pinned commit, applies the patch,
  builds the x64 Release C++/CLI assembly and drops ScreenRecorderLib.dll (+pdb)
  into deploy/screenrecorderlib/out, where publish-agent.ps1 picks it up over
  the stock NuGet copy. Needs MSBuild with the C++/CLI component and a Windows
  10/11 SDK - GitHub's windows runners have both; a dev box may not, which is
  why the release workflow, not the dev box, is the normal builder.

.PARAMETER WorkDir
  Scratch folder for the clone (default: a temp folder, removed afterwards).
#>
param(
    [string]$WorkDir = ""
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$out = Join-Path $here "out"
$repo = "https://github.com/sskodje/ScreenRecorderLib.git"
$tag = "v6.6.0"
# The tag's commit at the time the patch was written; a moved tag fails loudly.
$commit = "39ad1e2f1750fa06669b73743dbbaa25371dec21"

$cleanup = -not $WorkDir
if (-not $WorkDir) { $WorkDir = Join-Path ([IO.Path]::GetTempPath()) ("srl-" + [Guid]::NewGuid().ToString("n")) }
$src = Join-Path $WorkDir "ScreenRecorderLib"

# The library needs the C++ tools with ATL (CComPtr everywhere) and its
# vcxproj asks for the v143 (VS 2022) toolset - not every installed VS has
# both: GitHub's runner carries a VS 2026 without ATL next to a VS 2022 with
# it, and "the newest MSBuild" picked 2026 (atlbase.h missing). Prefer a VS
# 2022 with ATL, then any VS with ATL, then whatever MSBuild is on PATH.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) {
    $needs = @("Microsoft.Component.MSBuild", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "Microsoft.VisualStudio.Component.VC.ATL")
    $msbuild = & $vswhere -products * -version "[17.0,18.0)" -requires @needs -sort -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
    if (-not $msbuild) { $msbuild = & $vswhere -products * -requires @needs -sort -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1 }
}
if (-not $msbuild) { $msbuild = (Get-Command msbuild -ErrorAction SilentlyContinue).Source }
if (-not $msbuild) { throw "MSBuild with the C++ tools and ATL not found - install Visual Studio with the C++ workload (ATL, C++/CLI), or run this in the release workflow" }
Write-Host "  MSBuild: $msbuild"

Write-Host "Building ScreenRecorderLib $tag + hdr-tonemap.patch"
New-Item -ItemType Directory -Force $WorkDir | Out-Null
# autocrlf off: the patch matches upstream's committed bytes (mixed LF/CRLF),
# not whatever a Windows checkout would convert them to.
git -c core.autocrlf=false clone --quiet --branch $tag --depth 1 $repo $src
if ($LASTEXITCODE -ne 0) { throw "clone failed" }
$head = (git -C $src rev-parse HEAD).Trim()
if ($head -ne $commit) { throw "upstream $tag is $head, expected $commit - review the diff before re-pinning" }

git -C $src -c core.autocrlf=false apply --whitespace=nowarn -p1 (Join-Path $here "hdr-tonemap.patch")
if ($LASTEXITCODE -ne 0) { throw "patch did not apply" }

Push-Location $src
try {
    # ScreenRecorderLib.vcxproj (C++/CLI) references the native project, so
    # this builds both. The stock package is built the same way upstream.
    & $msbuild "ScreenRecorderLib\ScreenRecorderLib.vcxproj" /m /nologo /v:m /p:Configuration=Release /p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "ScreenRecorderLib build failed" }
} finally { Pop-Location }

$built = Join-Path $src "ScreenRecorderLib\bin\x64\Release\ScreenRecorderLib.dll"
if (-not (Test-Path $built)) { throw "build produced no ScreenRecorderLib.dll at $built" }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item $built (Join-Path $out "ScreenRecorderLib.dll") -Force
$pdb = [IO.Path]::ChangeExtension($built, ".pdb")
if (Test-Path $pdb) { Copy-Item $pdb (Join-Path $out "ScreenRecorderLib.pdb") -Force }
Write-Host "  $(Join-Path $out 'ScreenRecorderLib.dll') ($([math]::Round((Get-Item $built).Length / 1KB)) KB)"

if ($cleanup) { Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
