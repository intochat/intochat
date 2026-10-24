import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

import 'apps_request.dart';

/// What the shell shows after an app activation: a toast (empty for none) and, after an install, the app to open.
final class AppActivationResult {
  const AppActivationResult(this.toast, this.tone, [this.open]);
  final String toast;
  final UiTone tone;
  final String? open;
}

const _failed = 'That app could not be installed.';

/// Runs a `{kind: install|update|remove}` activation over the person's own session. The install request and the
/// update consent are passed through exactly as the Apps app sent them; nothing is computed here. A 409 asking
/// for consent shows nothing: the Apps app renders the requirements panel from its own signal.
Future<AppActivationResult> runAppActivation(
  Map<String, dynamic> action,
  AppsRequest request,
) async {
  final brain = Uri.encodeComponent(action['brainId'] as String);
  final apps = '/brains/$brain/apps';
  final name = action['name'] as String? ?? '';
  final app = '$apps/${Uri.encodeComponent(name)}';
  String title(String fallback) =>
      action['title'] is String ? action['title'] as String : fallback;
  try {
    switch (action['kind']) {
      case 'install':
        final answer = await request(
          'POST',
          '$apps/install',
          Map<String, dynamic>.from(action['request'] as Map),
        );
        final installed = answer is Map ? answer['installedName'] : null;
        if (installed is! String) {
          return const AppActivationResult(_failed, UiTone.error);
        }
        return AppActivationResult(
          'Installed ${title(installed)}.',
          UiTone.done,
          installed,
        );
      case 'update':
        await request('POST', '$app/update', {
          if (action['consent'] case final String consent) 'consent': consent,
        });
        return AppActivationResult('Updated ${title(name)}.', UiTone.done);
      case 'remove':
        await request('DELETE', app);
        return AppActivationResult('Removed ${title(name)}.', UiTone.done);
    }
  } on UiRequestException catch (error) {
    if (error.statusCode == 409 && error.status == 'needsConsent') {
      return const AppActivationResult('', UiTone.neutral);
    }
    // Only a refusal's fixed server copy is shown; other failures never surface their text.
    if (error.statusCode == 403 && error.error != null) {
      return AppActivationResult(error.error!, UiTone.error);
    }
  } catch (_) {
    // Fixed copy below.
  }
  return const AppActivationResult(_failed, UiTone.error);
}

/// An app's configuration view is the Apps app's detail for that installed app.
Map<String, dynamic> configurationActivation(Map<String, dynamic> action) => {
  if (action['brainId'] case final String brain) 'brainId': brain,
  'kind': 'workspace',
  'application': 'apps',
  'operation': 'open',
  'input': 'installed:${action['application']}',
};
