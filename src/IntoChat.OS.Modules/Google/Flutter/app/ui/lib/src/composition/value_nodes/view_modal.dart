import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../components/kit/ui_kit_surface.dart';
import '../../theme/ui_colors.dart';
import '../../theme/ui_shadows.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';

/// Modal value node, shown while it is in the tree. Escape or the backdrop
/// triggers the first Secondary or Ghost Button child (the cancel button);
/// without one they do nothing.
final class ViewModal extends StatefulWidget {
  const ViewModal({
    super.key,
    required this.node,
    required this.children,
    required this.action,
  });
  final Map node;

  /// Child maps paired with their rendered widgets.
  final List<(Map, Widget)> children;
  final VoidCallback? Function(Map node) action;

  @override
  State<ViewModal> createState() => _ViewModalState();
}

class _ViewModalState extends State<ViewModal> {
  final _portal = OverlayPortalController();

  @override
  void initState() {
    super.initState();
    _portal.show();
  }

  void _cancel() {
    for (final (child, _) in widget.children) {
      if (child['kind'] == 1 && (child['style'] == 1 || child['style'] == 2)) {
        widget.action(child)?.call();
        return;
      }
    }
  }

  @override
  Widget build(BuildContext context) => OverlayPortal(
    controller: _portal,
    overlayChildBuilder: _overlay,
    child: const SizedBox.shrink(),
  );

  Widget _overlay(BuildContext context) {
    final c = UiColors.of(context);
    final type = UiType.of(context);
    final title = widget.node['text'] as String? ?? '';
    final detail = widget.node['detail'] as String? ?? '';
    final buttons = [
      for (final (m, w) in widget.children)
        if (m['kind'] == 1) w,
    ];
    final body = [
      for (final (m, w) in widget.children)
        if (m['kind'] != 1) w,
    ];
    return CallbackShortcuts(
      bindings: {const SingleActivator(LogicalKeyboardKey.escape): _cancel},
      child: FocusScope(
        autofocus: true,
        child: Stack(
          children: [
            Positioned.fill(
              child: GestureDetector(
                onTap: _cancel,
                child: ColoredBox(color: c.strong.withValues(alpha: .55)),
              ),
            ),
            Center(
              child: Material(
                type: MaterialType.transparency,
                child: Container(
                  constraints: const BoxConstraints(maxWidth: 440),
                  margin: const EdgeInsets.all(24),
                  padding: const EdgeInsets.all(32),
                  decoration: BoxDecoration(
                    color: c.surface,
                    borderRadius: BorderRadius.circular(
                      UiTokens.of(context).modalRadius,
                    ),
                    border: Border.all(color: c.hairline),
                    boxShadow: UiShadows.xl(uiKitShadowBase(context)),
                  ),
                  child: Semantics(
                    scopesRoute: true,
                    namesRoute: true,
                    explicitChildNodes: true,
                    label: title,
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        if (title.isNotEmpty)
                          Text(
                            title,
                            style: type.heading,
                            textAlign: TextAlign.center,
                          ),
                        if (detail.isNotEmpty) ...[
                          const SizedBox(height: 8),
                          Text(
                            detail,
                            style: type.bodyMuted,
                            textAlign: TextAlign.center,
                          ),
                        ],
                        for (final w in body) ...[
                          const SizedBox(height: 16),
                          w,
                        ],
                        if (buttons.isNotEmpty) ...[
                          const SizedBox(height: 24),
                          Wrap(
                            alignment: WrapAlignment.center,
                            spacing: 10,
                            runSpacing: 10,
                            children: buttons,
                          ),
                        ],
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
