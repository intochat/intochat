// How a client reads a screen's UI child (`Screen.Child<T>` / `IScreen.ReadChild`):
// by its app's key path over the `/children/` wire route, never by the child's neuron
// name (the kernel names a screen's children by key only).

/// True when a `NeuronRef` (wire `refKind: 'child'`) names a child of the view it is
/// drawn in (`Screen.Child<T>`): a bare key. A ref naming a whole address
/// (`local/units/bank`) embeds that neuron's own view by name.
bool isScreenChild(String refKind, String uri) =>
    refKind == 'child' && !uri.contains('/');

/// True for a child key a view may name: non-empty, not `.` or `..`, no `/`.
bool isChildKey(String key) =>
    key.isNotEmpty && key != '.' && key != '..' && !key.contains('/');

/// The path under `/brains/{brain}/apps/` that reads a node: a UI child
/// (wire kind `child`, named `{brain}/apps/{app}/children/{path}`) through its app's
/// key path `{app}/children/{path}` (handled by `IScreen.ReadChild`), any other node
/// by kind and name.
String appNodePath(String kind, String name) {
  if (kind == 'child') {
    final at = name.indexOf('/apps/');
    final rest = at < 0 ? '' : name.substring(at + '/apps/'.length);
    final children = rest.indexOf('/children/');
    if (children <= 0) {
      throw ArgumentError.value(name, 'name', 'not an app child');
    }
    final path = rest
        .substring(children + '/children/'.length)
        .split('/')
        .map(Uri.encodeComponent)
        .join('/');
    return '${Uri.encodeComponent(rest.substring(0, children))}/children/$path';
  }
  return 'node?${Uri(queryParameters: {'kind': kind, 'name': name}).query}';
}
