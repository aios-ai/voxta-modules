[CmdletBinding()]
param(
    [switch] $Publish,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solution = Join-Path $repositoryRoot 'Voxta.Modules.slnx'

function Invoke-Git {
    param([Parameter(Mandatory)][string[]] $Arguments)

    $output = & git -C $repositoryRoot @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return @($output)
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string] $Command,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    $remoteTags = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    if ($Publish) {
        $status = @(Invoke-Git -Arguments @('status', '--porcelain', '--untracked-files=all'))
        if ($status.Count -ne 0) {
            throw 'The working tree must be clean before publishing releases.'
        }

        $branch = (Invoke-Git -Arguments @('branch', '--show-current')) -join ''
        if ($branch -ne 'main') {
            throw "Releases must be published from main, not '$branch'."
        }

        Invoke-Git -Arguments @('fetch', 'origin', 'main', '--tags') | Out-Null
        $head = (Invoke-Git -Arguments @('rev-parse', 'HEAD')) -join ''
        $originMain = (Invoke-Git -Arguments @('rev-parse', 'origin/main')) -join ''
        if ($head -ne $originMain) {
            throw 'HEAD must exactly match origin/main. Commit and push main before publishing releases.'
        }

        foreach ($line in @(Invoke-Git -Arguments @('ls-remote', '--tags', 'origin'))) {
            if ($line -match '\srefs/tags/(.+?)(?:\^\{\})?$') {
                [void] $remoteTags.Add($Matches[1])
            }
        }
    }

    $manifestPaths = @(Invoke-Git -Arguments @('ls-files', '--', '*/module.json'))
    if ($manifestPaths.Count -eq 0) {
        throw 'No tracked module.json files were found.'
    }

    $pendingTags = [System.Collections.Generic.List[string]]::new()
    $tagsToCreate = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $manifestPaths) {
        $manifestPath = Join-Path $repositoryRoot $relativePath
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $moduleId = [string] $manifest.id
        $version = [string] $manifest.version

        if ([string]::IsNullOrWhiteSpace($moduleId)) {
            throw "$relativePath has no module id."
        }
        if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
            throw "$relativePath has invalid version '$version'."
        }

        $tag = "$moduleId/v$version"
        $existingTag = @(Invoke-Git -Arguments @('tag', '--list', $tag))
        if ($Publish -and $remoteTags.Contains($tag)) {
            Write-Host "Already released: $tag"
        }
        elseif ($existingTag.Count -eq 0) {
            $tagsToCreate.Add($tag)
            $pendingTags.Add($tag)
        }
        elseif ($Publish) {
            $tagCommit = (Invoke-Git -Arguments @('rev-list', '-n', '1', $tag)) -join ''
            if ($tagCommit -ne $head) {
                throw "Local-only tag $tag does not point to HEAD. Resolve it manually before publishing."
            }
            $pendingTags.Add($tag)
        }
        else {
            Write-Host "Already released: $tag"
        }
    }

    if ($pendingTags.Count -eq 0) {
        Write-Host 'No unpublished module versions were found.'
        return
    }

    Write-Host 'Pending releases:'
    $pendingTags | ForEach-Object { Write-Host "  $_" }

    if (-not $Publish) {
        Write-Host 'Plan only. Re-run with -Publish after the release commit is on origin/main.'
        return
    }

    if (-not $SkipBuild) {
        Invoke-CheckedCommand -Command 'dotnet' -Arguments @('restore', $solution, '--locked-mode')
        Invoke-CheckedCommand -Command 'dotnet' -Arguments @(
            'build',
            $solution,
            '-c', 'Release',
            '-p:ContinuousIntegrationBuild=true',
            '--no-restore'
        )
    }

    foreach ($tag in $tagsToCreate) {
        Invoke-Git -Arguments @('tag', '--annotate', $tag, '--message', "Release $tag") | Out-Null
    }

    Invoke-Git -Arguments (@('push', '--atomic', 'origin') + $pendingTags) | Out-Null
    Write-Host "Published $($pendingTags.Count) release tag(s)."
}
finally {
    Pop-Location
}
