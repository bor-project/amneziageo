$base = $PSScriptRoot
$inbox = Join-Path $base 'inbox'
$outbox = Join-Path $base 'outbox'
$stop = Join-Path $base 'stop'
New-Item -ItemType Directory -Force $inbox, $outbox | Out-Null
Remove-Item $stop -Force -ErrorAction SilentlyContinue
. (Join-Path $base 'lib.ps1')
Set-Content -Path (Join-Path $base 'runner.alive') -Value $PID
while (-not (Test-Path $stop)) {
    foreach ($job in Get-ChildItem $inbox -Filter '*.ps1' | Sort-Object Name) {
        $result = Join-Path $outbox ($job.BaseName + '.txt')
        try {
            . $job.FullName *>&1 | Out-String -Width 400 | Out-File -FilePath $result -Encoding utf8
        }
        catch {
            ($_ | Out-String -Width 400) | Out-File -FilePath $result -Encoding utf8 -Append
        }
        Remove-Item $job.FullName -Force
        New-Item -ItemType File -Force ($result + '.done') | Out-Null
    }
    Start-Sleep -Milliseconds 250
}
Remove-Item (Join-Path $base 'runner.alive') -Force -ErrorAction SilentlyContinue
