<#
.SYNOPSIS
  Builds every printer module in the repo and packs each into a .srmod file.
.DESCRIPTION
  Used by the release pipeline, by "Publish modules", and by CI so that a mistake here is seen
  before a release needs it. The sample module drives a mock printer and is not shipped. A module
  with no module.yaml yet is still being built and is left out.
.PARAMETER Out
  The folder the .srmod files go into.
#>
param([Parameter(Mandatory)][string] $Out)

$ErrorActionPreference = "Stop"
$tool = "tools/ModuleTool"

dotnet build $tool --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Building the module tool failed." }

Get-ChildItem modules -Recurse -Filter *.csproj |
  Where-Object { $_.FullName -notmatch 'scanrelay[\\/]sample' -and (Test-Path (Join-Path $_.DirectoryName 'module.yaml')) } |
  ForEach-Object {
    $built = "artifacts/modules/$($_.BaseName)"
    dotnet publish $_.FullName --configuration Release --output $built
    if ($LASTEXITCODE -ne 0) { throw "Publishing $($_.Name) failed." }
    dotnet run --project $tool --configuration Release --no-build -- pack $built $Out
    if ($LASTEXITCODE -ne 0) { throw "Packing $($_.Name) failed." }
  }
