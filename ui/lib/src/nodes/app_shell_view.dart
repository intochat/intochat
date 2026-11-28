import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../components/workspace/ui_workspace_components.dart';
import '../primitives/resizable_sash.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_tokens.dart';
import '../theme/wire_node_values.dart';
import 'layout_nodes.dart';
import 'node_context.dart';
import 'node_renderer.dart';

const double kShellSidebarDefaultWidth = 248;
const double kShellSidebarMinWidth = 180;
const double kShellSidebarMaxWidth = 360;

const double kShellInspectorDefaultWidth = 320;
const double kShellInspectorMinWidth = 260;
const double kShellInspectorMaxWidth = 640;

/// Studio-grade desktop application shell (`AppShellNode`):
/// - Flush, compact `42px`–`48px` top toolbar header with 1-click sidebar and
///   inspector collapse/expand buttons (`key:shell-toggle-sidebar`,
///   `key:shell-toggle-inspector`).
/// - Draggable & keyboard-adjustable resize sashes on `sidebar` (`180–360px`,
///   default `248px`) and `inspector` (`260–640px`, default `320px`).
/// - Smooth `180ms` animated body transitions (`fade-through`, `shared-axis-x`,
///   `shared-axis-z`, `camera-fly-to`) respecting reduced-motion preferences.
/// - Clean vertical stack fallback on compact viewports (`< 640px`).
Widget renderAppShell(AppShellNode node, NodeContext ctx) => Semantics(
  container: true,
  explicitChildNodes: true,
  label: controlNameOf(node.automationName, node.key, node.key),
  child: AppShellView(node: node, ctx: ctx),
);

/// Responsive desktop application shell view for [AppShellNode].
class AppShellView extends StatefulWidget {
  const AppShellView({super.key, required this.node, required this.ctx});

  final AppShellNode node;
  final NodeContext ctx;

  @override
  State<AppShellView> createState() => _AppShellViewState();
}

class _AppShellViewState extends State<AppShellView> {
  double _sidebarWidth = kShellSidebarDefaultWidth;
  double _inspectorWidth = kShellInspectorDefaultWidth;
  bool _sidebarCollapsed = false;
  bool _inspectorCollapsed = false;

  Widget _buildPaneToggle({
    required UiColors c,
    required String identifier,
    required String buttonKey,
    required IconData icon,
    required bool expanded,
    required String expandLabel,
    required String collapseLabel,
    required bool enabled,
    required VoidCallback onToggle,
  }) {
    final label = expanded ? collapseLabel : expandLabel;
    return Semantics(
      key: ValueKey(buttonKey),
      identifier: identifier,
      button: true,
      enabled: enabled,
      toggled: expanded,
      label: label,
      onTap: enabled ? onToggle : null,
      child: ExcludeSemantics(
        child: Tooltip(
          message: label,
          child: Material(
            color: Colors.transparent,
            child: InkWell(
              borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
              onTap: enabled ? onToggle : null,
              child: Container(
                width: 28,
                height: 28,
                decoration: BoxDecoration(
                  color: expanded ? c.accentTint : Colors.transparent,
                  borderRadius: BorderRadius.circular(UiTokens.of(context).xs),
                  border: Border.all(
                    color: expanded
                        ? c.accent.withValues(alpha: 0.35)
                        : c.borderHairline,
                  ),
                ),
                child: Center(
                  child: Icon(
                    icon,
                    size: 14,
                    color: expanded ? c.accent : c.textMuted,
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final node = widget.node;
    final ctx = widget.ctx;
    final inner = ctx.deeper();

    return LayoutBuilder(
      builder: (context, constraints) {
        final boundedHeight =
            constraints.hasBoundedHeight && constraints.maxHeight.isFinite;
        final boundedWidth =
            constraints.hasBoundedWidth && constraints.maxWidth.isFinite;
        final bodyWidget = NodeRenderer.render(node.body, inner);
        final bodyFills = NodeRenderer.fills(node.body);

        final compact =
            boundedWidth && constraints.maxWidth < kCompactSplitPaneBreakpoint;

        if (compact) {
          final stacked = Padding(
            padding: const EdgeInsets.fromLTRB(8, 6, 8, 10),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: 8,
              children: [
                if (node.toolbar case final toolbar?)
                  NodeRenderer.render(toolbar, inner),
                if (node.sidebar case final sidebar?)
                  Container(
                    padding: const EdgeInsets.all(8),
                    decoration: BoxDecoration(
                      color: c.surfaceSunk,
                      borderRadius: BorderRadius.circular(
                        UiTokens.of(context).card,
                      ),
                      border: Border.all(color: c.borderHairline),
                    ),
                    child: NodeRenderer.render(sidebar, inner),
                  ),
                bodyWidget,
                if (node.inspector case final inspector?)
                  Container(
                    padding: const EdgeInsets.all(8),
                    decoration: BoxDecoration(
                      color: c.surfaceSunk,
                      borderRadius: BorderRadius.circular(
                        UiTokens.of(context).card,
                      ),
                      border: Border.all(color: c.borderHairline),
                    ),
                    child: NodeRenderer.render(inspector, inner),
                  ),
              ],
            ),
          );
          return boundedHeight
              ? SingleChildScrollView(primary: false, child: stacked)
              : stacked;
        }

        Widget scrollPane(
          Node paneNode,
          Widget rendered, {
          EdgeInsets? padding,
        }) {
          if (!boundedHeight || NodeRenderer.fills(paneNode)) {
            return rendered;
          }
          return SingleChildScrollView(
            primary: false,
            padding: padding ?? const EdgeInsets.only(bottom: 10),
            child: rendered,
          );
        }

        final transition = parseViewTransition(node.transition);
        final bodySignature = shellBodyTransitionSignature(
          node.body,
          node.transition,
        );

        final Widget rawMain = boundedHeight
            ? (bodyFills
                  ? bodyWidget
                  : SingleChildScrollView(
                      primary: false,
                      padding: const EdgeInsets.only(bottom: 10),
                      child: bodyWidget,
                    ))
            : bodyWidget;

        final Widget mainArea = UiViewTransition(
          transition: transition,
          signature: bodySignature,
          child: rawMain,
        );

        final hasSidebar = node.sidebar != null;
        final hasInspector = node.inspector != null;
        final showSidebar = hasSidebar && !_sidebarCollapsed;
        final showInspector = hasInspector && !_inspectorCollapsed;

        // Compute clamped side rail widths so body always retains healthy space.
        final contentAvailWidth = boundedWidth
            ? math.max(0.0, constraints.maxWidth - 16.0)
            : 1440.0;
        final minBodyReserve = math.min(220.0, contentAvailWidth * 0.32);
        var maxSideBudget = math.max(
          0.0,
          contentAvailWidth -
              minBodyReserve -
              (showSidebar ? kResizableSashHitWidth : 0.0) -
              (showInspector ? kResizableSashHitWidth : 0.0),
        );

        double effectiveSidebar = 0;
        if (showSidebar) {
          final rawSide = _sidebarWidth.clamp(
            kShellSidebarMinWidth,
            kShellSidebarMaxWidth,
          );
          final cap = showInspector ? maxSideBudget * 0.46 : maxSideBudget;
          effectiveSidebar = cap >= kShellSidebarMinWidth
              ? rawSide.clamp(kShellSidebarMinWidth, cap)
              : math.max(0.0, cap);
          maxSideBudget = math.max(0.0, maxSideBudget - effectiveSidebar);
        }

        double effectiveInspector = 0;
        if (showInspector) {
          final rawInsp = _inspectorWidth.clamp(
            kShellInspectorMinWidth,
            kShellInspectorMaxWidth,
          );
          effectiveInspector = maxSideBudget >= kShellInspectorMinWidth
              ? rawInsp.clamp(
                  kShellInspectorMinWidth,
                  math.min(kShellInspectorMaxWidth, maxSideBudget),
                )
              : math.max(0.0, maxSideBudget);
        }

        final sidebarToggle = hasSidebar
            ? _buildPaneToggle(
                c: c,
                identifier: 'key:shell-toggle-sidebar',
                buttonKey: 'shell-toggle-sidebar',
                icon: LucideIcons.panelLeft,
                expanded: !_sidebarCollapsed,
                expandLabel: 'Expand sidebar',
                collapseLabel: 'Collapse sidebar',
                enabled: ctx.enabled,
                onToggle: () =>
                    setState(() => _sidebarCollapsed = !_sidebarCollapsed),
              )
            : null;

        final inspectorToggle = hasInspector
            ? _buildPaneToggle(
                c: c,
                identifier: 'key:shell-toggle-inspector',
                buttonKey: 'shell-toggle-inspector',
                icon: LucideIcons.panelRight,
                expanded: !_inspectorCollapsed,
                expandLabel: 'Expand inspector',
                collapseLabel: 'Collapse inspector',
                enabled: ctx.enabled,
                onToggle: () =>
                    setState(() => _inspectorCollapsed = !_inspectorCollapsed),
              )
            : null;

        final panesRow = Row(
          crossAxisAlignment: boundedHeight
              ? CrossAxisAlignment.stretch
              : CrossAxisAlignment.start,
          children: [
            if (showSidebar) ...[
              SizedBox(
                key: const ValueKey('shell-sidebar-pane'),
                width: effectiveSidebar,
                child: Padding(
                  padding: const EdgeInsets.only(right: 6),
                  child: scrollPane(
                    node.sidebar!,
                    NodeRenderer.render(node.sidebar!, inner),
                  ),
                ),
              ),
              UiResizableSash(
                key: const ValueKey('shell_sidebar_sash'),
                semanticsIdentifier: 'key:shell-sash-sidebar',
                label: 'Resize sidebar',
                enabled: ctx.enabled,
                onDelta: (dx) {
                  setState(() {
                    _sidebarWidth = (effectiveSidebar + dx).clamp(
                      kShellSidebarMinWidth,
                      kShellSidebarMaxWidth,
                    );
                  });
                },
                onReset: () =>
                    setState(() => _sidebarWidth = kShellSidebarDefaultWidth),
              ),
              const SizedBox(width: 6),
            ],
            if (boundedWidth) Expanded(child: mainArea) else mainArea,
            if (showInspector) ...[
              const SizedBox(width: 6),
              UiResizableSash(
                key: const ValueKey('shell_inspector_sash'),
                semanticsIdentifier: 'key:shell-sash-inspector',
                label: 'Resize inspector',
                enabled: ctx.enabled,
                onDelta: (dx) {
                  // Inspector sits on the right rail: dragging left (-dx) widens it.
                  setState(() {
                    _inspectorWidth = (effectiveInspector - dx).clamp(
                      kShellInspectorMinWidth,
                      kShellInspectorMaxWidth,
                    );
                  });
                },
                onReset: () => setState(
                  () => _inspectorWidth = kShellInspectorDefaultWidth,
                ),
              ),
              SizedBox(
                key: const ValueKey('shell-inspector-pane'),
                width: effectiveInspector,
                child: Padding(
                  padding: const EdgeInsets.only(left: 6),
                  child: scrollPane(
                    node.inspector!,
                    NodeRenderer.render(node.inspector!, inner),
                  ),
                ),
              ),
            ],
          ],
        );

        final isEdgeToEdge =
            node.edgeToEdge ||
            (node.body is WebBrowserNode &&
                node.sidebar == null &&
                node.inspector == null);
        final paddedPanes = isEdgeToEdge
            ? panesRow
            : Padding(
                padding: const EdgeInsets.fromLTRB(8, 6, 8, 8),
                child: panesRow,
              );

        if (node.toolbar == null &&
            sidebarToggle == null &&
            inspectorToggle == null) {
          return paddedPanes;
        }

        final headerBar = Container(
          key: const ValueKey('shell-header-bar'),
          constraints: const BoxConstraints(minHeight: 40),
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            border: Border(bottom: BorderSide(color: c.borderHairline)),
          ),
          child: Row(
            mainAxisSize: boundedWidth ? MainAxisSize.max : MainAxisSize.min,
            children: [
              if (sidebarToggle != null) ...[
                sidebarToggle,
                const SizedBox(width: 6),
              ],
              if (node.toolbar != null)
                if (boundedWidth)
                  Expanded(child: NodeRenderer.render(node.toolbar!, inner))
                else
                  NodeRenderer.render(node.toolbar!, inner)
              else if (boundedWidth)
                const Spacer(),
              if (inspectorToggle != null) ...[
                const SizedBox(width: 6),
                inspectorToggle,
              ],
            ],
          ),
        );

        return Column(
          mainAxisSize: boundedHeight ? MainAxisSize.max : MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            headerBar,
            if (boundedHeight) Expanded(child: paddedPanes) else paddedPanes,
          ],
        );
      },
    );
  }
}
