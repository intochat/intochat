part of 'neuron_view.dart';

/// Inline presentation values never acquire their own load or watch lifecycle.
class _ImmutableView extends StatelessWidget {
  const _ImmutableView({
    required this.name,
    required this.generation,
    required this.root,
    required this.enabled,
    required this.dispatch,
    required this.markdown,
    required this.reference,
    required this.layout,
  });
  final String name;
  final int generation;
  final Map root;
  final bool enabled;
  final Future<void> Function(Map<String, dynamic>) dispatch;
  final Widget Function(String) markdown;
  final Widget Function(Map, Key) reference;
  final Widget Function(Map<String, dynamic>, List<Widget>, List) layout;

  static final _random = Random.secure();
  static String _operationId() {
    final bytes = List.generate(16, (_) => _random.nextInt(256));
    bytes[6] = (bytes[6] & 15) | 64;
    bytes[8] = (bytes[8] & 63) | 128;
    final hex = bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join();
    return '${hex.substring(0, 8)}-${hex.substring(8, 12)}-${hex.substring(12, 16)}-${hex.substring(16, 20)}-${hex.substring(20)}';
  }

  Widget _node(BuildContext context, Map node, int depth) {
    if (depth > 32) return const Text('This component cannot be displayed.');
    final key = ValueKey('view:$name:${node['key']}');
    final children = (node['children'] as List? ?? const [])
        .whereType<Map>()
        .take(512)
        .toList();
    final widgets = [
      for (final child in children) _node(context, child, depth + 1),
    ];
    final text = node['text'] as String? ?? '';
    Widget content;
    switch (node['kind']) {
      case 0:
        content = markdown(text);
      case 1:
        // Server slots shorter than the 48px button get the 36px compact one.
        content = LayoutBuilder(
          builder: (context, constraints) => UiKitButton(
            label: text,
            compact: constraints.maxHeight < 48,
            onPressed:
                enabled && node['enabled'] != false && node['action'] is String
                ? () {
                    FocusScope.of(context).unfocus();
                    unawaited(
                      dispatch({
                        'kind': 'view',
                        'name': name,
                        'revision': generation,
                        'field': node['key'],
                        'action': node['action'],
                        'operationId': _operationId(),
                      }).catchError((Object _) {}),
                    );
                  }
                : null,
          ),
        );
      case 2:
      case 3:
        content = layout(
          {
            'mode': node['kind'] == 3
                ? 'row'
                : (node['extents'] as List? ?? const []).isEmpty
                ? 'list'
                : 'column',
            'extents': node['extents'],
            'gap': node['gap'],
            'followEnd': node['followEnd'],
          },
          widgets,
          children,
        );
      case 4:
        content = SizedBox(
          width: double.infinity,
          child: UiKitSurface(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                if (text.isNotEmpty)
                  Text(text, style: Theme.of(context).textTheme.titleLarge),
                if (node['detail'] case final String detail
                    when detail.isNotEmpty)
                  markdown(detail),
                ...widgets,
              ],
            ),
          ),
        );
      case 5:
        final target = node['reference'];
        if (target is Map &&
            target['kind'] is String &&
            target['name'] is String) {
          return reference(target, key);
        }
        content = const Text('This component cannot be displayed.');
      default:
        content = const Text('This component cannot be displayed.');
    }
    return KeyedSubtree(key: key, child: content);
  }

  @override
  Widget build(BuildContext context) => _node(context, root, 0);
}
