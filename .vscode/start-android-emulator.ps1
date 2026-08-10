param(
  [string]$AndroidSdk = "C:\Users\admin\AppData\Local\Android\Sdk",
  [string]$AvdName = "Pixel_8_Pro",
  [int]$TimeoutSeconds = 300,
  [switch]$NoSnapshotLoad
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

# Returns adb's stdout lines; its diagnostics never abort the script.
function Invoke-Adb {
  param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

  $previous = $ErrorActionPreference
  $ErrorActionPreference = "Continue"
  try {
    return @(& $adb @Arguments 2>$null)
  } catch {
    return @()
  } finally {
    $ErrorActionPreference = $previous
  }
}

function Get-EmulatorAvdName {
  param([string]$Serial)

  $output = Invoke-Adb -s $Serial emu avd name
  if ($LASTEXITCODE -ne 0) {
    return $null
  }

  foreach ($line in $output) {
    $value = "$line".Trim()
    if ($value -and $value -ne "OK") {
      return $value
    }
  }

  return $null
}

function Get-AdbEmulators {
  $devices = Invoke-Adb devices
  foreach ($line in $devices) {
    if ($line -match "^(emulator-\d+)\s+(\S+)") {
      $serial = $Matches[1]
      [pscustomobject]@{
        Serial = $serial
        State = $Matches[2]
        AvdName = Get-EmulatorAvdName -Serial $serial
      }
    }
  }
}

function Get-TargetEmulator {
  $emulators = @(Get-AdbEmulators)
  $target = $emulators | Where-Object { $_.AvdName -eq $AvdName } | Select-Object -First 1
  if ($target) {
    return $target
  }

  # A booting emulator does not answer `emu avd name` yet; a single unnamed one is this launch. An emulator
  # running a different AVD is never adopted, or the build would deploy to the wrong device.
  $unnamed = @($emulators | Where-Object { -not $_.AvdName })
  if ($emulators.Count -eq 1 -and $unnamed.Count -eq 1) {
    return $emulators[0]
  }

  return $null
}

# Warns when the emulator runs in another Windows session: adb sees it, but its window is on a desktop
# this session cannot show (an emulator started over ssh lands in session 0).
function Show-ForeignSessionWarning {
  $process = Get-RunningAvdProcess
  if (-not $process -or $process.SessionId -eq (Get-Process -Id $PID).SessionId) {
    return
  }

  Write-Host "Emulator '$AvdName' (PID $($process.ProcessId)) runs in Windows session $($process.SessionId), this script is in session $((Get-Process -Id $PID).SessionId): its window is not visible here."
}

function Get-RunningAvdProcess {
  try {
    Get-CimInstance Win32_Process -Filter "Name = 'emulator.exe'" |
      Where-Object { $_.CommandLine -and $_.CommandLine.Contains("-avd $AvdName") } |
      Select-Object -First 1
  } catch {
    return $null
  }
}

$target = Get-TargetEmulator
$avdProcess = Get-RunningAvdProcess
if (-not $target -and -not $avdProcess) {
  Write-Host "Starting Android emulator '$AvdName'..."
  $emulatorArgs = @("-avd", $AvdName)
  if ($NoSnapshotLoad) {
    $emulatorArgs += "-no-snapshot-load"
    Write-Host "Quick Boot snapshot loading is disabled for this launch."
  }

  Start-Process -FilePath $emulator -ArgumentList $emulatorArgs
} elseif ($target) {
  Write-Host "Android emulator is already running: $($target.Serial) ($($target.State))"
  Show-ForeignSessionWarning
} else {
  Write-Host "Android emulator process is already running for '$AvdName'; waiting for adb..."
  Show-ForeignSessionWarning
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$waitStarted = Get-Date
$serial = $null
$adbRestarted = $false
$lastState = $null
do {
  Start-Sleep -Seconds 2
  $target = Get-TargetEmulator

  if ($target) {
    if ($target.State -eq "device") {
      $serial = $target.Serial
      break
    }

    $stateMessage = "$($target.Serial):$($target.State)"
    if ($lastState -ne $stateMessage) {
      Write-Host "Android emulator is visible to adb but not ready yet: $stateMessage"
      $lastState = $stateMessage
    }

    if (-not $adbRestarted -and $target.State -eq "offline" -and (Get-Date) -ge $waitStarted.AddSeconds(10)) {
      Write-Host "adb reports the emulator as offline; restarting adb server once..."
      Invoke-Adb kill-server | Out-Null
      Invoke-Adb start-server | Out-Null
      $adbRestarted = $true
    }
  }
} while (-not $serial -and (Get-Date) -lt $deadline)

if (-not $serial) {
  $devices = (Invoke-Adb devices -l) -join [Environment]::NewLine
  throw "Timed out waiting for Android emulator '$AvdName' to connect to adb as a ready device. Current adb devices:$([Environment]::NewLine)$devices"
}

Write-Host "Waiting for Android emulator boot: $serial"
Invoke-Adb -s $serial wait-for-device | Out-Null

do {
  # A device that drops out mid-boot answers with nothing; keep polling until the deadline.
  $bootCompleted = ((Invoke-Adb -s $serial shell getprop sys.boot_completed) -join "").Trim()
  if ($bootCompleted -eq "1") {
    Write-Host "Android emulator is ready: $serial"
    Write-Output $serial
    exit 0
  }

  Start-Sleep -Seconds 2
} while ((Get-Date) -lt $deadline)

throw "Timed out waiting for Android emulator '$AvdName' to finish booting."
