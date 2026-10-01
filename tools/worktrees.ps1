<#
.SYNOPSIS
  Every worktree of this repo: uncommitted files, commits not on main, age.

.DESCRIPTION
  worktrees.ps1            one line per worktree, the ones holding work first
  worktrees.ps1 -Detail    also list the uncommitted files and unmerged commits

  Branch-versus-main comparisons cannot see uncommitted work, and features
  have sat uncommitted in stale worktrees for days. Run this before any
  "merge everything", cleanup or worktree removal, and read the diff of
  anything it flags before deleting it.
#>
param([switch]$Detail)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path $PSScriptRoot -Parent
& git -C $Repo fetch -q origin main 2>$null

$trees = @()
$cur = $null
foreach ($l in (& git -C $Repo worktree list --porcelain)) {
    if ($l -like 'worktree *') { if ($cur) { $trees += $cur }; $cur = [ordered]@{ Path = $l.Substring(9); Branch = '(detached)' } }
    elseif ($l -like 'branch *') { $cur.Branch = $l.Substring(7) -replace '^refs/heads/', '' }
}
if ($cur) { $trees += $cur }

$rows = foreach ($t in $trees) {
    if (-not (Test-Path $t.Path)) { [pscustomobject]@{ Name = Split-Path $t.Path -Leaf; Branch = $t.Branch; Dirty = '?'; Ahead = '?'; Last = 'MISSING (git worktree prune)'; Path = $t.Path; Files = @(); Commits = @() }; continue }
    $status = @(& git -C $t.Path status --porcelain 2>$null)
    $commits = @(& git -C $t.Path log --oneline origin/main..HEAD 2>$null)
    $last = & git -C $t.Path log -1 --format='%cd' --date=short 2>$null
    [pscustomobject]@{
        Name = Split-Path $t.Path -Leaf
        Branch = $t.Branch
        Dirty = $status.Count
        Ahead = $commits.Count
        Last = $last
        Path = $t.Path
        Files = $status
        Commits = $commits
    }
}

$rows = $rows | Sort-Object @{ Expression = { [int]($_.Dirty -ne 0 -or $_.Ahead -ne 0) }; Descending = $true }, Last -Descending
'{0,-45} {1,6} {2,6}  {3,-10}  {4}' -f 'worktree', 'dirty', 'ahead', 'last', 'branch'
foreach ($r in $rows) {
    '{0,-45} {1,6} {2,6}  {3,-10}  {4}' -f $r.Name, $r.Dirty, $r.Ahead, $r.Last, $r.Branch
    if ($Detail) {
        foreach ($f in $r.Files | Select-Object -First 15) { "      $f" }
        if ($r.Files.Count -gt 15) { "      … $($r.Files.Count - 15) more" }
        foreach ($c in $r.Commits | Select-Object -First 10) { "      + $c" }
    }
}
"`ndirty = uncommitted or untracked files; ahead = commits not on origin/main."
