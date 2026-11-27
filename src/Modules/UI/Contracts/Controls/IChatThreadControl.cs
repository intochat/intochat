using DigitalBrain;
using DigitalBrain.Contracts;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>A chat thread: it owns the messages and signals what the person asks of them. A screen mounts it with <c>Child&lt;IChatThreadControl, ChatThreadProps&gt;(key, props)</c>.</summary>
[NeuronAlias("control.chatThread")]
[DefaultNeuronType("control.chatThread")]
public interface IChatThreadControl : IScreen, IControl<ChatThreadProps>;
