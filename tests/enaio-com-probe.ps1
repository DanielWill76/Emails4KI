<#
.SYNOPSIS
  Live-Test der enaio-COM-API: probiert die Übergabevarianten der ref-/out-Parameter durch.
.DESCRIPTION
  Benötigt einen laufenden enaio Client mit markierten Objekten (Trefferliste).
  Zeigt je Variante OK/FEHLER, damit klar ist, welche Übergabeart per Late Binding funktioniert.
  Aufruf: powershell -File tests\enaio-com-probe.ps1
#>
$ErrorActionPreference = 'Stop'
$app = New-Object -ComObject optimal_AS.Application
$global:fail = 0

function Probe($name, [scriptblock]$call, [bool]$expectOk) {
    try   { $res = & $call; $ok = $true; $info = $res }
    catch { $ok = $false; $info = $_.Exception.Message.Split("`n")[0] }
    $state = if ($ok -eq $expectOk) { 'OK    ' } else { $global:fail++; 'FEHLER' }
    Write-Host ("[{0}] {1} -> {2}{3}" -f $state, $name, $(if ($ok) { 'funktioniert: ' } else { 'Ausnahme: ' }), $info)
}

Write-Host "== GetCurrentSelection(ref result): Übergabevarianten =="
Probe 'ref $null (PowerShell: [ref]$null)' { $r = [ref]$null; $n = $app.GetCurrentSelection($r); "$n Treffer" } $true
Probe 'ref "" (leerer String)'              { $r = [ref]""; $n = $app.GetCurrentSelection($r); "$n Treffer" } $false
Probe 'ref 0 (Integer)'                     { $r = [ref]0;  $n = $app.GetCurrentSelection($r); "$n Treffer" } $false

Write-Host "== Auswahl auslesen =="
$sel = [ref]$null
$count = [int]$app.GetCurrentSelection($sel)
Write-Host "Markierte Objekte: $count"
if ($count -le 0) { Write-Host "Keine Markierung in enaio - weitere Tests übersprungen."; exit 2 }
$first = ("$($sel.Value)" -split '[;\r\n]+' | Where-Object { $_ })[0]
$id, $type = $first.Split(',')
Write-Host "Erstes Objekt: ID=$id Typ=$type"

Write-Host "== GetFilesFromIDEx(id, type, sep, flag, ref names) =="
Probe 'ref $null' { $r = [ref]$null; $rc = $app.GetFilesFromIDEx([int]$id, [int]$type, '|', $true, $r); "rc=$rc Datei=$($r.Value)" } $true
Probe 'ref ""'    { $r = [ref]"";    $rc = $app.GetFilesFromIDEx([int]$id, [int]$type, '|', $true, $r); "rc=$rc" } $false

Write-Host "== Zusatzprogramm-Parameter %i (Datei, eine Zeile je Eintrag ID,Typ) =="
$tmp = Join-Path $env:TEMP 'enaio_probe_sel.txt'
($sel.Value -split '[;\r\n]+' | Where-Object { $_ }) | Set-Content $tmp -Encoding ASCII
Write-Host "Parameter-Datei: $tmp ($((Get-Content $tmp).Count) Zeilen)"

if ($global:fail -gt 0) { Write-Host "`n$($global:fail) Variante(n) verhielten sich anders als erwartet."; exit 1 }
Write-Host "`nAlle Varianten wie erwartet."; exit 0
