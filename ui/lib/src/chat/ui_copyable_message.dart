import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../theme/ui_tokens.dart';

/// Hosts that stream tokens expose current text through this scope.
final class UiStreamCopy extends InheritedNotifier<ChangeNotifier> {
  const UiStreamCopy({
    super.key,
    required ChangeNotifier super.notifier,
    required this.textFor,
    required super.child,
  });

  final String Function(String streamId) textFor;

  static String streamText(BuildContext context, String streamId) {
    final scope = context.dependOnInheritedWidgetOfExactType<UiStreamCopy>();
    return scope?.textFor(streamId) ?? '';
  }
}

/// Selectable message body plus a whole-message copy control and optional
/// extra inline message actions (e.g. read-aloud).
final class UiCopyableMessage extends StatefulWidget {
  const UiCopyableMessage({
    super.key,
    required this.copyText,
    required this.child,
    this.actions = const <Widget>[],
    this.keepActionsVisible = false,
  });

  /// Resolved on build (button visibility) and again on tap (latest stream text).
  final String Function(BuildContext context) copyText;
  final Widget child;
  final List<Widget> actions;
  final bool keepActionsVisible;

  @override
  State<UiCopyableMessage> createState() => _UiCopyableMessageState();
}

final class _UiCopyableMessageState extends State<UiCopyableMessage> {
  var _hovered = false;
  var _copied = false;
  Timer? _copiedTimer;

  @override
  void dispose() {
    _copiedTimer?.cancel();
    super.dispose();
  }

  Future<void> _copy() async {
    final text = widget.copyText(context).trim();
    if (text.isEmpty) {
      return;
    }
    await Clipboard.setData(ClipboardData(text: text));
    if (!mounted) {
      return;
    }
    _copiedTimer?.cancel();
    setState(() => _copied = true);
    _copiedTimer = Timer(const Duration(milliseconds: 1400), () {
      if (mounted) {
        setState(() => _copied = false);
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final text = widget.copyText(context);
    final canCopy = text.trim().isNotEmpty;
    final actionCount = (canCopy ? 1 : 0) + widget.actions.length;
    final colors = Theme.of(context).colorScheme;
    final tokens = UiTokens.of(context);
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: Stack(
        clipBehavior: Clip.none,
        children: [
          Padding(
            padding: EdgeInsets.only(
              right: actionCount > 0 ? actionCount * 32.0 : 0,
            ),
            child: SelectionArea(child: widget.child),
          ),
          if (actionCount > 0)
            Positioned(
              top: 2,
              right: 2,
              child: AnimatedOpacity(
                duration: MediaQuery.disableAnimationsOf(context)
                    ? Duration.zero
                    : tokens.quick,
                opacity: _hovered || _copied || widget.keepActionsVisible
                    ? 1
                    : 0.55,
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    for (var i = 0; i < widget.actions.length; i++) ...[
                      if (i > 0) const SizedBox(width: 4),
                      widget.actions[i],
                    ],
                    if (canCopy) ...[
                      if (widget.actions.isNotEmpty) const SizedBox(width: 4),
                      Material(
                        color: colors.surfaceContainerHigh.withValues(
                          alpha: 0.92,
                        ),
                        shape: const CircleBorder(),
                        child: InkWell(
                          key: const Key('ui_copy_message'),
                          customBorder: const CircleBorder(),
                          onTap: _copy,
                          child: Tooltip(
                            message: _copied ? 'Copied' : 'Copy message',
                            child: Padding(
                              padding: const EdgeInsets.all(6),
                              child: Icon(
                                _copied
                                    ? Icons.check_rounded
                                    : Icons.copy_outlined,
                                size: 14,
                                color: _copied
                                    ? colors.primary
                                    : colors.onSurfaceVariant,
                              ),
                            ),
                          ),
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ),
        ],
      ),
    );
  }
}
