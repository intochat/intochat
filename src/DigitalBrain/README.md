# DigitalBrain

Neuron, signal, subscription, observer, and brain — the words — plus everything a script or
client may compile against: data rows, semantic types, lifecycle signals, the script edge
protocol, account references, enforcement shapes, names and the configuration grammar.

This is the vocabulary ring. It is what `ScriptContracts` ships into the sandbox, what module
`*.Contracts` projects reference, and what the Clients build on. It references Orleans
abstractions because the words are grains, and never Orleans hosting: `Module.Configure`
and everything else host-shaped lives in `DigitalBrain.Framework`.
