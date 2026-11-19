// How a client reads a view's slot child: by its app's key path, never by
// the child's neuron name (the kernel names a screen's children by key only).

/// True when a `NeuronRef` names a child of the view it is drawn in: a
/// `slot` whose uri is a bare key. A slot naming a whole address
/// (`local/units/bank`) embeds that neuron's own view by name.
bool isChildSlot(String refKind, String uri) =>
    refKind == 'slot' && !uri.contains('/');

/// True for a slot key a view may name: non-empty, not `.` or `..`, no `/`.
bool isSlotKey(String key) =>
    key.isNotEmpty && key != '.' && key != '..' && !key.contains('/');

/// The path under `/brains/{brain}/apps/` that reads a node: a slot child
/// (kind `slot`, named `{brain}/apps/{app}/slots/{path}`) through its app's
/// key path `{app}/slots/{path}`, any other node by kind and name.
String appNodePath(String kind, String name) {
  if (kind == 'slot') {
    final at = name.indexOf('/apps/');
    final rest = at < 0 ? '' : name.substring(at + '/apps/'.length);
    final slots = rest.indexOf('/slots/');
    if (slots <= 0) throw ArgumentError.value(name, 'name', 'not an app slot');
    final path = rest
        .substring(slots + '/slots/'.length)
        .split('/')
        .map(Uri.encodeComponent)
        .join('/');
    return '${Uri.encodeComponent(rest.substring(0, slots))}/slots/$path';
  }
  return 'node?${Uri(queryParameters: {'kind': kind, 'name': name}).query}';
}

/// True when [neuron] is a screen app's top-level neuron, `{brain}/apps/{app}`:
/// every command its views send goes to `POST /brains/{brain}/act`, a root
/// button's included. Units and surfaces keep the activation path.
bool isScreenAppNeuron(String neuron) {
  final parts = neuron.split('/');
  return parts.length == 3 &&
      parts[0].isNotEmpty &&
      parts[1] == 'apps' &&
      parts[2].isNotEmpty;
}
