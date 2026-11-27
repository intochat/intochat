import 'package:flutter/widgets.dart';
import 'package:lucide_icons_flutter/lucide_icons.dart';

import '../../primitives/button.dart';
import '../../primitives/display_primitives.dart';
import '../../theme/ui_colors.dart';
import '../../theme/ui_theme.dart';
import '../../theme/ui_tokens.dart';
import 'card_shell.dart';

enum JobState { running, done, cancelled, error }

enum StepStatus { waiting, running, done, failed }

class JobStep {
  JobStep(this.label, {this.detail, this.status = StepStatus.waiting});

  final String label;
  String? detail;
  StepStatus status;
}

class JobData {
  JobData({
    required this.title,
    required this.steps,
    this.progress = 0,
    this.resultText,
    this.errorText,
    this.elapsed,
  });

  final String title;
  final List<JobStep> steps;
  double progress;

  /// Collapsed line when done, e.g. "Checked 16 opportunities, 14 slipped".
  String? resultText;
  String? errorText;
  String? elapsed;
}

/// Job progress: a task running in the background, step by step.
class JobCard extends StatelessWidget {
  const JobCard({
    super.key,
    required this.source,
    required this.data,
    required this.state,
    this.onCancel,
    this.onRetry,
    this.onOpenArtifact,
    this.openArtifactLabel = 'Open artifact',
  });

  final CardSource source;
  final JobData data;
  final JobState state;
  final VoidCallback? onCancel;
  final VoidCallback? onRetry;
  final VoidCallback? onOpenArtifact;
  final String openArtifactLabel;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final d = data;
    switch (state) {
      case JobState.done:
        return CardResult(
          source: source,
          tone: ResultTone.done,
          text: d.resultText ?? d.title,
          detail: d.elapsed,
          expanded: _body(context, c),
        );
      case JobState.cancelled:
        return CardResult(
          source: source,
          tone: ResultTone.rejected,
          text: 'Stopped',
          detail: d.title,
        );
      default:
        return _body(context, c);
    }
  }

  Widget _body(BuildContext context, UiColors c) {
    final d = data;
    final running = state == JobState.running;
    final statusChip = switch (state) {
      JobState.running => CardChip(
        label: 'Working',
        fg: c.textBody,
        bg: c.surfaceSunk,
        icon: WorkingSpinner(size: 12, color: c.textBody),
      ),
      JobState.done => CardChip(
        label: 'Done',
        fg: c.done,
        bg: c.doneBg,
        icon: Icon(LucideIcons.check, size: 12, color: c.done),
      ),
      JobState.error => CardChip(
        label: 'Failed',
        fg: c.error,
        bg: c.errorBg,
        icon: Icon(LucideIcons.triangleAlert, size: 12, color: c.error),
      ),
      JobState.cancelled => null,
    };
    return CardShell(
      source: source,
      status: statusChip,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(
            d.title,
            style: UiType.textStyle(
              15,
              weight: FontWeight.w600,
              color: c.textStrong,
            ),
          ),
          const SizedBox(height: 12),
          for (final s in d.steps) _StepRow(step: s),
          const SizedBox(height: 12),
          ClipRRect(
            borderRadius: BorderRadius.circular(UiTokens.of(context).pill),
            child: Container(
              height: 6,
              color: c.surfaceSunk,
              alignment: Alignment.centerLeft,
              child: AnimatedFractionallySizedBox(
                duration: UiTokens.of(context).slow,
                curve: UiTokens.of(context).ease,
                widthFactor: d.progress.clamp(0.03, 1.0),
                child: Container(
                  color: state == JobState.error
                      ? c.error
                      : (running ? c.accent : c.done),
                ),
              ),
            ),
          ),
          if (state == JobState.error && d.errorText != null) ...[
            const SizedBox(height: 12),
            CardError(text: d.errorText!, onRetry: onRetry),
          ],
          if ((running && onCancel != null) || onOpenArtifact != null) ...[
            const SizedBox(height: 12),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                if (onOpenArtifact != null)
                  UiMixButton.preset(
                    key: const ValueKey('job-card-open-artifact'),
                    label: openArtifactLabel,
                    iconData: LucideIcons.externalLink,
                    style: UiMixButtonStyle.secondary,
                    compact: true,
                    onPressed: onOpenArtifact,
                  ),
                if (running && onCancel != null)
                  UiMixButton.preset(
                    label: 'Stop',
                    style: UiMixButtonStyle.secondary,
                    compact: true,
                    onPressed: onCancel,
                  ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

class _StepRow extends StatelessWidget {
  const _StepRow({required this.step});

  final JobStep step;

  @override
  Widget build(BuildContext context) {
    final c = UiColors.of(context);
    final s = step;
    final icon = switch (s.status) {
      StepStatus.done => Container(
        width: 18,
        height: 18,
        decoration: BoxDecoration(color: c.doneBg, shape: BoxShape.circle),
        child: Icon(LucideIcons.check, size: 11, color: c.done),
      ),
      StepStatus.running => SizedBox(
        width: 18,
        height: 18,
        child: Center(child: WorkingSpinner(size: 15, color: c.textStrong)),
      ),
      StepStatus.failed => Container(
        width: 18,
        height: 18,
        decoration: BoxDecoration(color: c.errorBg, shape: BoxShape.circle),
        child: Icon(LucideIcons.triangleAlert, size: 11, color: c.error),
      ),
      StepStatus.waiting => Container(
        width: 18,
        height: 18,
        decoration: BoxDecoration(
          shape: BoxShape.circle,
          border: Border.all(color: c.borderInput, width: 1.5),
        ),
      ),
    };
    final color = switch (s.status) {
      StepStatus.waiting => c.textSubtle,
      StepStatus.failed => c.error,
      StepStatus.running => c.textStrong,
      StepStatus.done => c.textBody,
    };
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          icon,
          const SizedBox(width: 10),
          Expanded(
            child: Text.rich(
              TextSpan(
                children: [
                  TextSpan(
                    text: s.label,
                    style: UiType.textStyle(
                      13,
                      weight: s.status == StepStatus.running
                          ? FontWeight.w600
                          : FontWeight.w500,
                      color: color,
                    ),
                  ),
                  if (s.detail != null)
                    TextSpan(
                      text: '  ${s.detail}',
                      style: UiType.textStyle(
                        13,
                        color: c.textMuted,
                        tnum: true,
                      ),
                    ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}
