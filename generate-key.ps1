param(
    [string]$KeyPath,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$sshKeygen = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh-keygen.exe'
if (-not (Test-Path -LiteralPath $sshKeygen -PathType Leaf)) {
    $cmd = Get-Command ssh-keygen -ErrorAction SilentlyContinue | Select-Object -First 1
    $sshKeygen = if ($cmd) { $cmd.Source } else { $null }
}
if ([string]::IsNullOrEmpty($sshKeygen)) {
    throw 'ssh-keygen.exe not found. Install the Windows OpenSSH Client optional feature.'
}

if (-not $PSBoundParameters.ContainsKey('KeyPath')) {
    $KeyPath = 'ssh\id_ed25519'
    $cfgPath = Join-Path $PSScriptRoot 'config.ini'
    if (Test-Path -LiteralPath $cfgPath -PathType Leaf) {
        foreach ($line in (Get-Content -LiteralPath $cfgPath -Encoding UTF8)) {
            if ($line -match '^\s*[#;]') {
                continue
            }
            if ($line -match '^\s*key_file\s*=\s*(.+?)\s*$') {
                $KeyPath = $Matches[1]
            }
        }
    }
    $baseDir = $PSScriptRoot
}
else {
    $baseDir = (Get-Location).ProviderPath
}

if ([string]::IsNullOrWhiteSpace($KeyPath) -or $KeyPath -match '["\r\n]' -or $KeyPath.EndsWith('\') -or $KeyPath.EndsWith('/')) {
    throw "Invalid key path: $KeyPath"
}

if (-not [System.IO.Path]::IsPathRooted($KeyPath)) {
    $KeyPath = [System.IO.Path]::GetFullPath((Join-Path $baseDir $KeyPath))
}
else {
    $KeyPath = [System.IO.Path]::GetFullPath($KeyPath)
}

if ($KeyPath.EndsWith('\') -or (Test-Path -LiteralPath $KeyPath -PathType Container)) {
    throw "Key path must be a file path, not a directory: $KeyPath"
}

$pubPath = "$KeyPath.pub"
if ((Test-Path -LiteralPath $KeyPath) -or (Test-Path -LiteralPath $pubPath)) {
    if (-not $Force) {
        throw "Key already exists at $KeyPath (pass -Force to overwrite)."
    }
    if (Test-Path -LiteralPath $pubPath -PathType Container) {
        throw "Public key path points to a directory: $pubPath"
    }
    if (Test-Path -LiteralPath $KeyPath) {
        Remove-Item -LiteralPath $KeyPath -Force
    }
    if (Test-Path -LiteralPath $pubPath) {
        Remove-Item -LiteralPath $pubPath -Force
    }
}

$keyDir = Split-Path -Parent $KeyPath
if (-not [string]::IsNullOrEmpty($keyDir)) {
    New-Item -ItemType Directory -Path $keyDir -Force | Out-Null
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $sshKeygen
$psi.Arguments = "-q -t ed25519 -C `"downspeed-renegotiator`" -N `"`" -f `"$KeyPath`""
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
try {
    [void]$proc.Start()
    $proc.StandardInput.Close()
    $stderrTask = $proc.StandardError.ReadToEndAsync()
    $stdout = $proc.StandardOutput.ReadToEnd()
    $proc.WaitForExit()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($proc.ExitCode -ne 0) {
        $detail = if (-not [string]::IsNullOrWhiteSpace($stderr)) { $stderr.Trim() } else { $stdout.Trim() }
        throw "ssh-keygen failed with exit code $($proc.ExitCode): $detail"
    }
}
finally {
    $proc.Dispose()
}

if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf) -or -not (Test-Path -LiteralPath $pubPath -PathType Leaf)) {
    throw "ssh-keygen did not create expected key files at $KeyPath"
}

$pubKey = (Get-Content -LiteralPath $pubPath -Raw -Encoding UTF8).Trim()
if ($pubKey -notmatch '^ssh-ed25519 ') {
    throw "Unexpected public key format in $pubPath"
}

# Match SshRunner.EnsureKeyPermissions in src\Program.cs using locale-independent SIDs.
try {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $currentUser = if ($identity) { $identity.User } else { $null }
    if ($currentUser) {
        $sysSid = New-Object System.Security.Principal.SecurityIdentifier ([System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
        $admSid = New-Object System.Security.Principal.SecurityIdentifier ([System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
        $fullControl = [System.Security.AccessControl.FileSystemRights]::FullControl
        $allow = [System.Security.AccessControl.AccessControlType]::Allow

        $acl = New-Object System.Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule ($sysSid, $fullControl, $allow)))
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule ($admSid, $fullControl, $allow)))
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule ($currentUser, $fullControl, $allow)))

        if ('System.IO.FileSystemAclExtensions' -as [type]) {
            [System.IO.FileSystemAclExtensions]::SetAccessControl((New-Object System.IO.FileInfo $KeyPath), $acl)
        }
        else {
            [System.IO.File]::SetAccessControl($KeyPath, $acl)
        }
    }
}
catch {
    Write-Warning "Could not tighten NTFS permissions on ${KeyPath}: $_"
}

Write-Host "Generated key pair:"
Write-Host "  Private: $KeyPath"
Write-Host "  Public:  $pubPath"
Write-Host "Paste this public key into your router's SSH Authorized Keys setting:"
Write-Output $pubKey
