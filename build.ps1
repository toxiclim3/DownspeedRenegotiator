$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path $csc)) {
    throw "csc.exe not found at $csc"
}
Push-Location $PSScriptRoot
try {
    & $csc /nologo /target:winexe /optimize+ /out:DownspeedRenegotiator.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\Program.cs
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    Write-Host "Built DownspeedRenegotiator.exe"
}
finally {
    Pop-Location
}
