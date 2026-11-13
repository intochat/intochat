import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../design/cards/job_card.dart';
import 'node_common.dart';

Widget renderJobCard(JobCardNode node, NodeContext ctx) => JobCard(
  source: cardSource('Job'),
  data: JobData(
    title: node.title,
    progress: node.progress > 1 ? node.progress / 100 : node.progress,
    resultText: node.resultText,
    steps: [
      for (final step in node.steps)
        JobStep(
          step.label,
          detail: step.detail,
          status: switch (step.status.toLowerCase()) {
            'running' => StepStatus.running,
            'done' => StepStatus.done,
            'failed' || 'error' => StepStatus.failed,
            _ => StepStatus.waiting,
          },
        ),
    ],
  ),
  state: switch (node.status.toLowerCase()) {
    'done' => JobState.done,
    'cancelled' => JobState.cancelled,
    'error' || 'failed' => JobState.error,
    _ => JobState.running,
  },
  onCancel: pressOf(node.cancel, ctx),
);
