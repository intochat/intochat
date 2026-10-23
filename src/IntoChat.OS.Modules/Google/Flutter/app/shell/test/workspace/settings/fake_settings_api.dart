import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:intochat_os_flutter_shell/workspace/settings/settings_api.dart';

/// In-memory [SettingsApi]: a provider's models are available once its key is set.
base class FakeSettingsApi implements SettingsApi {
  FakeSettingsApi({
    Map<String, String>? secrets,
    Map<String, String>? variables,
  }) : secrets = secrets ?? {},
       variables = variables ?? {};
  final Map<String, String> secrets;
  final Map<String, String> variables;
  final puts = <(String, EntryKind, String)>[];
  final removed = <String>[];
  var modelLoads = 0;
  var notificationPrefs = <String, bool>{'agent-finished': true};

  @override
  Future<List<EntrySummary>> listEntries() async => [
    for (final e in variables.entries)
      EntrySummary(name: e.key, kind: EntryKind.variable, value: e.value),
    for (final e in secrets.entries)
      EntrySummary(
        name: e.key,
        kind: EntryKind.secret,
        tail: e.value.length >= 8
            ? e.value.substring(e.value.length - 4)
            : null,
      ),
  ];

  @override
  Future<void> putEntry(String name, EntryKind kind, String value) async {
    puts.add((name, kind, value));
    (kind == EntryKind.secret ? secrets : variables)[name] = value;
  }

  @override
  Future<void> removeEntry(String name) async {
    removed.add(name);
    secrets.remove(name);
    variables.remove(name);
  }

  @override
  Future<ChatModelCatalog> models() async {
    modelLoads++;
    final anthropic = secrets.containsKey('provider:anthropic');
    return ChatModelCatalog(
      automatic: const ChatModelOption(label: 'Automatic'),
      models: [
        const ChatModelOption(
          id: 'preset:IGpt54Nano',
          label: 'gpt-5.4-nano',
          provider: 'OpenAI',
          capabilities: ['Tools'],
        ),
        ChatModelOption(
          id: 'preset:IHaiku45',
          label: 'claude-haiku-4-5',
          provider: 'Anthropic',
          available: anthropic,
          capabilities: const ['Tools'],
        ),
      ],
    );
  }

  @override
  Future<Map<String, bool>> notifications() async => {...notificationPrefs};

  @override
  Future<void> putNotifications(Map<String, bool> preferences) async =>
      notificationPrefs = {...notificationPrefs, ...preferences};
}
