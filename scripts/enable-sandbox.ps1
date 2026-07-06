Write-Host "启用 Windows Sandbox..." -ForegroundColor Cyan
Enable-WindowsOptionalFeature -Online -FeatureName "Containers-DisposableClientVM" -All
if ($LASTEXITCODE -eq 0) {
    Write-Host "安装完成。需要重启生效。" -ForegroundColor Green
    $choice = Read-Host "立即重启? (y/n)"
    if ($choice -eq 'y') { Restart-Computer -Confirm:$false }
}
pause
