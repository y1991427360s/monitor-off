# 构建程序并在项目目录创建快捷方式；加 -Desktop 同时放到桌面
param([switch]$Desktop, [switch]$NoBuild)
$root = $PSScriptRoot
$exe = Join-Path $root 'bin\分屏关闭显示器.exe'

if (-not $NoBuild) {
    Get-Process 分屏关闭显示器 -ErrorAction SilentlyContinue | Stop-Process -Force
    $start = Get-Date
    dotnet publish (Join-Path $root 'app\MonitorOff.csproj') -c Release -o (Join-Path $root 'bin') --nologo
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
    $f = Get-Item $exe
    if ($f.LastWriteTime -lt $start) { throw "产物未更新：$($f.LastWriteTime)" }
    Write-Output "已构建 $($f.FullName)  $($f.LastWriteTime)  $($f.Length) 字节"
}

$sh = New-Object -ComObject WScript.Shell
function New-Lnk($path, $argsLine, $desc) {
    $l = $sh.CreateShortcut($path)
    $l.TargetPath = $exe
    $l.Arguments = $argsLine
    $l.WorkingDirectory = Split-Path $exe
    $l.Description = $desc
    $l.IconLocation = "$exe,0"
    $l.Save()
    Write-Output "已创建 $path"
}

$dirs = @($root)
if ($Desktop) { $dirs += [Environment]::GetFolderPath('Desktop') }
foreach ($d in $dirs) {
    New-Lnk (Join-Path $d '分屏关闭显示器.lnk') '' '选择关闭哪几台显示器'
    New-Lnk (Join-Path $d '远程模式-关闭实体屏.lnk') '--remote' '关闭全部支持 DDC 的实体屏，只留虚拟屏'
    New-Lnk (Join-Path $d '恢复全部显示器.lnk') '--restore' '把关掉的显示器全部恢复'
}
