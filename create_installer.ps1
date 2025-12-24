
$releaseDir = ".\DevAi\bin\Release"
$outputExe = ".\DevAi_SingleFile.exe"
$sedFile = ".\installer.sed"

$files = Get-ChildItem $releaseDir | Where-Object { $_.Name -ne "session.json" -and $_.Extension -ne ".xml" -and $_.Extension -ne ".pdb" }

$sedContent = @"
[Version]
Class=IEXPRESS
SEDVersion=3.0
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=1
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$outputExe
FriendlyName=DevAi
AppLaunched=DevAi.exe
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles

[Strings]
"@

$fileIndex = 0
foreach ($file in $files) {
    $sedContent += "`r`nFILE$fileIndex=`"$($file.Name)`""
    $fileIndex++
}

$sedContent += "`r`n`r`n[SourceFiles]`r`nSourceFiles0=$releaseDir\`r`n`r`n[SourceFiles0]"

$fileIndex = 0
foreach ($file in $files) {
    $sedContent += "`r`n%FILE$fileIndex%="
    $fileIndex++
}

Set-Content -Path $sedFile -Value $sedContent -Encoding Ascii

Write-Host "Generated SED file at $sedFile"
Write-Host "Running IExpress..."

iexpress /N $sedFile

if (Test-Path $outputExe) {
    Write-Host "Success! Created $outputExe"
} else {
    Write-Host "Failed to create executable."
}
