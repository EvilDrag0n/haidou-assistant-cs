param([string]$Exe = "$PSScriptRoot\..\out\HaiDouAssistant.exe")
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Exe))
$type = $assembly.GetType('HaiDou.HexData', $true)
$hex = [Activator]::CreateInstance($type, $true)
$select = $type.GetMethod('Select')
$snapshot = $type.GetMethod('Snapshot')
$rows = @()
foreach ($case in @(@{ Id = 526; Name = '镕铁少女'; Dataset = 'high' }, @{ Id = 103; Name = '九尾妖狐'; Dataset = 'all' })) {
    $select.Invoke($hex, [object[]]@([int]$case.Id, $case.Name, $case.Dataset, $false)) | Out-Null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $state = $snapshot.Invoke($hex, $null)
    } while ($state['status'] -eq 'loading' -and $watch.Elapsed.TotalSeconds -lt 50)
    if ($state['status'] -ne 'ready') { throw "Hero $($case.Id): $($state['status']) $($state['details'])" }
    if ($state['championId'] -ne $case.Id -or $state['dataset'] -ne $case.Dataset) { throw 'Wrong hero or dataset' }
    if ($state['augments'].Count -eq 0 -or $state['combinations'].Count -eq 0) { throw 'Missing real augment statistics' }
    $rows += [ordered]@{
        championId = $state['championId']; dataset = $state['dataset']; version = $state['version']; dataDate = $state['dataDate']
        fetchedAt = $state['fetchedAt']; augments = $state['augments'].Count; combinations = $state['combinations'].Count
        championSamples = $state['championSamples']; endpoint = $state['endpoint']; firstAugment = $state['augments'][0]; firstCombination = $state['combinations'][0]
    }
}
$rows | ConvertTo-Json -Depth 8
