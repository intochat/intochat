import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart' show UiTheme;

String _text(dynamic value, String fallback) =>
    value is String ? value : fallback;

class WorkspacePreferences {
  String? defaultModelProfile;
  String displayName = '';
  String role = '';
  String theme = 'midnight';
  bool reducedMotion = false;
  bool compactDensity = false;
  Map<String, dynamic> toJson() => {
    'defaultModelProfile': defaultModelProfile,
    'displayName': displayName,
    'role': role,
    'theme': theme,
    'reducedMotion': reducedMotion,
    'compactDensity': compactDensity,
  };
  WorkspacePreferences();
  factory WorkspacePreferences.fromJson(Map<String, dynamic> json) =>
      WorkspacePreferences()
        ..defaultModelProfile = json['defaultModelProfile'] as String?
        ..displayName = _text(json['displayName'], '')
        ..role = _text(json['role'], '')
        ..theme = UiTheme.paletteName(_text(json['theme'], 'midnight'))
        ..reducedMotion = json['reducedMotion'] == true
        ..compactDensity = json['compactDensity'] == true;
}

/// Device preferences only. Workspace navigation and application data live in neurons.
class WorkspaceStore extends ChangeNotifier {
  WorkspaceStore({this.readPreferences, this.writePreferences});
  final Future<String?> Function()? readPreferences;
  final Future<void> Function(String)? writePreferences;
  WorkspacePreferences settings = WorkspacePreferences();
  bool loaded = false;
  String? persistenceError;
  bool _disposed = false;
  Future<void> _writes = Future.value();
  Future<void> load() async {
    if (loaded) return;
    try {
      final raw = await readPreferences?.call();
      if (raw != null) {
        settings = WorkspacePreferences.fromJson(
          Map<String, dynamic>.from(jsonDecode(raw) as Map),
        );
      }
      persistenceError = null;
    } catch (_) {
      persistenceError = 'Preferences could not be loaded.';
    }
    loaded = true;
    if (!_disposed) notifyListeners();
  }

  Future<void> save() {
    final value = jsonEncode(settings.toJson());
    _writes = _writes.then((_) async {
      try {
        await writePreferences?.call(value);
        persistenceError = null;
      } catch (_) {
        persistenceError = 'Preferences could not be saved.';
      }
      if (!_disposed) notifyListeners();
    });
    if (!_disposed) notifyListeners();
    return _writes;
  }

  Future<void> flush() => _writes;
  @override
  void dispose() {
    _disposed = true;
    super.dispose();
  }
}
