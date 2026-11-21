<#
  Installs ScanRelay silently on this Windows machine, checks what the installer
  promises, runs it again as a repair, then uninstalls and checks nothing is left.

  Run from the repo root in an elevated PowerShell 7:
      pwsh tests/windows/Test-Install.ps1 -Setup artifacts/installer/ScanRelaySetup.exe -Module release/scanrelay-sample-1.0.0.srmod

  It creates and removes a local account, a share, a firewall rule and a service,
  so run it on a build machine or a test server, not on a server in use.
#>
param(
    [Parameter(Mandatory)] [string] $Setup,
    [string] $Module,
    [string] $Root = "C:\ScanRelayTest",
    [int] $Port = 18443
)

$ErrorActionPreference = "Stop"
$failures = [System.Collections.Generic.List[string]]::new()
$checks = 0

function Check([string] $what, [scriptblock] $test) {
    $script:checks++
    try {
        if (& $test) { Write-Host "  ok    $what" }
        else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures.Add($what) }
    }
    catch { Write-Host "  FAIL  $what : $($_.Exception.Message)" -ForegroundColor Red; $script:failures.Add("$what : $($_.Exception.Message)") }
}

function Refused([scriptblock] $action) {
    try { & $action | Out-Null; return $false } catch { return $true }
}

function Run-Setup([string[]] $arguments) {
    $process = Start-Process -FilePath $Setup -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    if (Test-Path "$env:ProgramData\ScanRelay-setup.log") { Get-Content "$env:ProgramData\ScanRelay-setup.log" -Tail 60 | ForEach-Object { Write-Host "      $_" } }
    return $process.ExitCode
}

$Setup = (Resolve-Path $Setup).Path
$installFolder = "$Root\App"
$dataFolder = "$Root\Data"
$account = "srtest-printer"
$share = 'SrTest$'
$service = "ScanRelay"
$url = "https://localhost:$Port"

$answers = @{
    InstallFolder  = $installFolder
    DataFolder     = $dataFolder
    HostName       = "localhost"
    Port           = $Port
    PrinterAccount = $account
    ShareName      = $share
    UpdateCheck    = $false
    AddFirewallRule = $true
    Modules        = @()
}
if ($Module) {
    $answers.CustomModuleFiles = @((Resolve-Path $Module).Path)
    $answers.AcceptCustomModules = $true
}
New-Item -ItemType Directory -Force $Root | Out-Null
$answersFile = "$Root\answers.json"
$answers | ConvertTo-Json | Set-Content $answersFile -Encoding utf8

# ---------------------------------------------------------------- install
Write-Host "`nInstall"
$exit = Run-Setup @("/quiet", "/answers", "`"$answersFile`"")
Check "the installer finished without error (exit code $exit)" { $exit -eq 0 }
if ($exit -ne 0) { throw "The install failed, so the remaining checks cannot run." }

Write-Host "`nService"
$svc = Get-CimInstance Win32_Service -Filter "Name='$service'"
Check "the service exists and is running" { $svc -and $svc.State -eq "Running" }
Check "it runs as its own low-privilege account, not an administrator" { $svc.StartName -eq "NT SERVICE\$service" }
Check "it starts automatically" { $svc.StartMode -eq "Auto" }
Check "it is set to restart on failure" { (sc.exe qfailure $service | Out-String) -match "RESTART" }
$health = Invoke-RestMethod "$url/health" -SkipCertificateCheck
Check "the admin screen answers over HTTPS and reports ok" { $health.status -eq "ok" }
$setupPage = Invoke-WebRequest "$url/setup" -SkipCertificateCheck
Check "the first-run screen opens without sign-in on the server itself" { $setupPage.StatusCode -eq 200 -and $setupPage.Content -match "blazor.web.js" }
Check "the admin screen's script and styles are served" {
    (Invoke-WebRequest "$url/_framework/blazor.web.js" -SkipCertificateCheck).StatusCode -eq 200 -and
    (Invoke-WebRequest "$url/_content/ScanRelay.Admin/app.css" -SkipCertificateCheck).StatusCode -eq 200
}
$proxied = try { (Invoke-WebRequest "$url/setup" -SkipCertificateCheck -Headers @{ "X-Forwarded-For" = "203.0.113.9" }).StatusCode } catch { $_.Exception.Response.StatusCode.value__ }
Check "a request that came through a proxy is refused until setup is finished" { $proxied -eq 403 }

Write-Host "`nFiles and folders"
$install = Get-Content "$installFolder\install.json" -Raw | ConvertFrom-Json
Check "install.json records the data folder, address, account and share" {
    $install.DataFolder -eq $dataFolder -and $install.Url -eq $url -and $install.PrinterAccount -eq $account -and $install.ShareName -eq $share
}
Check "the data folder has Inbox, Store, Logs and the database" {
    (Test-Path "$dataFolder\Inbox") -and (Test-Path "$dataFolder\Store") -and (Test-Path "$dataFolder\Logs") -and (Test-Path "$dataFolder\scanrelay.db")
}
Check "the data folder is hidden" { (Get-Item $dataFolder -Force).Attributes -band [IO.FileAttributes]::Hidden }
$dataAcl = Get-Acl $dataFolder
$who = $dataAcl.Access | Where-Object { $_.AccessControlType -eq "Allow" } | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique
Check "only SYSTEM, Administrators and the service can open the data folder (found: $($who -join ', '))" {
    -not ($who | Where-Object { $_ -notin @("NT AUTHORITY\SYSTEM", "BUILTIN\Administrators", "NT SERVICE\$service") })
}
Check "the data folder does not inherit rights from its parent" { $dataAcl.AreAccessRulesProtected }
$installAcl = (Get-Acl $installFolder).Access | Where-Object { $_.IdentityReference.Value -eq "NT SERVICE\$service" -and $_.AccessControlType -eq "Allow" }
Check "the service cannot write to the program folder" {
    -not ($installAcl | Where-Object { $_.FileSystemRights -band ([Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Modify -bor [Security.AccessControl.FileSystemRights]::FullControl) -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::WriteData) })
}
Check "the service wrote a log file" { (Get-ChildItem "$dataFolder\Logs" -Filter "scanrelay-*.log").Count -ge 1 }

Write-Host "`nCertificates"
$graphCert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq $install.GraphCertThumbprint
$webCert = Get-ChildItem Cert:\LocalMachine\My | Where-Object Thumbprint -eq $install.WebCertThumbprint
Check "the Graph certificate is in the machine store with a private key" { $graphCert -and $graphCert.HasPrivateKey }
Check "the web certificate is in the machine store with a private key" { $webCert -and $webCert.HasPrivateKey }
Check "the web certificate names this address" { ($webCert.DnsNameList.Unicode -contains "localhost") }
Check "the Graph certificate's key cannot be exported" {
    Refused { $graphCert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, "x") }
}

Write-Host "`nPrinter account and share"
$user = Get-LocalUser -Name $account -ErrorAction SilentlyContinue
Check "the local printer account exists and is enabled" { $user -and $user.Enabled }
Check "its password never expires and it cannot change it" { -not $user.PasswordExpires -and -not $user.UserMayChangePassword }
$groups = Get-LocalGroup | Where-Object { (Get-LocalGroupMember $_ -ErrorAction SilentlyContinue | Where-Object Name -like "*\$account") }
Check "it is a member of no groups (found: $($groups.Name -join ', '))" { -not $groups }
secedit /export /cfg "$Root\secpol.cfg" /areas USER_RIGHTS | Out-Null
$policy = Get-Content "$Root\secpol.cfg"
$sid = $user.SID.Value
Check "it is denied interactive sign-in" { ($policy | Where-Object { $_ -match "^SeDenyInteractiveLogonRight" }) -match [regex]::Escape($sid) }
Check "it is denied Remote Desktop sign-in" { ($policy | Where-Object { $_ -match "^SeDenyRemoteInteractiveLogonRight" }) -match [regex]::Escape($sid) }
$smb = Get-SmbShare -Name $share -ErrorAction SilentlyContinue
Check "the hidden share exists and points at Inbox" { $smb -and $smb.Path -eq "$dataFolder\Inbox" }
$shareAccess = Get-SmbShareAccess -Name $share
Check "only the printer account has access to the share (found: $($shareAccess.AccountName -join ', '))" {
    @($shareAccess).Count -eq 1 -and $shareAccess.AccountName -like "*\$account" -and $shareAccess.AccessRight -eq "Change"
}
Check "Store, the database and the logs are not shared" { -not (Get-SmbShare | Where-Object { $_.Path -like "$dataFolder*" -and $_.Name -ne $share }) }

# Act as a printer: connect to the share with the printer account and try what it should and should not be able to do.
Add-Type -AssemblyName System.Security
$password = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
    [Convert]::FromBase64String($install.PrinterPasswordProtected), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine))
Check "the stored printer password is long and random" { $password.Length -ge 24 }
$person = [guid]::NewGuid().ToString()
New-Item -ItemType Directory "$dataFolder\Inbox\$person" | Out-Null
$unc = "\\127.0.0.1\$share"
net use $unc /delete /y 2>$null | Out-Null
$connected = (net use $unc $password "/user:$env:COMPUTERNAME\$account" 2>&1 | Out-String)
Check "a printer can connect to the share with the printer account ($($connected.Trim()))" { $LASTEXITCODE -eq 0 }
Check "it can write a scan into a person's folder" {
    [IO.File]::WriteAllText("$unc\$person\printer-test.pdf", "%PDF-1.4 written over SMB by the printer account")
    $true
}
Check "it cannot list the share" { Refused { Get-ChildItem $unc -ErrorAction Stop } }
Check "it cannot list a person's folder" { Refused { Get-ChildItem "$unc\$person" -ErrorAction Stop } }
Start-Sleep -Milliseconds 200
Check "it cannot read a scan back" {
    (Refused { [IO.File]::ReadAllText("$unc\$person\printer-test.pdf") }) -or -not (Test-Path "$dataFolder\Inbox\$person\printer-test.pdf")
}
[IO.File]::WriteAllText("$unc\$person\keep.pdf.part", "")
Check "it cannot delete a file" { Refused { [IO.File]::Delete("$unc\$person\keep.pdf.part"); if (Test-Path "$dataFolder\Inbox\$person\keep.pdf.part") { throw "still there" } else { $false } } }
net use $unc /delete /y | Out-Null

# The service account must be able to pick up what the printer account wrote.
$deadline = (Get-Date).AddSeconds(40)
while ((Get-Date) -lt $deadline -and (Test-Path "$dataFolder\Inbox\$person\printer-test.pdf")) { Start-Sleep -Seconds 1 }
Check "the service picked the scan up from Inbox within 40 seconds" { -not (Test-Path "$dataFolder\Inbox\$person\printer-test.pdf") }
Check "and moved it into Store" { (Get-ChildItem "$dataFolder\Store" -Filter *.pdf).Count -ge 1 }

Write-Host "`nFirewall and Installed apps"
Check "the firewall rule for the admin screen exists on the right port" {
    $rule = Get-NetFirewallRule -DisplayName "ScanRelay admin screen" -ErrorAction SilentlyContinue
    $rule -and ($rule | Get-NetFirewallPortFilter).LocalPort -eq "$Port"
}
$uninstallKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ScanRelay"
$registered = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
Check "Windows lists ScanRelay under Installed apps with Modify and Uninstall" {
    $registered.DisplayName -eq "ScanRelay" -and $registered.UninstallString -match "/uninstall" -and $registered.ModifyPath -match "/modify"
}
Check "the installer copied itself into the program folder" { Test-Path "$installFolder\ScanRelaySetup.exe" }

if ($Module) {
    Write-Host "`nModules"
    $manifest = Get-Content "$installFolder\Modules\modules.json" -Raw | ConvertFrom-Json
    Check "the custom module is installed and marked custom" { @($manifest).Count -eq 1 -and $manifest[0].Status -eq "custom" }
    $log = Get-Content (Get-ChildItem "$dataFolder\Logs" -Filter "scanrelay-*.log" | Select-Object -First 1).FullName -Raw
    Check "the service loaded it" { $log -match "Loaded module $($manifest[0].Id)" }
}

# ----------------------------------------------------------------- repair
Write-Host "`nRun the installer again (repair)"
$graphBefore = $install.GraphCertThumbprint
$exit = Run-Setup @("/quiet", "/answers", "`"$answersFile`"")
Check "a second run finishes without error (exit code $exit)" { $exit -eq 0 }
$installAfter = Get-Content "$installFolder\install.json" -Raw | ConvertFrom-Json
Check "it kept the same Graph certificate, so the Entra connection survives" { $installAfter.GraphCertThumbprint -eq $graphBefore }
Check "it kept the printer account's password, so printers keep working" { $installAfter.PrinterPasswordProtected -and (
    [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($installAfter.PrinterPasswordProtected), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)) -eq $password) }
Check "the service is running again and healthy" { (Invoke-RestMethod "$url/health" -SkipCertificateCheck).status -eq "ok" }
Check "the scan from before the repair is still in Store" { (Get-ChildItem "$dataFolder\Store" -Filter *.pdf).Count -ge 1 }

# -------------------------------------------------------------- uninstall
Write-Host "`nUninstall"
# Run from the copy outside the program folder, so the removal is finished when it returns.
$exit = Run-Setup @("/quiet", "/uninstall")
Check "the uninstaller finished without error (exit code $exit)" { $exit -eq 0 }
Check "the service is gone" { -not (Get-Service $service -ErrorAction SilentlyContinue) }
Check "the share is gone" { -not (Get-SmbShare -Name $share -ErrorAction SilentlyContinue) }
Check "the printer account is gone" { -not (Get-LocalUser -Name $account -ErrorAction SilentlyContinue) }
Check "the firewall rule is gone" { -not (Get-NetFirewallRule -DisplayName "ScanRelay admin screen" -ErrorAction SilentlyContinue) }
Check "ScanRelay is no longer under Installed apps" { -not (Test-Path $uninstallKey) }
Check "both certificates are gone" { -not (Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -in @($install.GraphCertThumbprint, $install.WebCertThumbprint) }) }
Check "the data folder is gone" { -not (Test-Path $dataFolder) }
Check "the program folder is gone" { -not (Test-Path $installFolder) }

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) of $checks checks failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "All $checks checks passed."
