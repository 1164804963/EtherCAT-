param([Parameter(Mandatory=$true)][string]$Path)

$ErrorActionPreference = 'Stop'
$invariant = [Globalization.CultureInfo]::InvariantCulture
$intervals = [Collections.Generic.List[double]]::new()
$exchanges = [Collections.Generic.List[double]]::new()
$phases = [ordered]@{
    timer_return_offset_us = [Collections.Generic.List[double]]::new()
    dispatch_late_us = [Collections.Generic.List[double]]::new()
    al_before_exchange_us = [Collections.Generic.List[double]]::new()
    send_us = [Collections.Generic.List[double]]::new()
    receive_us = [Collections.Generic.List[double]]::new()
}
$count = 0
$bad = 0
$skipped = 0L
$schedulerLeads = [Collections.Generic.SortedSet[int]]::new()
$previous = $null
$elapsed = 0.0
$captureWall = $null
$processCpu = $null
$alCodes = [Collections.Generic.SortedSet[string]]::new()
$firstAlError = $null

Import-Csv -LiteralPath $Path | ForEach-Object {
    $count++
    if ($_.interval_us) { $intervals.Add([double]::Parse($_.interval_us, $invariant)) }
    $exchanges.Add([double]::Parse($_.exchange_us, $invariant))
    if ($count -eq 1) {
        if ($_.capture_wall_seconds) { $captureWall = [double]::Parse($_.capture_wall_seconds, $invariant) }
        if ($_.process_cpu_percent) { $processCpu = [double]::Parse($_.process_cpu_percent, $invariant) }
    }
    $lead = $_.PSObject.Properties['scheduler_spin_lead_us']
    if ($lead -and ![string]::IsNullOrWhiteSpace($lead.Value)) {
        [void]$schedulerLeads.Add([int]$lead.Value)
    }
    foreach ($key in $phases.Keys) {
        $field = $_.PSObject.Properties[$key]
        if (!$field -or [string]::IsNullOrWhiteSpace($field.Value)) { continue }
        $value = [double]::Parse($field.Value, $invariant)
        if ($key -ne 'al_before_exchange_us' -or $value -gt 0) { $phases[$key].Add($value) }
    }
    if ([int]$_.wkc -ne [int]$_.expected_wkc) { $bad++ }
    $total = [long]$_.skipped_slots
    if ($null -ne $previous -and $total -ge $previous) { $skipped += $total - $previous }
    $previous = $total
    $elapsed = [double]::Parse($_.elapsed_us, $invariant)
    $code = $_.PSObject.Properties['al_status_code']
    if ($code -and $code.Value -match '^0x[0-9a-f]{4}$' -and $code.Value -ne '0x0000') {
        [void]$alCodes.Add($code.Value.ToUpperInvariant().Replace('0X','0x'))
        if ($null -eq $firstAlError) { $firstAlError = [Math]::Round($elapsed / 1000000, 6) }
    }
}

if ($count -lt 2 -or $intervals.Count -eq 0) { throw 'The PDO timing trace needs at least two samples with a measured interval.' }
$intervals.Sort()
$exchanges.Sort()

function Get-Percentile($Values, [double]$Fraction) {
    $rank = [int][Math]::Ceiling($Values.Count * $Fraction) - 1
    [Math]::Round($Values[$rank], 3)
}

$summary = [ordered]@{
    Samples = $count
    DurationSeconds = [Math]::Round($elapsed / 1000000, 3)
    PdoRateHz = if ($elapsed -gt 0) { [Math]::Round(($count - 1) * 1000000.0 / $elapsed, 2) } else { $null }
    IntervalP50Us = Get-Percentile $intervals 0.5
    IntervalP99Us = Get-Percentile $intervals 0.99
    IntervalP999Us = Get-Percentile $intervals 0.999
    IntervalMaxUs = Get-Percentile $intervals 1
    ExchangeP99Us = Get-Percentile $exchanges 0.99
    ExchangeMaxUs = Get-Percentile $exchanges 1
    WkcMismatches = $bad
    SkippedSlots = $skipped
    SkippedSlotPercent = [Math]::Round(100.0 * $skipped / ($count - 1 + $skipped), 2)
}

if ($schedulerLeads.Count -gt 0) { $summary['SchedulerSpinLeadUs'] = @($schedulerLeads) -join ',' }
if ($null -ne $captureWall) { $summary['CaptureWallSeconds'] = $captureWall }
if ($null -ne $processCpu) { $summary['ProcessCpuPercent'] = $processCpu }
if ($alCodes.Count -gt 0) {
    $summary['NonZeroAlStatusCodes'] = @($alCodes) -join ','
    $summary['FirstNonZeroAlStatusSeconds'] = $firstAlError
}

foreach ($key in $phases.Keys) {
    $values = $phases[$key]
    if ($values.Count -eq 0) { continue }
    $values.Sort()
    $summary["$($key)_samples"] = $values.Count
    $summary["$($key)_p50"] = Get-Percentile $values 0.5
    $summary["$($key)_p99"] = Get-Percentile $values 0.99
    $summary["$($key)_max"] = Get-Percentile $values 1
}
[pscustomobject]$summary
