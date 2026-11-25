import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../design/tokens.dart';
import '../theme/ui_colors.dart';
import '../theme/ui_theme.dart';
import '../theme/ui_tokens.dart';
import '../theme/wire_node_values.dart';
import '../composition/child_route.dart';
import 'node_context.dart';

/// Maximum nested `NeuronRef` composition depth before halting recursion.
const int maxEmbedDepth = 4;

/// Returns true when [refKind] denotes an embeddable live `ViewHost` child
/// (`"view"`, `"child"`, `"app"`, `"unit"`, or `""`), excluding `"browser"`,
/// `"webbrowser"`, `"viewport"`, and `"confirmation"`.
bool isEmbedRefKind(String refKind) => isEmbedNeuronRefKind(refKind);

/// Normalizes a `NeuronRef` [neuronUri] into the target `view` neuron name
/// relative to [parentView]'s brain scope when needed.
String resolveEmbedTargetName({
  required String parentView,
  required String refKind,
  required String neuronUri,
}) {
  var uri = neuronUri.trim();
  if (uri.startsWith('neuron://')) {
    uri = uri.substring('neuron://'.length);
  }
  if (uri.isEmpty) return uri;

  final String brain;
  final appsIdx = parentView.indexOf('/apps/');
  if (appsIdx > 0) {
    brain = parentView.substring(0, appsIdx);
  } else {
    final slashIdx = parentView.indexOf('/');
    brain = slashIdx > 0 ? parentView.substring(0, slashIdx) : '';
  }

  if (brain.isEmpty || uri.startsWith('$brain/')) {
    return uri;
  }

  if (isAppNeuronRefKind(refKind) && !uri.contains('/')) {
    return '$brain/apps/$uri/view';
  }
  if (!uri.contains('/') ||
      uri.startsWith('apps/') ||
      uri.startsWith('units/') ||
      uri.startsWith('views/')) {
    return '$brain/$uri';
  }
  return uri;
}

/// A live neuron surface (a nested child or embedded view, a `viewport`, or a `webbrowser`)
/// hosted in place; without a host (or for `confirmation`), its display-only URI chip.
Widget renderNeuronRef(NeuronRefNode node, NodeContext ctx) {
  final rawUri = node.neuronUri.trim();
  if (isEmbedRefKind(node.refKind) && rawUri.isNotEmpty) {
    final resolved = resolveEmbedTargetName(
      parentView: ctx.view,
      refKind: node.refKind,
      neuronUri: rawUri,
    );
    // A `child` names only its key: its host guards cycles by key path.
    final keyed = isScreenChild(node.refKind, rawUri);
    if (!keyed &&
        (ctx.childAncestors.contains(rawUri) ||
            ctx.childAncestors.contains(resolved) ||
            resolved == ctx.view)) {
      return EmbedGuardNotice(
        neuronUri: node.neuronUri,
        message: 'This component cannot be displayed.',
      );
    }
    if (ctx.childDepth >= maxEmbedDepth) {
      return EmbedGuardNotice(
        neuronUri: node.neuronUri,
        message: 'Nested view limit reached.',
      );
    }
  }

  final props = node.props;
  return ctx.reference?.call(
        node.refKind,
        node.neuronUri,
        props is Map ? Map<String, dynamic>.from(props) : const {},
      ) ??
      _NeuronUriChip(neuronUri: node.neuronUri);
}

class _NeuronUriChip extends StatelessWidget {
  const _NeuronUriChip({required this.neuronUri});

  final String neuronUri;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      decoration: BoxDecoration(
        color: c.recessed,
        borderRadius: BorderRadius.circular(tokens.xs),
        border: Border.all(color: c.hairline),
      ),
      child: SelectableText(
        neuronUri,
        style: UiType.tabularStyle(
          12,
          weight: FontWeight.w600,
          color: c.accent,
        ).copyWith(letterSpacing: -0.1),
      ),
    );
  }
}

/// Non-recursive notice rendered when a `NeuronRef` child hits a cycle or
/// exceeds [maxEmbedDepth].
class EmbedGuardNotice extends StatelessWidget {
  const EmbedGuardNotice({
    super.key,
    required this.neuronUri,
    required this.message,
  });

  final String neuronUri;
  final String message;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    return Container(
      key: ValueKey('embed-guard:$neuronUri'),
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        color: c.surfaceSunk,
        borderRadius: BorderRadius.circular(tokens.sm),
        border: Border.all(color: c.borderHairline),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(LucideIcons.triangleAlert, size: 14, color: c.textMuted),
          const SizedBox(width: 8),
          Flexible(
            child: Text(
              message,
              style: UiType.textStyle(12.5, color: c.textMuted),
            ),
          ),
        ],
      ),
    );
  }
}

/// Warm `surfaceSunk` skeleton shimmer (`DESIGN.md` §2.1 & §7: 1100 ms opacity
/// pulse, static under reduced motion or widget test bindings).
class EmbedSkeleton extends StatefulWidget {
  const EmbedSkeleton({super.key, this.compact = false});

  final bool compact;

  @override
  State<EmbedSkeleton> createState() => _EmbedSkeletonState();
}

class _EmbedSkeletonState extends State<EmbedSkeleton>
    with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(
    vsync: this,
    duration: IcMotion.skeleton,
  );

  bool get _isTestBinding =>
      WidgetsBinding.instance.runtimeType.toString().contains('Test');

  @override
  void initState() {
    super.initState();
    if (!_isTestBinding) {
      _controller.repeat(reverse: true);
    } else {
      _controller.value = 0.75;
    }
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final disable = MediaQuery.maybeDisableAnimationsOf(context) ?? false;
    if (disable || _isTestBinding) {
      if (_controller.isAnimating) _controller.stop();
      _controller.value = 0.75;
    } else if (!_controller.isAnimating) {
      _controller.repeat(reverse: true);
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final tokens = UiTokens.of(context);
    final barFactors = widget.compact
        ? const <double>[0.58, 0.36]
        : const <double>[0.45, 0.82, 0.64];

    return Semantics(
      label: 'Loading view',
      child: AnimatedBuilder(
        animation: _controller,
        builder: (context, child) =>
            Opacity(opacity: 0.45 + (_controller.value * 0.55), child: child),
        child: Container(
          key: const ValueKey('embed-skeleton'),
          padding: EdgeInsets.symmetric(
            horizontal: widget.compact ? 12 : 16,
            vertical: widget.compact ? 10 : 14,
          ),
          decoration: BoxDecoration(
            color: c.surfaceCard,
            borderRadius: BorderRadius.circular(tokens.card),
            border: Border.all(color: c.borderHairline),
          ),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              for (var i = 0; i < barFactors.length; i++) ...[
                if (i > 0) const SizedBox(height: 8),
                FractionallySizedBox(
                  alignment: Alignment.centerLeft,
                  widthFactor: barFactors[i],
                  child: Container(
                    height: i == 0 ? 12 : 10,
                    decoration: BoxDecoration(
                      color: c.surfaceSunk,
                      borderRadius: BorderRadius.circular(tokens.xs),
                    ),
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
