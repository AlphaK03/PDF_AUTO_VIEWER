<#
.SYNOPSIS
  End-to-end test of the document selection rules (language / type / copies).

.DESCRIPTION
  For every round:
    1. Generates small valid PDFs in e2e\generated\<round> (OUTSIDE Downloads).
       Only the names matter, but files under 100 bytes are ignored by the app
       on purpose (browser placeholders), so each one is a tiny real PDF that
       shows its own name.
    2. Starts the app with the round's preferred language.
    3. Copies the files into Downloads (all at once, or staggered).
    4. Waits until the app settles, then compares the viewer windows left open
       against the expected winners computed by an independent oracle below.
    5. Closes the remaining viewers (like a user would) and checks that every
       test file was deleted from Downloads.
  Also records the RAM used by the app + its WebView2 processes and its CPU
  time while idle.

  The user's settings.json and startup registry entry are restored at the end.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File e2e\Run-SelectionTests.ps1
#>
param(
    [string]$AppExe   = "$PSScriptRoot\app\PdfAutoViewer.exe",
    [string[]]$Rounds = @('B1-SPA', 'B2-ENG', 'B3-ANY', 'S1-SPA', 'S2-ENG', 'L1-SPA'),
    [int]$RandomFamilies = 6,
    [int]$Seed = 20261005
)

$ErrorActionPreference = 'Stop'

# ── Win32: list / close the viewer windows ────────────────────────────────
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class E2EWin {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    public static List<KeyValuePair<IntPtr, string>> Windows(uint pid) {
        var list = new List<KeyValuePair<IntPtr, string>>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(512);
                GetWindowText(h, sb, sb.Capacity);
                if (sb.Length > 0) list.Add(new KeyValuePair<IntPtr, string>(h, sb.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

$Prefix    = 'ZZE2E_'
$Downloads = (New-Object -ComObject Shell.Application).Namespace('shell:Downloads').Self.Path
$Settings  = Join-Path $env:LOCALAPPDATA 'PdfAutoViewer\settings.json'
$RunKey    = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$GenRoot   = Join-Path $PSScriptRoot 'generated'
$ResRoot   = Join-Path $PSScriptRoot 'results'

# ── Test data ─────────────────────────────────────────────────────────────

# Writes a tiny valid one-page PDF that displays $Text.
function New-MiniPdf([string]$Path, [string]$Text) {
    $t = $Text -replace '([\\()])', '\$1'
    $content = "BT /F1 18 Tf 30 300 Td ($t) Tj ET"
    $objs = @(
        '<< /Type /Catalog /Pages 2 0 R >>',
        '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
        "<< /Length $($content.Length) >>`nstream`n$content`nendstream",
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'
    )
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append("%PDF-1.4`n")
    $offsets = @()
    for ($i = 0; $i -lt $objs.Count; $i++) {
        $offsets += $sb.Length
        [void]$sb.Append("$($i + 1) 0 obj`n$($objs[$i])`nendobj`n")
    }
    $xref = $sb.Length
    [void]$sb.Append("xref`n0 $($objs.Count + 1)`n0000000000 65535 f `n")
    foreach ($o in $offsets) { [void]$sb.Append(('{0:D10} 00000 n `n' -f $o)) }
    [void]$sb.Append("trailer`n<< /Size $($objs.Count + 1) /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
    [IO.File]::WriteAllBytes($Path, [Text.Encoding]::ASCII.GetBytes($sb.ToString()))
}

# Variant codes: S/E/N = SPA / ENG / no language tag; "d" = _docx.pdf;
# "+n" = browser duplicate copy " (n)". Names mimic the real ones:
#   native SPA : <id>_SPA_MPI Test Doc 01.pdf
#   native ENG : <id>_ENG MPI Test Doc 01.pdf
#   docx       : <id>_SPA_MPI_Test_Doc_01_docx.pdf
function Get-VariantFileName([string]$Round, [int]$Family, [string]$Code) {
    $id   = '{0}{1}F{2:D2}_A' -f $Prefix, ($Round -replace '-', ''), $Family
    $copy = ''
    if ($Code -match '\+(\d+)$') { $copy = " ($($Matches[1]))"; $Code = $Code -replace '\+\d+$', '' }
    $docx = $Code.EndsWith('d')
    $lang = switch ($Code.Substring(0, 1)) { 'S' { 'SPA' } 'E' { 'ENG' } default { '' } }
    $doc  = '{0:D2}' -f $Family
    if ($docx) {
        $tag = if ($lang) { "_$lang" } else { '' }
        $stem = "$id${tag}_MPI_Test_Doc_${doc}_docx"
    }
    elseif ($lang -eq 'SPA') { $stem = "${id}_SPA_MPI Test Doc $doc" }
    elseif ($lang -eq 'ENG') { $stem = "${id}_ENG MPI Test Doc $doc" }
    else                     { $stem = "${id}_MPI Test Doc $doc" }
    return "$stem$copy.pdf"
}

# Fixed families cover every rule explicitly; random ones add variety.
$FixedFamilies = @(
    @('S', 'Sd', 'E', 'Ed'),        # all four combinations
    @('S', 'E'),                    # language only
    @('Sd', 'E'),                   # SPA docx vs ENG native  (cross case)
    @('S', 'Ed'),                   # SPA native vs ENG docx  (cross case)
    @('E', 'Ed'),                   # type only
    @('S', 'S+1', 'S+2'),           # duplicate copies
    @('Sd', 'Sd+1', 'E', 'E+1'),    # copies in both languages
    @('E'),                         # single file
    @('N', 'Nd'),                   # no language tag
    @('N', 'S', 'E'),               # tagged + untagged
    @('Ed', 'E+1', 'S'),            # mixed
    @('Nd', 'E'),                   # untagged docx vs ENG
    @('Sd'),                        # single docx
    @('S', 'Sd', 'Sd+1', 'E', 'Ed', 'Ed+1', 'N')  # everything at once
)

function Get-Families([string]$Round, [System.Random]$Rng) {
    $all = @($FixedFamilies)
    $pool = @('S', 'Sd', 'E', 'Ed', 'N', 'Nd')
    for ($i = 0; $i -lt $RandomFamilies; $i++) {
        $pick = @($pool | Where-Object { $Rng.Next(2) -eq 1 })
        if ($pick.Count -eq 0) { $pick = @($pool[$Rng.Next($pool.Count)]) }
        if ($Rng.Next(3) -eq 0) { $pick += ($pick[0] + '+1') }
        $all += , $pick
    }
    return , $all
}

# ── Oracle (independent re-implementation of the expected rules) ──────────
#   One document per family. Ranking, highest first:
#     1. preferred language > untagged > other language (no preference: one per language)
#     2. _docx.pdf > native .pdf
#     3. any copy of the winning variant is acceptable (arrival order decides)

function Get-DocInfo([string]$Name) {
    $stem = [IO.Path]::GetFileNameWithoutExtension($Name) -replace '\s*\(\d+\)\s*$', ''
    $docx = $stem -match '[_\s]docx$'
    $stem = $stem -replace '[_\s]docx$', ''
    $lang = if ($stem -match '_SPA(?=[_\s]|$)') { 'SPA' } elseif ($stem -match '_ENG(?=[_\s]|$)') { 'ENG' } else { '' }
    $fam  = (($stem -replace '_(SPA|ENG)(?=[_\s]|$)', '') -replace '[_\s]+', ' ').Trim().ToUpperInvariant()
    [pscustomobject]@{ Name = $Name; Family = $fam; Lang = $lang; Docx = [bool]$docx }
}

function Get-Rank($Info, [string]$Pref) {
    $l = 0
    if ($Pref) { if ($Info.Lang -eq $Pref) { $l = 2 } elseif ($Info.Lang -eq '') { $l = 1 } }
    return $l * 2 + [int]$Info.Docx
}

function Get-Group($Info, [string]$Pref) {
    if ($Pref) { return $Info.Family } else { return "$($Info.Family)|$($Info.Lang)" }
}

# Returns @{ group -> acceptable winner names }
function Get-Expected([string[]]$Names, [string]$Pref) {
    $expected = @{}
    $Names | ForEach-Object { Get-DocInfo $_ } |
        Group-Object { Get-Group $_ $Pref } | ForEach-Object {
            $best = ($_.Group | ForEach-Object { Get-Rank $_ $Pref } | Measure-Object -Maximum).Maximum
            $expected[$_.Name] = @($_.Group | Where-Object { (Get-Rank $_ $Pref) -eq $best } | ForEach-Object Name)
        }
    return $expected
}

# ── App control and measurement ───────────────────────────────────────────

function Get-TestWindows($Proc) {
    @([E2EWin]::Windows([uint32]$Proc.Id) | Where-Object { $_.Value.StartsWith($Prefix) })
}

function Get-TestFilesInDownloads {
    @(Get-ChildItem -LiteralPath $Downloads -Filter "$Prefix*" -File -ErrorAction SilentlyContinue | ForEach-Object Name)
}

# Private memory (MB, not shared pages, so nothing is counted twice) of the
# app itself and of the WebView2 processes that use its profile folder.
function Get-AppMemory($Proc) {
    $app = Get-Process -Id $Proc.Id -ErrorAction SilentlyContinue
    $wv = 0
    Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
        Where-Object { $_.CommandLine -like '*PdfAutoViewer\WebView2*' } |
        ForEach-Object { $wv += [int64]$_.PrivatePageCount }
    [pscustomobject]@{
        AppMB     = $(if ($app) { [math]::Round($app.PrivateMemorySize64 / 1MB) } else { 0 })
        WebViewMB = [math]::Round($wv / 1MB)
    }
}
function Get-AppMemoryMB($Proc) { $m = Get-AppMemory $Proc; $m.AppMB + $m.WebViewMB }

function Start-App([string]$Pref) {
    $num = switch ($Pref) { 'SPA' { 1 } 'ENG' { 2 } default { 0 } }
    New-Item -ItemType Directory -Force (Split-Path $Settings) | Out-Null
    Set-Content -LiteralPath $Settings -Value "{ `"PreferredLanguage`": $num }" -Encoding UTF8
    $p = Start-Process -FilePath $AppExe -PassThru
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        if (@([E2EWin]::Windows([uint32]$p.Id)).Count -gt 0) { break }
    }
    Start-Sleep -Seconds 4   # let the WebView2 prewarm finish
    return $p
}

function Stop-App($Proc) {
    if ($Proc -and -not $Proc.HasExited) { Stop-Process -Id $Proc.Id -Force; $Proc.WaitForExit(10000) | Out-Null }
    Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
        Where-Object { $_.CommandLine -like '*PdfAutoViewer\WebView2*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

# Settled = every test file still in Downloads has a window, and nothing has
# changed for $QuietSeconds.
function Wait-Settled($Proc, [int]$QuietSeconds = 6, [int]$MaxSeconds = 180, $Seen) {
    $last = ''; $quiet = 0; $peak = 0
    $start = Get-Date
    while (((Get-Date) - $start).TotalSeconds -lt $MaxSeconds) {
        Start-Sleep -Milliseconds 500
        $wins  = @(Get-TestWindows $Proc | ForEach-Object Value | Sort-Object)
        if ($null -ne $Seen) { foreach ($w in $wins) { [void]$Seen.Add($w) } }
        $files = @(Get-TestFilesInDownloads | Sort-Object)
        $peak  = [math]::Max($peak, (Get-AppMemoryMB $Proc))
        $snap  = ($wins -join '|') + '#' + ($files -join '|')
        $consistent = (@(Compare-Object $wins $files -ErrorAction SilentlyContinue).Count -eq 0)
        if ($snap -eq $last -and $consistent -and $files.Count -gt 0) { $quiet++ } else { $quiet = 0 }
        $last = $snap
        if ($quiet -ge $QuietSeconds * 2) { break }
    }
    [pscustomobject]@{ Windows = $wins; Files = $files; PeakMB = $peak; Seconds = [int]((Get-Date) - $start).TotalSeconds }
}

# ── One round ─────────────────────────────────────────────────────────────
#   B = bulk:      every file of every family pasted at once.
#   S = staggered: first five families, one file per family every 4 s —
#                  worst version first in odd families (forces the replace
#                  path), best first in even ones (forces the discard path).
#   L = long run:  5 bulk cycles in the SAME app instance, recording memory
#                  after each one, to detect leaks over time.

# Generates, delivers and checks one batch. Returns the problems found.
function Invoke-Batch($Proc, [string]$Tag, [string]$Mode, [string]$Pref, [System.Random]$Rng, $Seen) {
    $dir = Join-Path $GenRoot $Tag
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null

    $families = Get-Families $Tag $Rng
    if ($Mode -eq 'S') { $families = $families[0..4] }
    if ($Mode -eq 'L') { $families = $families[0..9] }

    $plan = @()   # one entry per family: its file names
    for ($f = 0; $f -lt $families.Count; $f++) {
        $names = @($families[$f] | ForEach-Object { Get-VariantFileName $Tag ($f + 1) $_ })
        foreach ($n in $names) { New-MiniPdf (Join-Path $dir $n) $n }
        $plan += , $names
    }
    $allNames = @($plan | ForEach-Object { $_ })
    $expected = Get-Expected $allNames $Pref

    if ($Mode -ne 'S') {
        Copy-Item -Path (Join-Path $dir '*') -Destination $Downloads
    }
    else {
        $ordered = @()
        for ($f = 0; $f -lt $plan.Count; $f++) {
            $sorted = @($plan[$f] | Sort-Object { Get-Rank (Get-DocInfo $_) $Pref })
            if ($f % 2 -eq 1) { [array]::Reverse($sorted) }
            $ordered += , $sorted
        }
        $steps = ($ordered | ForEach-Object { $_.Count } | Measure-Object -Maximum).Maximum
        for ($s = 0; $s -lt $steps; $s++) {
            foreach ($fam in $ordered) {
                if ($s -lt $fam.Count) { Copy-Item -LiteralPath (Join-Path $dir $fam[$s]) -Destination $Downloads }
            }
            for ($t = 0; $t -lt 8; $t++) {   # 4 s, still recording what opens
                Start-Sleep -Milliseconds 500
                foreach ($w in (Get-TestWindows $Proc)) { [void]$Seen.Add($w.Value) }
            }
        }
    }

    $settled = Wait-Settled $Proc -Seen $Seen

    # Compare with the oracle.
    $problems = @()
    $openByGroup = @{}
    foreach ($w in $settled.Windows) {
        $g = Get-Group (Get-DocInfo $w) $Pref
        if (-not $openByGroup.ContainsKey($g)) { $openByGroup[$g] = @() }
        $openByGroup[$g] += $w
    }
    foreach ($g in $expected.Keys) {
        $open = @($openByGroup[$g])
        if ($open.Count -eq 0)      { $problems += "NOTHING OPEN for [$g] (expected one of: $($expected[$g] -join ' / '))" }
        elseif ($open.Count -gt 1)  { $problems += "$($open.Count) OPEN for [$g]: $($open -join ' + ') (expected only one of: $($expected[$g] -join ' / '))" }
        elseif ($expected[$g] -notcontains $open[0]) { $problems += "WRONG for [$g]: '$($open[0])' (expected one of: $($expected[$g] -join ' / '))" }
    }

    # Close what is left, like the user would, and check the cleanup.
    foreach ($w in (Get-TestWindows $Proc)) { [void][E2EWin]::PostMessage($w.Key, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and (Get-TestFilesInDownloads).Count -gt 0) { Start-Sleep -Seconds 1 }
    $left = @(Get-TestFilesInDownloads)
    if ($left.Count -gt 0) { $problems += "NOT DELETED after closing: $($left -join ', ')" }

    [pscustomobject]@{
        Families = $plan.Count; Files = $allNames.Count; Expected = $expected.Count
        Open = $settled.Windows.Count; Settle_s = $settled.Seconds; PeakMB = $settled.PeakMB
        Problems = $problems
    }
}

function Invoke-Round([string]$Round, [System.Random]$Rng) {
    $mode   = $Round.Substring(0, 1)
    $pref   = $Round.Split('-')[1]; if ($pref -eq 'ANY') { $pref = '' }
    $cycles = if ($mode -eq 'L') { 5 } else { 1 }

    $proc   = Start-App $pref
    $idle   = Get-AppMemory $proc
    $seen   = New-Object 'System.Collections.Generic.HashSet[string]'
    $batches = @(); $memoryAfter = @()
    try {
        for ($c = 1; $c -le $cycles; $c++) {
            $tag = if ($cycles -gt 1) { "$Round-C$c" } else { $Round }
            $batches += Invoke-Batch $proc $tag $mode $pref $Rng $seen
            Start-Sleep -Seconds 5
            $m = Get-AppMemory $proc
            $memoryAfter += "$($m.AppMB)+$($m.WebViewMB)"
        }

        # Idle cost: CPU time used over 10 s with nothing to do.
        $proc.Refresh(); $cpu0 = $proc.TotalProcessorTime.TotalMilliseconds
        Start-Sleep -Seconds 10
        $proc.Refresh(); $cpu1 = $proc.TotalProcessorTime.TotalMilliseconds

        $problems = @($batches | ForEach-Object { $_.Problems })
        [pscustomobject]@{
            Round         = $Round
            Preference    = $(if ($pref) { $pref } else { 'ANY' })
            Cycles        = $cycles
            Files         = ($batches | Measure-Object Files -Sum).Sum
            Expected      = ($batches | Measure-Object Expected -Sum).Sum
            Open          = ($batches | Measure-Object Open -Sum).Sum
            EverOpened    = $seen.Count
            Settle_s      = ($batches | Measure-Object Settle_s -Maximum).Maximum
            Problems      = $problems.Count
            IdleMB        = "$($idle.AppMB)+$($idle.WebViewMB)"
            PeakMB        = ($batches | Measure-Object PeakMB -Maximum).Maximum
            AfterEachMB   = $memoryAfter -join ' | '
            IdleCpu_ms    = [math]::Round($cpu1 - $cpu0)
            Details       = $problems
        }
    }
    finally {
        Stop-App $proc
        Get-ChildItem -LiteralPath $Downloads -Filter "$Prefix*" -File -ErrorAction SilentlyContinue | Remove-Item -Force
    }
}

# ── Main ──────────────────────────────────────────────────────────────────

if (-not (Test-Path $AppExe)) { throw "App not found: $AppExe (build it first)" }
if (Get-Process -Name PdfAutoViewer -ErrorAction SilentlyContinue) {
    throw 'PdfAutoViewer is already running. Close it (tray icon > Exit) before testing.'
}

$savedSettings = if (Test-Path $Settings) { Get-Content -LiteralPath $Settings -Raw } else { $null }
$savedRun      = (Get-ItemProperty -Path $RunKey -Name PdfAutoViewer -ErrorAction SilentlyContinue).PdfAutoViewer

$rng = New-Object System.Random $Seed
$results = @()
try {
    foreach ($r in $Rounds) {
        Write-Host "── Round $r ──" -ForegroundColor Cyan
        $res = Invoke-Round $r $rng
        $results += $res
        $color = if ($res.Problems -eq 0) { 'Green' } else { 'Red' }
        Write-Host ("  files={0} expected={1} open={2} everOpened={3} problems={4} settle={5}s | RAM MB (app+webview2) idle={6} peak={7} afterEachCycle=[{8}] | idleCPU={9} ms" -f `
            $res.Files, $res.Expected, $res.Open, $res.EverOpened, $res.Problems, $res.Settle_s, $res.IdleMB, $res.PeakMB, $res.AfterEachMB, $res.IdleCpu_ms) -ForegroundColor $color
        $res.Details | ForEach-Object { Write-Host "   - $_" -ForegroundColor Yellow }
    }
}
finally {
    if ($null -ne $savedSettings) { Set-Content -LiteralPath $Settings -Value $savedSettings -NoNewline -Encoding UTF8 }
    if ($savedRun) { Set-ItemProperty -Path $RunKey -Name PdfAutoViewer -Value $savedRun }
    Get-ChildItem -LiteralPath $Downloads -Filter "$Prefix*" -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

New-Item -ItemType Directory -Force $ResRoot | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$results | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $ResRoot "results-$stamp.json") -Encoding UTF8
Write-Host "Results: $(Join-Path $ResRoot "results-$stamp.json")"
$total = ($results | Measure-Object Problems -Sum).Sum
if ($total -gt 0) { exit 1 } else { exit 0 }
