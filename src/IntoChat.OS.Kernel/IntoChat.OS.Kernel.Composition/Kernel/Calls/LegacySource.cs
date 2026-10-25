namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-02, deleted in 06-08): the runtime admits a synapse only through the plain class hosted for the source's type
// (SynapseEngine.Admit), and a legacy Framework publisher has none. A module whose plain neuron subscribes to a legacy publisher
// declares a stand-in [Neuron("<the legacy grain type>")] class deriving this one in a hosted assembly; it admits subscribers of
// the same brain (the runtime default) and serves nothing else. Signals still come from the legacy grain, through
// LegacySignalJournal into the shared BrainLog. The stand-in leaves with its family's migration.
public abstract class LegacySource : DigitalBrain.Neuron;
