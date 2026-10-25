<#
.SYNOPSIS
  Applies the textual part of the Phase 6 neuron migration recipe to C# files (06-03; reused by 06-04..06-07).

.DESCRIPTION
  Rewrites contracts and classes in place, idempotently: a second run over a migrated file changes nothing.

  Contracts (interfaces and signal records):
    - ": INeuron" and "IGrainWithStringKey" leave the base list (D-11).
    - [Alias("x")] on an interface becomes [NeuronAlias("x")]; [DefaultGrainType("x")] becomes [DefaultNeuronType("x")].
    - [AlwaysInterleave] -> [Interleaves], [ReadOnly] -> [ReadOnlyCall], [OneWay] -> [OneWayCall],
      [ResponseTimeout("hh:mm:ss")] -> [CallTimeout(milliseconds)], [MayInterleave(p)] -> [InterleavesWhen(p)].
    - A record with [GenerateSerializer, Alias("x")] deriving Signal becomes [Signal("x")] deriving DigitalBrain.Signal;
      its [property: Id(n)] markers go (signal payloads are JSON, D-14); other records in the file keep theirs.
  Classes:
    - [GrainType(x)] becomes [Neuron(x)] with the same argument (type strings never move).
    - [PersistentState("name", store)] constructor parameters go; a name other than "state" becomes State = "name" on [Neuron].
      The store stays pinned in NeuronGrainTypes.json.
    - The base becomes DigitalBrain.Neuron / DigitalBrain.Neuron<TState> (the "(store)" base argument goes).
    - this.GetPrimaryKeyString() -> Id.Key, PublishAsync( -> Publish(, [StatelessWorker] -> [StatelessNeuron].
  Options (06-04, for a family whose classes share a module base):
    - -Base Name: Neuron<T>(store) becomes Name<T> instead of DigitalBrain.Neuron<T> (a module-local base deriving it, e.g.
      Flutter's UiNeuron<T> with Snapshot, FullKey and ServiceProvider).
    - -KeyExpression Expr: this.GetPrimaryKeyString() becomes Expr instead of Id.Key (e.g. FullKey, the os key "{brain}/{key}"
      that signals and state carry).
  Also (06-04): an Orleans method alias ([Alias("read")] on a contract method, alone or in a list) goes, and
  [Alias("x"), Orleans.Metadata.DefaultGrainType(Y)] in one list becomes [NeuronAlias("x"), DefaultNeuronType(Y)].
  Everywhere: "using Orleans..." lines go; DigitalBrain usings are added when needed.

  What the script cannot decide is left as a compile error for hand fixing, never guessed:
    - GrainFactory.GetGrain<T>(k) becomes Get<T>(new NeuronId(Id.Brain, MigrateNeuronTypeOf_T, k)): name T's neuron type.
    - A Watch/Unwatch/Read override gets "#error migrate-neuron" above it: delete it and move its admission rule into
      AdmitSynapse (R4-03).
    - State reads and writes (state.State, WriteStateAsync) are left as they are: rewrite them to Save(next, signals) (D-02).

.EXAMPLE
  pwsh scripts/migrate-neuron.ps1 -Path src/IntoChat.OS.Modules/Time/IntoChat.OS.Modules.Time/Timers/TimerNeuron.cs
  pwsh scripts/migrate-neuron.ps1 -Path (Get-ChildItem src/IntoChat.OS.Modules/Time -Recurse -Filter *.cs) -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory, ValueFromPipeline, ValueFromPipelineByPropertyName)]
    [Alias('FullName')]
    [string[]]$Path,
    [string]$Base = 'DigitalBrain.Neuron',
    [string]$KeyExpression = 'Id.Key'
)

begin {
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    $changed = 0
    $Error.Clear()

    function Convert-NeuronSource([string]$text) {
        $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }

        # Orleans usings go (contracts and classes reference DigitalBrain only).
        $text = [regex]::Replace($text, '(?m)^[ \t]*(?:global\s+)?using\s+(?:static\s+)?Orleans(?:\.[\w.]+)?\s*;[ \t]*\r?\n', '')

        # Contracts: grain bases leave the base list.
        $text = [regex]::Replace($text, '(?m)(\binterface\s+\w+(?:<[^>]*>)?)[ \t]*:[ \t]*(?:INeuron|IGrainWithStringKey)[ \t]*(?:,[ \t]*(?:INeuron|IGrainWithStringKey)[ \t]*)*(?=[\r\n{;]|$)', '$1')
        $text = [regex]::Replace($text, '(?m)(\binterface\s+\w+(?:<[^>]*>)?\s*:\s*)(?:(?:INeuron|IGrainWithStringKey)\s*,\s*)+', '$1')
        $text = [regex]::Replace($text, '(?m)(\binterface\s+\w+(?:<[^>]*>)?\s*:[^{\r\n]*?)\s*,\s*(?:INeuron|IGrainWithStringKey)\b', '$1')
        $text = [regex]::Replace($text, '(?m)(\binterface\s+\w+(?:<[^>]*>)?\s*:[^{\r\n]*?)\s*,\s*(?:INeuron|IGrainWithStringKey)\b', '$1')

        # Contracts: neutral attributes.
        $text = [regex]::Replace($text, '\[(?:Orleans\.Metadata\.)?DefaultGrainType\(', '[DefaultNeuronType(')
        $text = [regex]::Replace($text, '(\[Alias\("[^"]*"\),\s*)(?:Orleans\.Metadata\.)?DefaultGrainType\(', '$1DefaultNeuronType(')
        # Orleans method aliases: in a list ([ReadOnly, Alias("read")]) or alone on a method line of a contract.
        # (06-04) also on a class method, where the list stands above "public [async] Task...".
        $text = [regex]::Replace($text, ',\s*Alias\("[^"]*"\)(?=[^\[\]]*\]\s*(?:public\s+(?:async\s+)?)?(?:Task|ValueTask|IAsyncEnumerable))', '')
        $text = [regex]::Replace($text, '\[Alias\("[^"]*"\),\s*(?=[^\[\]]*\]\s*(?:public\s+(?:async\s+)?)?(?:Task|ValueTask|IAsyncEnumerable))', '[')
        $text = [regex]::Replace($text, '(?m)^([ 	]*)\[Alias\("[^"]*"\)\]\s*(?=(?:\[[^\]]*\]\s*)*(?:Task|ValueTask|IAsyncEnumerable))', '$1')
        $text = [regex]::Replace($text, '(?m)^([ \t]*)\[Alias\(("[^"]*")\)\](?=\s*(?:\[[^\]]*\]\s*)*(?:public|internal)\s+(?:partial\s+)?interface\b)', '$1[NeuronAlias($2)]')
        $text = [regex]::Replace($text, '\[Alias\(("[^"]*")\),\s*(DefaultNeuronType\()', '[NeuronAlias($1), $2')
        $text = $text -replace '\[AlwaysInterleave\]', '[Interleaves]'
        $text = $text -replace '\[ReadOnly\]', '[ReadOnlyCall]'
        $text = $text -replace '\[OneWay\]', '[OneWayCall]'
        $text = [regex]::Replace($text, '\[MayInterleave\(', '[InterleavesWhen(')
        $text = [regex]::Replace($text, '\[ResponseTimeout\("([^"]+)"\)\]', {
                param($m)
                $span = [TimeSpan]::Parse($m.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
                "[CallTimeout($([int]$span.TotalMilliseconds))]"
            })

        # Signals: [GenerateSerializer, Alias("x")] record ... : Signal -> [Signal("x")] record ... : DigitalBrain.Signal.
        $text = [regex]::Replace($text, '\[GenerateSerializer,\s*Alias\(("[^"]*")\)\](\s*(?:public|internal)\s+(?:sealed\s+)?record\s+\w+(?:\((?:[^()]|\([^()]*\))*\))?\s*:\s*)Signal\b', '[Signal($1)]$2DigitalBrain.Signal', 'Singleline')
        # Only inside the [Signal] record's own declaration (06-04 fix): a state or definition record beside it in the same file
        # keeps its serializer ids, which its stored bytes still need.
        $text = [regex]::Replace($text, '\[Signal\("[^"]*"\)\]\s*(?:public|internal)\s+(?:sealed\s+)?record\s+\w+\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)', {
                param($m)
                [regex]::Replace($m.Value, '\[property:\s*Id\(\d+\)\]\s*', '')
            }, 'Singleline')

        # Classes: identity and base.
        $stateName = $null
        $persistent = [regex]::Match($text, '\[PersistentState\("([^"]+)"[^\]]*\]\s*IPersistentState<[^>]+>\s*\w+\s*,?\s*')
        if ($persistent.Success) {
            $stateName = $persistent.Groups[1].Value
            $text = $text.Remove($persistent.Index, $persistent.Length)
            $text = [regex]::Replace($text, ',\s*\)', ')')
            # A primary constructor left with no parameters goes (06-04).
            $text = [regex]::Replace($text, '(class\s+\w+)\(\s*\)(\s*:)', '$1$2')
        }
        $text = [regex]::Replace($text, '\[GrainType\(([^)]*)\)', {
                param($m)
                if ($stateName -and $stateName -ne 'state') { "[Neuron($($m.Groups[1].Value), State = `"$stateName`")" } else { "[Neuron($($m.Groups[1].Value))" }
            })
        $text = [regex]::Replace($text, '(\)\s*:\s*|class\s+\w+\s*:\s*)(?:IntoChat\.OS\.Framework\.)?Neuron<([^>]+)>\(\w+\)', "`$1$Base<`$2>")
        $text = [regex]::Replace($text, '(\)\s*:\s*|class\s+\w+\s*:\s*)(?:IntoChat\.OS\.Framework\.)?Neuron(?=\s*[,{\r\n])', '$1DigitalBrain.Neuron')
        $text = $text -replace '\[StatelessWorker\]', '[StatelessNeuron]'
        $text = $text -replace '(?:this\.)?GetPrimaryKeyString\(\)', $KeyExpression
        $text = [regex]::Replace($text, '\bPublishAsync\(', 'Publish(')
        $text = [regex]::Replace($text, '\bGrainFactory\.GetGrain<(\w+)>\(([^()]*(?:\([^()]*\))?[^()]*)\)', 'Get<$1>(new NeuronId(Id.Brain, MigrateNeuronTypeOf_$1, $2))')

        # Watch/Unwatch/Read overrides: flagged once, deleted by hand.
        $text = [regex]::Replace($text, '(?m)^([ \t]*)(?<!#error migrate-neuron[^\n]*\n\s*)(public\s+override\s+[^\r\n]*\b(?:Watch|Unwatch|Read)\()', "`$1#error migrate-neuron: delete this override; its admission rule moves to AdmitSynapse (R4-03)$nl`$1`$2")

        # Usings the rewritten text needs.
        $needs = @()
        if ($text -match '\[(?:NeuronAlias|DefaultNeuronType)\(' -and $text -notmatch '(?m)^using DigitalBrain\.Contracts;') { $needs += 'using DigitalBrain.Contracts;' }
        if ($text -match '\[(?:Neuron|Signal|Interleaves|ReadOnlyCall|OneWayCall|CallTimeout|InterleavesWhen|StatelessNeuron)\b' -and $text -notmatch '(?m)^using DigitalBrain;') { $needs += 'using DigitalBrain;' }
        if ($needs.Count -gt 0) {
            $first = [regex]::Match($text, '(?m)^using [^\r\n]+\r?\n')
            $insert = ($needs -join $nl) + $nl
            if ($first.Success) { $text = $text.Insert($first.Index, $insert) } else { $text = $insert + $nl + $text }
        }
        return $text
    }
}

process {
    foreach ($item in $Path) {
        $file = (Resolve-Path -LiteralPath $item).ProviderPath
        $before = [IO.File]::ReadAllText($file)
        $after = Convert-NeuronSource $before
        if ($after -ceq $before) { Write-Verbose "unchanged: $file"; continue }
        if ($PSCmdlet.ShouldProcess($file, 'migrate neuron source')) {
            [IO.File]::WriteAllText($file, $after, [Text.UTF8Encoding]::new($false))
            $changed++
            Write-Host "migrated: $file"
        }
    }
}

end {
    Write-Host "$changed file(s) changed. Build, then fix what the compiler names (MigrateNeuronTypeOf_*, #error migrate-neuron, state writes)."
}
