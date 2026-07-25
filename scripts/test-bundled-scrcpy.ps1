param(
    [Parameter(Mandatory = $true)]
    [string]$DeviceId,
    [int]$MaxSize = 1280,
    [int]$BitRate = 2000000,
    [int]$MaxFps = 30
)

$ErrorActionPreference = "Stop"

function Read-Exactly {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)]
        [int]$Length
    )

    $buffer = [byte[]]::new($Length)
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($buffer, $offset, $Length - $offset)
        if ($read -eq 0) {
            throw "scrcpy socket closed while reading $Length bytes."
        }
        $offset += $read
    }
    return $buffer
}

function Read-Int32BigEndian {
    param([byte[]]$Buffer, [int]$Offset)
    return [int](
        ([uint32]$Buffer[$Offset] -shl 24) -bor
        ([uint32]$Buffer[$Offset + 1] -shl 16) -bor
        ([uint32]$Buffer[$Offset + 2] -shl 8) -bor
        [uint32]$Buffer[$Offset + 3]
    )
}

function Connect-ScrcpySocket {
    param(
        [int]$Port,
        [System.Diagnostics.Process]$ServerProcess,
        [bool]$ExpectDummyByte
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastError = $null
    while ($stopwatch.ElapsedMilliseconds -lt 8000) {
        if ($ServerProcess.HasExited) {
            $errorText = $ServerProcess.StandardError.ReadToEnd()
            throw "scrcpy server exited early ($($ServerProcess.ExitCode)): $errorText"
        }

        $client = [System.Net.Sockets.TcpClient]::new()
        $client.NoDelay = $true
        try {
            $connectTask = $client.ConnectAsync("127.0.0.1", $Port)
            if (-not $connectTask.Wait(600) -or -not $client.Connected) {
                throw "TCP connect timed out."
            }
            $client.ReceiveTimeout = 1500
            if ($ExpectDummyByte) {
                $dummy = Read-Exactly -Stream $client.GetStream() -Length 1
                if ($dummy[0] -ne 0) {
                    throw "scrcpy dummy byte is invalid."
                }
            }
            return $client
        }
        catch {
            $lastError = $_.Exception
            $client.Dispose()
            Start-Sleep -Milliseconds 80
        }
    }
    throw "Unable to connect to scrcpy socket within 8 seconds: $lastError"
}

$serverProcess = $null
$videoClient = $null
$controlClient = $null
$forwardedPort = $null
try {
    $packagePathOutput = & adb -s $DeviceId shell pm path com.adbcontrol.companion
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to query the installed companion APK."
    }
    $apkPath = ($packagePathOutput | Where-Object { $_ -like "package:*" } | Select-Object -First 1) -replace '^package:', ''
    if ([string]::IsNullOrWhiteSpace($apkPath)) {
        throw "The companion APK is not installed on $DeviceId."
    }

    $scidValue = Get-Random -Minimum 0x10000000 -Maximum 0x7fffffff
    $scid = $scidValue.ToString("x8")
    $socketName = "scrcpy_$scid"
    $forwardedPort = [int](& adb -s $DeviceId forward tcp:0 "localabstract:$socketName")
    if ($LASTEXITCODE -ne 0 -or $forwardedPort -le 0) {
        throw "Unable to create the scrcpy ADB forward."
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "adb"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    @(
        "-s", $DeviceId, "shell",
        "CLASSPATH=$apkPath", "app_process", "/", "com.genymobile.scrcpy.Server", "4.0",
        "scid=$scid", "log_level=info", "audio=false", "video=true", "control=true",
        "video_codec=h264", "max_size=$MaxSize", "video_bit_rate=$BitRate", "max_fps=$MaxFps",
        "tunnel_forward=true", "send_device_meta=false", "send_dummy_byte=true",
        "send_stream_meta=true", "send_frame_meta=true"
    ) | ForEach-Object { $startInfo.ArgumentList.Add($_) }
    $serverProcess = [System.Diagnostics.Process]::Start($startInfo)

    $videoClient = Connect-ScrcpySocket -Port $forwardedPort -ServerProcess $serverProcess -ExpectDummyByte $true
    $controlClient = Connect-ScrcpySocket -Port $forwardedPort -ServerProcess $serverProcess -ExpectDummyByte $false
    $videoStream = $videoClient.GetStream()
    $videoStream.ReadTimeout = 8000

    $codec = [System.Text.Encoding]::ASCII.GetString((Read-Exactly -Stream $videoStream -Length 4))
    if ($codec -ne "h264") {
        throw "Unexpected scrcpy video codec: $codec"
    }

    $sessionMetadata = Read-Exactly -Stream $videoStream -Length 12
    if (($sessionMetadata[0] -band 0x80) -eq 0) {
        throw "The scrcpy stream does not contain initial session metadata."
    }
    $width = Read-Int32BigEndian -Buffer $sessionMetadata -Offset 4
    $height = Read-Int32BigEndian -Buffer $sessionMetadata -Offset 8
    if ($width -le 0 -or $height -le 0) {
        throw "Invalid scrcpy frame size: ${width}x${height}."
    }

    $configBytes = 0
    $firstFrameBytes = 0
    while ($firstFrameBytes -eq 0) {
        $packetMetadata = Read-Exactly -Stream $videoStream -Length 12
        if (($packetMetadata[0] -band 0x80) -ne 0) {
            continue
        }
        $packetLength = Read-Int32BigEndian -Buffer $packetMetadata -Offset 8
        if ($packetLength -le 0 -or $packetLength -gt 16777216) {
            throw "Invalid scrcpy packet length: $packetLength."
        }
        $packet = Read-Exactly -Stream $videoStream -Length $packetLength
        if (($packetMetadata[0] -band 0x40) -ne 0) {
            $configBytes = $packet.Length
        }
        else {
            $firstFrameBytes = $packet.Length
        }
    }
    if ($configBytes -eq 0) {
        throw "The scrcpy stream did not send H.264 SPS/PPS before the first frame."
    }

    [pscustomobject]@{
        Result = "PASS"
        DeviceId = $DeviceId
        Source = "companion-apk"
        Classpath = $apkPath
        Codec = $codec
        Width = $width
        Height = $height
        CodecConfigBytes = $configBytes
        FirstFrameBytes = $firstFrameBytes
        ControlConnected = $controlClient.Connected
    } | Format-List
}
finally {
    if ($null -ne $videoClient) {
        $videoClient.Dispose()
    }
    if ($null -ne $controlClient) {
        $controlClient.Dispose()
    }
    if ($serverProcess -and -not $serverProcess.HasExited) {
        $serverProcess.Kill($true)
        $serverProcess.WaitForExit(3000) | Out-Null
    }
    if ($null -ne $serverProcess) {
        $serverProcess.Dispose()
    }
    if ($forwardedPort) {
        & adb -s $DeviceId forward --remove "tcp:$forwardedPort" | Out-Null
    }
}
