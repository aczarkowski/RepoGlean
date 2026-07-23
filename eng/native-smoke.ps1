[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [switch] $RequireLinuxBaseline
)

$ErrorActionPreference = "Stop"
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("devcleaner-native-smoke-" + [Guid]::NewGuid().ToString("N"))
$repository = Join-Path $smokeRoot "repository"
$previousGlobalConfig = $env:GIT_CONFIG_GLOBAL
$previousNoSystemConfig = $env:GIT_CONFIG_NOSYSTEM

function Invoke-JsonCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $stderrPath = Join-Path $smokeRoot ("stderr-" + [Guid]::NewGuid().ToString("N") + ".txt")
    try {
        $stdout = @(& $executable @Arguments 2> $stderrPath)
        $exitCode = $LASTEXITCODE
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { "" }
        if ($exitCode -ne 0) {
            throw "DevCleaner exited $exitCode. stderr: $stderr stdout: $($stdout -join [Environment]::NewLine)"
        }

        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            throw "DevCleaner wrote unexpected stderr: $stderr"
        }

        return (($stdout -join [Environment]::NewLine) | ConvertFrom-Json -Depth 32)
    }
    finally {
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

try {
    New-Item -ItemType Directory -Path $repository -Force | Out-Null

    if ($RequireLinuxBaseline) {
        if (-not $IsLinux) {
            throw "-RequireLinuxBaseline is valid only on Linux."
        }

        $osRelease = Get-Content -LiteralPath "/etc/os-release" -Raw
        $isUbuntu = $osRelease -match '(?m)^ID="?ubuntu"?$'
        $isUbuntu2404 = $osRelease -match '(?m)^VERSION_ID="?24\.04"?$'
        if (-not $isUbuntu -or -not $isUbuntu2404) {
            throw "Linux release smoke requires Ubuntu 24.04."
        }

        $kernelRelease = (& uname -r).Trim()
        if ($LASTEXITCODE -ne 0 -or [int](($kernelRelease -split '\.')[0]) -lt 6) {
            throw "Linux release smoke requires a Linux 6.x-or-newer kernel; found '$kernelRelease'."
        }

        $fileSystem = (& findmnt --noheadings --output FSTYPE --target $smokeRoot).Trim()
        if ($LASTEXITCODE -ne 0 -or $fileSystem -ne "ext4") {
            throw "Linux release smoke requires an ext4 workspace; found '$fileSystem'."
        }
    }

    $emptyGitConfig = Join-Path $smokeRoot "gitconfig"
    Set-Content -LiteralPath $emptyGitConfig -Value "" -NoNewline
    $env:GIT_CONFIG_GLOBAL = $emptyGitConfig
    $env:GIT_CONFIG_NOSYSTEM = "1"

    & git -C $repository init --quiet
    if ($LASTEXITCODE -ne 0) { throw "git init failed." }
    & git -C $repository config user.email "devcleaner-smoke@example.invalid"
    & git -C $repository config user.name "DevCleaner Smoke"

    New-Item -ItemType Directory -Path (Join-Path $repository "tracked") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $repository ".gitignore") -Value "obj/`nnode_modules/"
    Set-Content -LiteralPath (Join-Path $repository "project.csproj") -Value "<Project />"
    Set-Content -LiteralPath (Join-Path $repository "package.json") -Value "{}"
    Set-Content -LiteralPath (Join-Path $repository "tracked/keep.txt") -Value "tracked content"
    & git -C $repository add -- .gitignore project.csproj package.json tracked/keep.txt
    & git -C $repository commit --quiet -m "smoke fixture"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed." }

    New-Item -ItemType Directory -Path (Join-Path $repository "obj") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $repository "node_modules") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $repository "unrelated") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $repository "obj/artifact.bin") -Value "build output"
    Set-Content -LiteralPath (Join-Path $repository "node_modules/package.bin") -Value "dependency output"
    Set-Content -LiteralPath (Join-Path $repository "unrelated/keep.txt") -Value "untracked content"
    $configPath = Join-Path $smokeRoot "config.json"
    Set-Content -LiteralPath $configPath -Value '{"schemaVersion":1}' -NoNewline

    $scan = Invoke-JsonCommand -Arguments @("scan", $repository, "--config", $configPath, "--format", "json", "--no-progress")
    if ($scan.schemaVersion -ne 1 -or $scan.operation -ne "scan" -or $scan.status -ne "success") {
        throw "Native scan returned an unexpected JSON envelope."
    }

    $candidatePaths = @($scan.repositories | ForEach-Object { $_.candidates } | ForEach-Object { $_.relativePath })
    if ($candidatePaths -notcontains "obj" -or $candidatePaths -notcontains "node_modules") {
        throw "Native scan did not report both build and dependency fixtures."
    }

    $clean = Invoke-JsonCommand -Arguments @("clean", $repository, "--yes", "--category", "build", "--config", $configPath, "--format", "json", "--no-progress")
    if ($clean.operation -ne "clean" -or $clean.status -ne "success" -or $clean.cleanup.deletedCount -ne 1) {
        throw "Native scoped cleanup returned an unexpected JSON result."
    }

    if (Test-Path -LiteralPath (Join-Path $repository "obj")) { throw "Scoped cleanup left the selected build artifact behind." }
    if (-not (Test-Path -LiteralPath (Join-Path $repository "node_modules/package.bin"))) { throw "Scoped cleanup removed an opt-in dependency artifact." }
    if (-not (Test-Path -LiteralPath (Join-Path $repository "tracked/keep.txt"))) { throw "Scoped cleanup removed tracked content." }
    if (-not (Test-Path -LiteralPath (Join-Path $repository "unrelated/keep.txt"))) { throw "Scoped cleanup removed unrelated untracked content." }
    if (-not (Test-Path -LiteralPath (Join-Path $repository ".git"))) { throw "Scoped cleanup removed Git metadata." }

    Write-Output "Native packaged-executable smoke PASS: $executable"
}
finally {
    $env:GIT_CONFIG_GLOBAL = $previousGlobalConfig
    $env:GIT_CONFIG_NOSYSTEM = $previousNoSystemConfig
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
}
