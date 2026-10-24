import 'package:intochat_os_flutter/intochat_os_flutter.dart';

/// Wire value of the C# `EntryKind` (camel case).
enum EntryKind {
  variable,
  secret;

  static EntryKind parse(Object? value) =>
      value == 'secret' ? EntryKind.secret : EntryKind.variable;
}

/// One row of `GET /brains/{brainId}/entries`. A secret never carries its
/// value; [tail] is its last four characters, and only for long secrets.
final class EntrySummary {
  const EntrySummary({
    required this.name,
    required this.kind,
    this.isSet = true,
    this.value,
    this.tail,
    this.updatedAt,
  });
  final String name;
  final EntryKind kind;
  final bool isSet;
  final String? value, tail;
  final DateTime? updatedAt;

  factory EntrySummary.fromJson(Map<String, dynamic> json) => EntrySummary(
    name: json['name'] as String,
    kind: EntryKind.parse(json['kind']),
    isSet: json['isSet'] != false,
    value: json['value'] as String?,
    tail: json['tail'] as String?,
    updatedAt: DateTime.tryParse(json['updatedAt'] as String? ?? ''),
  );
}

/// API keys are secret entries named `provider:<id>`.
const providerEntryPrefix = 'provider:';

/// The key-based providers, in display order.
const keyProviders = <(String, String)>[
  ('openai', 'OpenAI'),
  ('anthropic', 'Anthropic'),
  ('google', 'Google'),
  ('xai', 'xAI'),
  ('openrouter', 'OpenRouter'),
];

/// Thrown when the signed-in user is not the brain's owner (HTTP 403).
final class SettingsForbidden implements Exception {
  const SettingsForbidden();
  @override
  String toString() => 'Only the brain owner can change these settings.';
}

/// The workspace preferences: the app a new tab opens, and the apps to choose from.
final class NewTabPreferences {
  const NewTabPreferences({required this.newTabApp, required this.choices});
  final String newTabApp;
  final List<({String name, String title})> choices;

  factory NewTabPreferences.fromJson(Map<String, dynamic> json) =>
      NewTabPreferences(
        newTabApp: json['newTabApp'] as String? ?? 'ino',
        choices: [
          for (final row in json['choices'] as List? ?? const [])
            (
              name: (row as Map)['name'] as String,
              title: row['title'] as String,
            ),
        ],
      );
}

/// What the Settings tabs read and write for one brain.
abstract interface class SettingsApi {
  Future<List<EntrySummary>> listEntries();
  Future<void> putEntry(String name, EntryKind kind, String value);
  Future<void> removeEntry(String name);
  Future<ChatModelCatalog> models();
  Future<Map<String, bool>> notifications();
  Future<void> putNotifications(Map<String, bool> preferences);
  Future<NewTabPreferences> workspacePreferences();
  Future<NewTabPreferences> putNewTabApp(String name);
}

/// [SettingsApi] over the signed-in client's session.
final class ClientSettingsApi implements SettingsApi {
  ClientSettingsApi(this.client, this.brainId);
  final IntoChatOSUiClient client;
  final String brainId;

  String get _brain => '/brains/${Uri.encodeComponent(brainId)}';

  Future<dynamic> _call(String method, String path, [Object? body]) async {
    try {
      return await client.jsonRequest(method, '$_brain$path', body);
    } on UiRequestException catch (error) {
      if (error.statusCode == 403) throw const SettingsForbidden();
      rethrow;
    }
  }

  @override
  Future<List<EntrySummary>> listEntries() async => [
    for (final row in (await _call('GET', '/entries') as List? ?? const []))
      EntrySummary.fromJson(Map<String, dynamic>.from(row as Map)),
  ];

  @override
  Future<void> putEntry(String name, EntryKind kind, String value) => _call(
    'PUT',
    '/entries/${Uri.encodeComponent(name)}',
    {'kind': kind.name, 'value': value},
  );

  @override
  Future<void> removeEntry(String name) =>
      _call('DELETE', '/entries/${Uri.encodeComponent(name)}');

  @override
  Future<ChatModelCatalog> models() => client.readModelCatalog();

  @override
  Future<Map<String, bool>> notifications() async {
    final body = await _call('GET', '/preferences/notifications');
    return {
      if (body is Map)
        for (final e in body.entries)
          if (e.value is bool) e.key as String: e.value as bool,
    };
  }

  @override
  Future<void> putNotifications(Map<String, bool> preferences) =>
      _call('PUT', '/preferences/notifications', preferences);

  @override
  Future<NewTabPreferences> workspacePreferences() async =>
      NewTabPreferences.fromJson(
        Map<String, dynamic>.from(
          await _call('GET', '/preferences/workspace') as Map,
        ),
      );

  @override
  Future<NewTabPreferences> putNewTabApp(String name) async =>
      NewTabPreferences.fromJson(
        Map<String, dynamic>.from(
          await _call('PUT', '/preferences/workspace', {'newTabApp': name})
              as Map,
        ),
      );
}
