# Shared retry/status helpers for the msstore CLI steps in Publish-msix.yml.
# Dot-source this file at the top of a step's run: block: . ./.github/scripts/MsstoreHelpers.ps1

# Global (not script) scope so this stays visible to callers regardless of whether this file
# is dot-sourced from a top-level step script or a nested one — dot-sourcing itself doesn't
# introduce a new scope, but $global: is robust even if that ever changes.
$global:BlockingSubmissionStatuses = @(
    'PendingCommit', 'CommitStarted', 'PendingPublication',
    'Publishing', 'PreProcessing', 'Certification', 'Release'
)

function Invoke-WithRetry {
    param(
        [Parameter(Mandatory)] [scriptblock] $Action,
        [Parameter(Mandatory)] [string] $Description,
        [int] $MaxAttempts = 10,
        [int] $DelaySeconds = 15
    )

    for ($i = 1; $i -le $MaxAttempts; $i++) {
        & $Action
        if ($LASTEXITCODE -eq 0) { return }

        Write-Warning "Attempt $i/$MaxAttempts of '$Description' failed (exit $LASTEXITCODE)."
        if ($i -lt $MaxAttempts) {
            Write-Warning "Retrying in $DelaySeconds s..."
            Start-Sleep -Seconds $DelaySeconds
        }
    }

    Write-Error "'$Description' failed after $MaxAttempts attempts."
    exit 1
}

# Returns the current submission status string for $AppId, or $null if none is reported.
# Retries transient CLI failures but exits the process (hard failure) if the status
# itself can't be determined after $MaxAttempts — callers rely on knowing the real state.
function Get-MsstoreSubmissionStatus {
    param(
        [Parameter(Mandatory)] [string] $AppId,
        [int] $MaxAttempts = 10,
        [int] $DelaySeconds = 15
    )

    $output = $null
    for ($i = 1; $i -le $MaxAttempts; $i++) {
        $output = msstore submission status $AppId 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0) { break }
        Write-Warning "Attempt $i/$MaxAttempts of 'msstore submission status' failed (exit $LASTEXITCODE)."
        if ($i -lt $MaxAttempts) { Start-Sleep -Seconds $DelaySeconds }
    }

    # Write-Host (not Write-Output) — this must not add to the function's return value,
    # which callers capture as the parsed status string.
    Write-Host $output
    if ($LASTEXITCODE -ne 0) {
        Write-Error "msstore submission status failed after $MaxAttempts attempts."
        exit 1
    }

    $clean = $output -replace '\x1b\[[\d;]*[mGKHF]', ''
    if ($clean -match 'Submission Status\s*=\s*(\S+)') {
        return $Matches[1].Trim()
    }
    return $null
}
