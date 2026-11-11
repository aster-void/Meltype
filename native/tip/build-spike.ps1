# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 aster-void
#
# spike (#1670): stub の DLL と、入れ替える impl の DLL 2 つを native\tip\bin\spike に作る (x64 だけ)。
#   powershell -ExecutionPolicy Bypass -File native\tip\build-spike.ps1
# 出力:
#   bin\spike\MeltypeTip.dll   stub (Dll.cpp + Stub.cpp)。これを regsvr32 で登録する
#   bin\spike\impl\a.dll・b.dll 今の MeltypeTip.dll (中身は同じ。パスが違えば別のモジュールとして読み込まれる)
#   bin\spike\current.txt      stub が読み込む impl のファイル名 (a.dll)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
& (Join-Path $here 'build.ps1')

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat'
$env:PATH = (Split-Path $vswhere) + ';' + $env:PATH

$out = Join-Path $here 'bin\spike'
$obj = Join-Path $here 'obj\spike'
New-Item -ItemType Directory -Force -Path $out, $obj, (Join-Path $out 'impl') | Out-Null
$cmd = @(
    "`"$vcvars`" x64 >nul",
    "cd /d `"$here`"",
    "rc /nologo /fo `"$obj\MeltypeTip.res`" MeltypeTip.rc",
    "cl /nologo /std:c++17 /EHsc /W4 /wd4100 /utf-8 /DUNICODE /D_UNICODE /DMELTYPE_TIP_STUB /O2 /MT /DNDEBUG /Fo`"$obj\\`" /Fd`"$obj\\`" /LD Dll.cpp Stub.cpp `"$obj\MeltypeTip.res`" /link /DEF:MeltypeTip.def /IMPLIB:`"$obj\MeltypeTip.lib`" /OUT:`"$out\MeltypeTip.dll`" ole32.lib oleaut32.lib uuid.lib user32.lib advapi32.lib"
) -join ' && '
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw 'stub のビルドに失敗しました。' }

foreach ($name in 'a.dll', 'b.dll') { Copy-Item -LiteralPath (Join-Path $here 'bin\x64\MeltypeTip.dll') -Destination (Join-Path $out "impl\$name") -Force }
[IO.File]::WriteAllText((Join-Path $out 'current.txt'), 'a.dll')
Write-Host 'できました:' $out
