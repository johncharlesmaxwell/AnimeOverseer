[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ServerHost,

    [Parameter(Mandatory)]
    [string]$ServerUser,

    [string]$RemoteDeployPath = 'C:\AnimeOverseer',

    [string]$ImageRepository = 'animeoverseer',

    [string]$ImageTag,

    [string]$GitTagPrefix = 'deploy-',

    [ValidateSet('true', 'false')]
    [string]$PushGitTag = 'true'
)

$ErrorActionPreference = 'Stop'
$shouldPushGitTag = [System.Convert]::ToBoolean($PushGitTag)

function Invoke-Checked {
    param([scriptblock]$Command, [string]$Description)

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function New-DeploymentGitTag {
    param([string]$TagName, [string]$ImageName)

    Invoke-Checked { git -C $repositoryRoot rev-parse --verify HEAD } 'Git repository verification'
    Invoke-Checked { git -C $repositoryRoot tag --annotate $TagName --message "Deployed $ImageName" } "Git tag creation ($TagName)"

    if ($shouldPushGitTag) {
        Invoke-Checked { git -C $repositoryRoot push origin "refs/tags/$TagName" } "Git tag push ($TagName)"
    }
}

$repositoryRoot = $PSScriptRoot
$dockerPath = (Get-Command docker -ErrorAction SilentlyContinue).Source
if (-not $dockerPath) {
    $dockerDesktopCli = Join-Path $env:ProgramFiles 'Docker\Docker\resources\bin\docker.exe'
    if (Test-Path -LiteralPath $dockerDesktopCli) {
        $dockerPath = $dockerDesktopCli
    }
}

if (-not $dockerPath) {
    throw 'Docker CLI was not found. Install Docker Desktop or add docker.exe to PATH.'
}

# Check the Docker engine before building, so a stopped Docker Desktop gives a clear message
# instead of failing partway through the image build. Windows PowerShell turns native stderr
# into errors under 'Stop', so relax the preference for this probe only.
$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$dockerOsType = (& $dockerPath info --format '{{.OSType}}' 2>$null)
$dockerInfoExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference

if ($dockerInfoExitCode -ne 0 -or -not $dockerOsType) {
    throw 'The Docker engine is not running. Start Docker Desktop, wait until it shows "Engine running", then publish again.'
}

if ("$dockerOsType".Trim() -ne 'linux') {
    throw "Docker is using $("$dockerOsType".Trim()) containers. Switch Docker Desktop to Linux containers (tray icon > Switch to Linux containers), then publish again."
}

$imageTag = if ($ImageTag) {
    $ImageTag
} else {
    $commit = (& git -C $repositoryRoot rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $commit = 'local' }
    "{0}-{1}" -f (Get-Date -Format 'yyyyMMddHHmmss'), $commit.Trim()
}

$image = "${ImageRepository}:${imageTag}"
$gitTag = "${GitTagPrefix}${imageTag}"
$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) "${ImageRepository}-${imageTag}.tar"
$destination = "${ServerUser}@${ServerHost}"
$remoteArchivePath = Join-Path $RemoteDeployPath 'animeoverseer.tar'
$composeFile = Join-Path $repositoryRoot 'docker-compose.yml'

try {
    Write-Host "Building $image for Linux containers..."
    Invoke-Checked { & $dockerPath buildx build --platform linux/amd64 --tag $image --load $repositoryRoot } 'Docker image build'

    Write-Host 'Saving image archive...'
    Invoke-Checked { & $dockerPath save --output $archivePath $image } 'Docker image export'

    # The OpenSSH server on Windows uses PowerShell to create the deployment directory.
    $createDirectoryCommand = "New-Item -ItemType Directory -Force -Path '$RemoteDeployPath'"
    Invoke-Checked { ssh $destination powershell -NoProfile -Command $createDirectoryCommand } 'Remote deployment-directory creation'

    Write-Host 'Copying image and Compose file to the server...'
    Invoke-Checked { scp $archivePath "${destination}:$remoteArchivePath" } 'Image archive copy'
    Invoke-Checked { scp $composeFile "${destination}:$RemoteDeployPath/docker-compose.yml" } 'Compose file copy'

    $remoteCommand = "`$ErrorActionPreference = 'Stop'; docker load --input '$remoteArchivePath'; Set-Location '$RemoteDeployPath'; `$env:APP_VERSION = '$imageTag'; docker compose up --detach --no-build --remove-orphans; Remove-Item -Force '$remoteArchivePath'"
    Write-Host 'Loading the image and restarting the service...'
    Invoke-Checked { ssh $destination powershell -NoProfile -Command $remoteCommand } 'Remote deployment'

    Write-Host "Creating Git tag $gitTag..."
    New-DeploymentGitTag -TagName $gitTag -ImageName $image

    Write-Host "Deployment complete: $image"
    Write-Host "Git tag created: $gitTag"
}
finally {
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
}
