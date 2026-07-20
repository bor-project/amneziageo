param(
  [string]$AndroidSdk = "C:\Users\admin\AppData\Local\Android\Sdk",
  [string]$AvdName = "Pixel_8_Pro",
  [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = "Stop"

$adb = Join-Path $AndroidSdk "platform-tools\adb.exe"
$emulator = Join-Path $AndroidSdk "emulator\emulator.exe"

if (-not (Test-Path -LiteralPath $adb)) {
  throw "adb.exe was not found at '$adb'. Check AndroidSdk."
}

if (-not (Test-Path -LiteralPath $emulator)) {
  throw "emulator.exe was not found at '$emulator'. Check AndroidSdk."
}

function Get-RunningEmulatorSerial {
  $devices = & $adb devices
  foreach ($line in $devices) {
    if ($line -match "^(emulator-\d+)\s+device$") {
      return $Matches[1]
    }
  }

  return $null
}

$serial = Get-RunningEmulatorSerial
if (-not $serial) {
  Write-Host "Starting Android emulator '$AvdName'..."
  Start-Process -FilePath $emulator -ArgumentList @("-avd", $AvdName)
} else {
  Write-Host "Android emulator is already running: $serial"
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
do {
  Start-Sleep -Seconds 2
  $serial = Get-RunningEmulatorSerial
} while (-not $serial -and (Get-Date) -lt $deadline)

if (-not $serial) {
  throw "Timed out waiting for Android emulator '$AvdName' to connect to adb."
}

Write-Host "Waiting for Android emulator boot: $serial"
& $adb -s $serial wait-for-device | Out-Null

do {
  $bootCompleted = (& $adb -s $serial shell getprop sys.boot_completed 2>$null).Trim()
  if ($bootCompleted -eq "1") {
    Write-Host "Android emulator is ready: $serial"
    exit 0
  }

  Start-Sleep -Seconds 2
} while ((Get-Date) -lt $deadline)

throw "Timed out waiting for Android emulator '$AvdName' to finish booting."
