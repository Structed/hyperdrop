$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# This script deliberately avoids cmdlets that live in autoloaded modules (ConvertTo-SecureString,
# New-Object, Test-Path, New-Item, ...) and uses .NET types directly instead. Module autoloading is
# fragile here: the host process may pass down a PSModulePath pointing at PowerShell 7's
# Core-edition modules, which Windows PowerShell 5.1 cannot load. Only New-PSSession,
# Invoke-Command and Remove-PSSession are used, and those live in the always-present
# Microsoft.PowerShell.Core snap-in.

function Send([string]$line) {
    [Console]::Out.WriteLine($line)
    [Console]::Out.Flush()
}

function Flatten([string]$text) {
    if ($null -eq $text) { return '' }
    return ($text -replace '\r?\n', ' ').Trim()
}

try {
    $user = [Console]::In.ReadLine()
    $pass = [Console]::In.ReadLine()
    $vmName = [Console]::In.ReadLine()
    $chunkSize = [int][Console]::In.ReadLine()

    $secure = [System.Security.SecureString]::new()
    foreach ($ch in $pass.ToCharArray()) { $secure.AppendChar($ch) }
    $secure.MakeReadOnly()

    $credential = [System.Management.Automation.PSCredential]::new($user, $secure)
    $pass = $null

    $session = New-PSSession -VMName $vmName -Credential $credential
}
catch {
    Send ('#FATAL ' + (Flatten $_.Exception.Message))
    exit 1
}

Send '#READY'

while ($true) {
    $line = [Console]::In.ReadLine()
    if ($null -eq $line -or $line -eq 'QUIT') { break }
    if (-not $line.StartsWith('COPY ')) { continue }

    $parts = $line.Substring(5).Split(' ')
    $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($parts[0]))
    $destination = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($parts[1]))
    $overwrite = $parts[2] -eq '1'
    $createPath = $parts[3] -eq '1'

    $stream = $null
    try {
        # Open the target file inside the guest and keep the handle in the remote session.
        Invoke-Command -Session $session -ScriptBlock {
            param($path, $create, $over)
            $ErrorActionPreference = 'Stop'

            $folder = [System.IO.Path]::GetDirectoryName($path)
            if ($folder -and -not [System.IO.Directory]::Exists($folder)) {
                if ($create) {
                    [System.IO.Directory]::CreateDirectory($folder) | Out-Null
                }
                else {
                    throw "The destination folder '$folder' does not exist in the guest."
                }
            }

            if ([System.IO.File]::Exists($path) -and -not $over) {
                throw "A file named '$path' already exists in the guest."
            }

            $global:HvdTarget = [System.IO.File]::Open(
                $path,
                [System.IO.FileMode]::Create,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
        } -ArgumentList $destination, $createPath, $overwrite

        $stream = [System.IO.File]::Open(
            $source,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)

        $buffer = [byte[]]::new($chunkSize)
        $sent = [int64]0

        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($read -eq $buffer.Length) {
                $payload = $buffer
            }
            else {
                $payload = [byte[]]::new($read)
                [Array]::Copy($buffer, $payload, $read)
            }

            # The leading comma keeps the array as a single argument instead of splatting it.
            Invoke-Command -Session $session -ScriptBlock {
                param($bytes)
                $global:HvdTarget.Write($bytes, 0, $bytes.Length)
            } -ArgumentList (, $payload)

            $sent += $read
            Send ('#P ' + $sent)
        }

        $stream.Dispose()
        $stream = $null

        Invoke-Command -Session $session -ScriptBlock {
            $global:HvdTarget.Flush()
            $global:HvdTarget.Dispose()
            $global:HvdTarget = $null
        }

        Send ('#P ' + $sent)
        Send '#OK'
    }
    catch {
        if ($null -ne $stream) {
            $stream.Dispose()
        }

        try {
            Invoke-Command -Session $session -ScriptBlock {
                if ($global:HvdTarget) {
                    $global:HvdTarget.Dispose()
                    $global:HvdTarget = $null
                }
            }
        }
        catch {
            # The session may already be gone; the outer error is the one that matters.
        }

        Send ('#E ' + (Flatten $_.Exception.Message))
    }
}

if ($session) {
    Remove-PSSession -Session $session -ErrorAction SilentlyContinue
}
