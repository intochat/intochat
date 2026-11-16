import 'package:flutter/painting.dart';
import 'package:intochat_os_flutter_shell/intochat_os_flutter.dart';

import '../components/kit/ui_kit_button.dart';
import '../design/app_tile.dart';
import '../design/cards/approval_card.dart';
import '../design/cards/chart_card.dart';
import '../design/cards/draft_card.dart';
import '../design/cards/job_card.dart';
import '../design/cards/secret_card.dart';
import '../design/controls.dart' show TagTone;

/// Semantic node tone resolved across `Badge`, `TagPill`, `Text`, and `InfoBar`.
enum UiNodeTone { neutral, accent, done, working, error }

enum WireMessageRole { user, assistant, other }

enum WireMessageStatus { sending, running, failed, interrupted, other }

enum WireActivityStatus { running, failed, done }

enum TextToneVariant { muted, strong, semantic }

enum DiffChangeVariant { added, deleted, renamed, modified }

enum SurfaceVariantKind {
  toolbar,
  breadcrumb,
  image,
  code,
  expander,
  scroll,
  studioFrame,
  recessed,
  elevated,
  acrylic,
  generic,
}

final class ParsedSurfaceVariant {
  const ParsedSurfaceVariant(
    this.kind, {
    this.boxFit = BoxFit.contain,
    this.language = 'code',
    this.initiallyOpen = false,
    this.targetWidth,
  });

  final SurfaceVariantKind kind;
  final BoxFit boxFit;
  final String language;
  final bool initiallyOpen;
  final double? targetWidth;
}

UiNodeTone parseUiNodeTone(String tone) => switch (tone.trim().toLowerCase()) {
  'accent' ||
  'primary' ||
  'info' ||
  'needs-you' ||
  'needs_you' ||
  'needsyou' => UiNodeTone.accent,
  'done' ||
  'ok' ||
  'success' ||
  'positive' ||
  'connected' ||
  'saved' => UiNodeTone.done,
  'working' ||
  'warn' ||
  'warning' ||
  'pending' ||
  'running' ||
  'syncing' ||
  'saving' => UiNodeTone.working,
  'error' ||
  'danger' ||
  'critical' ||
  'failed' ||
  'destructive' => UiNodeTone.error,
  _ => UiNodeTone.neutral,
};

(TextToneVariant, UiNodeTone) parseTextTone(String raw) {
  final lower = raw.trim().toLowerCase();
  return switch (lower) {
    'muted' ||
    'subtle' ||
    'ghost' => (TextToneVariant.muted, UiNodeTone.neutral),
    'strong' => (TextToneVariant.strong, UiNodeTone.neutral),
    _ => (TextToneVariant.semantic, parseUiNodeTone(lower)),
  };
}

WireMessageRole parseMessageRole(String role) =>
    switch (role.trim().toLowerCase()) {
      'user' => WireMessageRole.user,
      'assistant' => WireMessageRole.assistant,
      _ => WireMessageRole.other,
    };

WireMessageStatus parseMessageStatus(String status) =>
    switch (status.trim().toLowerCase()) {
      'sending' => WireMessageStatus.sending,
      'running' => WireMessageStatus.running,
      'failed' => WireMessageStatus.failed,
      'interrupted' => WireMessageStatus.interrupted,
      _ => WireMessageStatus.other,
    };

WireActivityStatus parseActivityStatus(String status) =>
    switch (status.trim().toLowerCase()) {
      'running' || 'working' => WireActivityStatus.running,
      'failed' || 'error' => WireActivityStatus.failed,
      _ => WireActivityStatus.done,
    };

ApprovalState parseApprovalState(String status) =>
    switch (status.trim().toLowerCase()) {
      'approved' => ApprovalState.approved,
      'rejected' || 'denied' => ApprovalState.rejected,
      'error' => ApprovalState.error,
      _ => ApprovalState.pending,
    };

UiKitButtonStyle parseButtonStyle(String style, String tone) => switch (style
    .trim()
    .toLowerCase()) {
  'primary' => UiKitButtonStyle.primary,
  'ghost' => UiKitButtonStyle.ghost,
  'danger' => UiKitButtonStyle.danger,
  'link' => UiKitButtonStyle.link,
  _ when parseUiNodeTone(tone) == UiNodeTone.accent => UiKitButtonStyle.primary,
  _ => UiKitButtonStyle.secondary,
};

ChartKind parseChartKind(String kind) => switch (kind.trim().toLowerCase()) {
  'line' => ChartKind.line,
  'area' => ChartKind.area,
  'pie' => ChartKind.pie,
  _ => ChartKind.columns,
};

(String, TagTone) parseDiffChangeBadge(String changeKind) =>
    switch (changeKind.trim().toLowerCase()) {
      'added' || 'add' || 'a' || 'new' => ('A', TagTone.done),
      'deleted' || 'delete' || 'removed' || 'd' => ('D', TagTone.error),
      'renamed' || 'rename' || 'r' => ('R', TagTone.accent),
      _ => ('M', TagTone.working),
    };

final RegExp _hunkHeaderNumbers = RegExp(
  r'@@\s*-(\d+)(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s*@@',
);

(int oldLine, int newLine) parseDiffHunkStart(String header) {
  final match = _hunkHeaderNumbers.firstMatch(header);
  if (match == null) return (1, 1);
  return (
    int.tryParse(match.group(1) ?? '') ?? 1,
    int.tryParse(match.group(2) ?? '') ?? 1,
  );
}

DraftChannel parseDraftChannel(String channel) =>
    switch (channel.trim().toLowerCase()) {
      'slack' => DraftChannel.slack,
      'sms' => DraftChannel.sms,
      _ => DraftChannel.email,
    };

DraftState parseDraftState(String status) =>
    switch (status.trim().toLowerCase()) {
      'sending' => DraftState.sending,
      'sent' => DraftState.sent,
      'scheduled' => DraftState.scheduled,
      'discarded' => DraftState.discarded,
      'error' => DraftState.error,
      _ => DraftState.pending,
    };

StepStatus parseJobStepStatus(String status) =>
    switch (status.trim().toLowerCase()) {
      'running' => StepStatus.running,
      'done' => StepStatus.done,
      'failed' || 'error' => StepStatus.failed,
      _ => StepStatus.waiting,
    };

JobState parseJobState(String status) => switch (status.trim().toLowerCase()) {
  'done' => JobState.done,
  'cancelled' => JobState.cancelled,
  'error' || 'failed' => JobState.error,
  _ => JobState.running,
};

SecretState parseSecretState(String status) =>
    switch (status.trim().toLowerCase()) {
      'entering' => SecretState.entering,
      'saved' => SecretState.saved,
      'skipped' => SecretState.skipped,
      _ => SecretState.pending,
    };

bool isSlotNeuronRefKind(String refKind) =>
    switch (refKind.trim().toLowerCase()) {
      'view' || 'slot' || 'app' || 'unit' || '' => true,
      _ => false,
    };

bool isAppNeuronRefKind(String refKind) =>
    refKind.trim().toLowerCase() == 'app';

bool isDigitalBrainSourceName(String name) => name.trim() == 'DigitalBrain';

ParsedSurfaceVariant parseSurfaceVariant(String surfaceKind) {
  final kind = surfaceKind.trim().toLowerCase();
  switch (kind) {
    case 'toolbar':
      return const ParsedSurfaceVariant(SurfaceVariantKind.toolbar);
    case 'breadcrumb':
      return const ParsedSurfaceVariant(SurfaceVariantKind.breadcrumb);
    case 'scroll':
      return const ParsedSurfaceVariant(SurfaceVariantKind.scroll);
    case 'recessed':
      return const ParsedSurfaceVariant(SurfaceVariantKind.recessed);
    case 'elevated':
      return const ParsedSurfaceVariant(SurfaceVariantKind.elevated);
    case 'acrylic':
      return const ParsedSurfaceVariant(SurfaceVariantKind.acrylic);
  }
  if (kind == 'image' || kind.startsWith('image:')) {
    final idx = kind.indexOf(':');
    final fitMode = idx >= 0 ? kind.substring(idx + 1).trim() : 'contain';
    return ParsedSurfaceVariant(
      SurfaceVariantKind.image,
      boxFit: fitMode == 'cover' ? BoxFit.cover : BoxFit.contain,
    );
  }
  if (kind == 'code' || kind.startsWith('code:')) {
    final idx = kind.indexOf(':');
    final lang = idx >= 0 ? kind.substring(idx + 1).trim() : 'code';
    return ParsedSurfaceVariant(
      SurfaceVariantKind.code,
      language: lang.isEmpty ? 'code' : lang,
    );
  }
  if (kind == 'expander' || kind.startsWith('expander:')) {
    return ParsedSurfaceVariant(
      SurfaceVariantKind.expander,
      initiallyOpen: kind.endsWith(':open'),
    );
  }
  if (kind == 'studio-frame' || kind.startsWith('studio-frame:')) {
    final idx = kind.indexOf(':');
    final widthSpec = idx >= 0 ? kind.substring(idx + 1).trim() : '';
    return ParsedSurfaceVariant(
      SurfaceVariantKind.studioFrame,
      targetWidth: double.tryParse(widthSpec),
    );
  }
  return const ParsedSurfaceVariant(SurfaceVariantKind.generic);
}

List<String> splitSourceLines(String source) =>
    source.replaceAll('\r\n', '\n').split('\n');

final RegExp _numericPattern = RegExp(
  r'^\s*[$€£¥+\-−]?\s*\d[\d,.\s]*[%KMBkmb]?\s*$',
);

bool isNumericTableColumn(List<List<String>> rows, int columnIndex) {
  var nonEmpty = 0;
  for (final row in rows) {
    if (columnIndex >= row.length) continue;
    final cell = row[columnIndex].trim();
    if (cell.isEmpty || cell == '—' || cell == '-') continue;
    nonEmpty++;
    if (!_numericPattern.hasMatch(cell)) return false;
  }
  return nonEmpty > 0;
}

String humanizeFieldLabel(String rawName) {
  final tail = rawName.contains('/')
      ? rawName.substring(rawName.lastIndexOf('/') + 1)
      : rawName;
  if (tail.isEmpty) return '';
  if (tail.contains(' ') ||
      (tail[0].toUpperCase() == tail[0] && tail[0].toLowerCase() != tail[0])) {
    return tail;
  }
  final spaced = tail
      .replaceAllMapped(RegExp(r'([a-z0-9])([A-Z])'), (m) => '${m[1]} ${m[2]}')
      .replaceAll(RegExp(r'[-_]+'), ' ')
      .trim();
  if (spaced.isEmpty) return tail;
  return '${spaced[0].toUpperCase()}${spaced.substring(1)}';
}

String inferAppIdFromViewPath(String view) {
  final trimmed = view.trim().toLowerCase();
  final appsIdx = trimmed.indexOf('/apps/');
  if (appsIdx >= 0) {
    final rest = trimmed.substring(appsIdx + 6);
    final slash = rest.indexOf('/');
    final seg = (slash >= 0 ? rest.substring(0, slash) : rest).trim();
    if (seg.isNotEmpty) {
      final norm = IcAppIcon.normalizeAppId(seg);
      if (IcAppIcon.builtInSpecs.containsKey(norm)) return norm;
    }
  }
  if (trimmed.startsWith('apps/')) {
    final rest = trimmed.substring(5);
    final slash = rest.indexOf('/');
    final seg = (slash >= 0 ? rest.substring(0, slash) : rest).trim();
    if (seg.isNotEmpty) {
      final norm = IcAppIcon.normalizeAppId(seg);
      if (IcAppIcon.builtInSpecs.containsKey(norm)) return norm;
    }
  }
  return 'ino';
}

bool isStaleConflictError(int statusCode, String status) =>
    statusCode == 409 && status.trim().toLowerCase() == 'stale';

IntentKind? parseIntentKind(Object? value) {
  final name = '$value'.toLowerCase();
  for (final kind in IntentKind.values) {
    if (kind.name.toLowerCase() == name) return kind;
  }
  return null;
}

final RegExp _intentAppKeyPattern = RegExp(r'^[a-z][a-z0-9-]*$');

bool isAllowedOpenWindowUri(Uri? uri) =>
    uri != null &&
    uri.scheme == 'os' &&
    uri.host == 'window' &&
    uri.path == '/open' &&
    uri.userInfo.isEmpty &&
    !uri.hasPort &&
    !uri.hasFragment;

bool isValidIntentAppKey(String key) => _intentAppKeyPattern.hasMatch(key);

bool isTapBrainInput(String kind) => kind == 'tap';
bool isWildcardEdgeOrigin(String fromId) => fromId == '*';
bool isDraftBrainNode(String kind) => kind == 'draft';

String stripSignalAliasHash(String? signalLabel) {
  final raw = signalLabel ?? '';
  final hash = raw.indexOf('#');
  return hash < 0 ? raw : raw.substring(0, hash);
}

({int index, int seq, String alias, String traceId})? parseBrainReplayLabel(
  String? label,
  String prefix,
) {
  if (label == null || !label.startsWith(prefix)) return null;
  final parts = label.split(':');
  if (parts.length < 5) return null;
  final index = int.tryParse(parts[1]);
  final seq = int.tryParse(parts[2]);
  if (index == null || seq == null) return null;
  return (
    index: index,
    seq: seq,
    alias: parts[3],
    traceId: parts.sublist(4).join(':'),
  );
}
