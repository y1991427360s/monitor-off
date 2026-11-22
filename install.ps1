# 构建程序并在项目目录创建快捷方式；加 -Desktop 同时放到桌面
# 加 -Startup 在启动文件夹放一个快捷方式：开机后在托盘待命，Ctrl+Alt+F12 随时可用
param([switch]$Desktop, [switch]$NoBuild, [switch]$Startup)
$root = $PSScriptRoot
$exe = Join-Path $root 'bin\分屏关闭显示器.exe'

function Get-OffCount {
    try {
        $off = (Get-Content -LiteralPath (Join-Path $env:APPDATA 'MonitorOff\settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json).Off
        if ($null -eq $off) { 0 } else { @($off).Count }
    } catch { 0 }
}

if (-not $NoBuild) {
    $running = Get-Process 分屏关闭显示器 -ErrorAction SilentlyContinue
    if ($running) {
        # 有屏关着时先恢复再结束进程，避免屏关着却没有热键可用
        if ((Get-OffCount) -gt 0 -and (Test-Path -LiteralPath $exe)) {
            Write-Output '有显示器处于关闭状态，先恢复…'
            & $exe --restore
            for ($i = 0; $i -lt 30 -and (Get-OffCount) -gt 0; $i++) { Start-Sleep -Milliseconds 500 }
            if ((Get-OffCount) -gt 0) { throw '显示器没有全部恢复，已中止；请先按 Ctrl+Alt+F12 恢复后重试' }
        }
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
    }
    $start = Get-Date
    dotnet publish (Join-Path $root 'app\MonitorOff.csproj') -c Release -o (Join-Path $root 'bin') --nologo
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
    # 代码在 dll 里；exe 只是启动壳，增量构建时不会重写，不能拿它判断新旧
    $f = Get-Item ([IO.Path]::ChangeExtension($exe, '.dll'))
    if ($f.LastWriteTime -lt $start) { throw "产物未更新：$($f.LastWriteTime)" }
    Write-Output "已构建 $($f.FullName)  $($f.LastWriteTime)  $($f.Length) 字节"
    if ($running) {
        Start-Process -FilePath $exe -ArgumentList '--tray' -WorkingDirectory (Split-Path $exe)
        Write-Output '已重新启动到托盘'
    }
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
    foreach ($old in @('分屏关闭显示器.lnk', '远程模式-关闭实体屏.lnk', '恢复全部显示器.lnk')) {
        $p = Join-Path $d $old
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
    }
    New-Lnk (Join-Path $d '显示器控制面板.lnk') '' '打开显示器控制面板'
}
if ($Startup) {
    New-Lnk (Join-Path ([Environment]::GetFolderPath('Startup')) '分屏关闭显示器.lnk') '--tray' '开机后在托盘待命，Ctrl+Alt+F12 随时恢复显示器'
}
