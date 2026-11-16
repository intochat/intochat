import 'package:flutter/material.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import 'node_context.dart';
import '../design/cards/job_card.dart';
import '../theme/wire_node_values.dart';
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
          status: parseJobStepStatus(step.status),
        ),
    ],
  ),
  state: parseJobState(node.status),
  onCancel: pressOf(node.cancel, ctx),
);
