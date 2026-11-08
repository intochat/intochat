import 'package:flutter/widgets.dart';

/// Maps focus targets (`kind:name` or `window:{appId}`) to on-screen rects.
class FocusRegistry {
  FocusRegistry();

  static final FocusRegistry instance = FocusRegistry();

  final Map<String, GlobalKey> _keys = {};

  void register(String target, GlobalKey key) => _keys[target] = key;

  /// Removes the target; when [key] is given, only if it is still the
  /// registered one.
  void unregister(String target, [GlobalKey? key]) {
    if (key == null || identical(_keys[target], key)) _keys.remove(target);
  }

  Rect? resolve(String target) {
    final box = _keys[target]?.currentContext?.findRenderObject();
    if (box is! RenderBox || !box.attached || !box.hasSize) return null;
    return box.localToGlobal(Offset.zero) & box.size;
  }
}
