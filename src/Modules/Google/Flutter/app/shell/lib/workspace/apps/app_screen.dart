import 'dart:async';

import 'package:flutter/material.dart';

import 'apps_request.dart';

// The Creator: describe the app and it exists — Drafted, in this brain — then iterate.
// Implement and Verify answer immediately and run in the background; the screen follows
// the app's status and activity feed. A red app is a state to revisit, never a refusal.
class AppScreen extends StatefulWidget {
  const AppScreen({
    super.key,
    required this.workspaceId,
    required this.request,
    this.name,
    this.pollInterval = const Duration(seconds: 1),
  });

  final String workspaceId;
  final AppsRequest request;
  final String? name;
  final Duration pollInterval;

  @override
  State<AppScreen> createState() => _AppScreenState();
}

class _AppScreenState extends State<AppScreen> {
  final _describe = TextEditingController();
  Map<String, dynamic>? _app;
  String? _name;
  bool _busy = false;
  Timer? _poll;
  String? _error;

  String get _appsPath =>
      '/brains/${Uri.encodeComponent(widget.workspaceId)}/apps';
  String get _appPath => '$_appsPath/${Uri.encodeComponent(_name!)}';

  static Map<String, dynamic> _map(dynamic value) =>
      value is Map ? Map<String, dynamic>.from(value) : <String, dynamic>{};

  @override
  void initState() {
    super.initState();
    _name = widget.name;
    if (_name != null) {
      unawaited(
        _perform('', () async => _show(await widget.request('GET', _appPath))),
      );
    }
  }

  @override
  void dispose() {
    _poll?.cancel();
    _describe.dispose();
    super.dispose();
  }

  void _show(dynamic app) {
    if (!mounted) return;
    setState(() {
      _app = _map(app);
      _name = '${_app!['name']}';
    });
    // Building finishes in the background; keep the activity feed current until it lands.
    if (_app?['status'] == 'Building') {
      _poll ??= Timer.periodic(widget.pollInterval, (_) async {
        try {
          final current = _map(await widget.request('GET', _appPath));
          if (!mounted) return;
          setState(() => _app = current);
          if (current['status'] != 'Building') {
            _poll?.cancel();
            _poll = null;
          }
        } catch (_) {}
      });
    }
  }

  Future<void> _perform(String notice, Future<void> Function() action) async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _error = notice.isEmpty ? null : notice;
    });
    try {
      await action();
      if (mounted && notice.isNotEmpty) setState(() => _error = null);
    } catch (error) {
      if (mounted) setState(() => _error = error.toString());
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _author() => _perform(
    'The Author is writing the specification…',
    () async {
      final text = _describe.text;
      _show(
        _name == null
            ? await widget.request('POST', _appsPath, {'text': text})
            : await widget.request('POST', '$_appPath/author', {'text': text}),
      );
      _describe.clear();
    },
  );

  Future<void> _implement() => _perform('', () async {
    _show(
      await widget.request('POST', '$_appPath/implement', {
        'text': _describe.text,
      }),
    );
    _describe.clear();
  });

  Future<void> _verify() => _perform('', () async {
    await widget.request('POST', '$_appPath/verify', const <String, dynamic>{});
    _show(await widget.request('GET', _appPath));
  });

  @override
  Widget build(BuildContext context) {
    final app = _app;
    final activity = app?['activity'] is List
        ? (app!['activity'] as List).map((line) => '$line').toList()
        : const <String>[];
    final verdicts = app?['verdicts'] is List
        ? (app!['verdicts'] as List).map(_map).toList()
        : const <Map<String, dynamic>>[];
    return Scaffold(
      appBar: AppBar(
        title: Text(
          app == null
              ? 'Create app'
              : '${_map(app['manifest'])['title'] ?? _name}',
        ),
      ),
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 960),
          child: ListView(
            padding: const EdgeInsets.all(16),
            children: [
              if (_busy) const LinearProgressIndicator(),
              if (_error != null)
                Padding(
                  padding: const EdgeInsets.only(bottom: 12),
                  child: Text(
                    _error!,
                    key: const ValueKey('app-error'),
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                ),
              if (app != null)
                Text(
                  'v${app['version']} · ${app['status']}',
                  key: const ValueKey('app-status'),
                  style: Theme.of(context).textTheme.titleMedium,
                ),
              TextField(
                key: const ValueKey('describe-app'),
                controller: _describe,
                minLines: 2,
                maxLines: 6,
                decoration: InputDecoration(
                  labelText: app == null
                      ? 'Describe the app you want'
                      : 'Describe a change (spec or implementation)',
                ),
              ),
              const SizedBox(height: 8),
              Wrap(
                spacing: 8,
                children: [
                  FilledButton(
                    key: const ValueKey('author-app'),
                    onPressed: _busy ? null : _author,
                    child: Text(app == null ? 'Create' : 'Author'),
                  ),
                  if (app != null) ...[
                    FilledButton(
                      key: const ValueKey('build-app'),
                      onPressed: _busy || app['status'] == 'Building'
                          ? null
                          : _implement,
                      child: const Text('Implement'),
                    ),
                    FilledButton.tonal(
                      key: const ValueKey('run-scenarios'),
                      onPressed: _busy || app['status'] == 'Building'
                          ? null
                          : _verify,
                      child: const Text('Verify'),
                    ),
                  ],
                ],
              ),
              if (activity.isNotEmpty) ...[
                const SizedBox(height: 16),
                Text('Activity', style: Theme.of(context).textTheme.titleSmall),
                for (final line in activity)
                  Text(line, key: ValueKey('activity-$line')),
              ],
              if (verdicts.isNotEmpty) ...[
                const SizedBox(height: 16),
                Text(
                  'Scenarios',
                  style: Theme.of(context).textTheme.titleSmall,
                ),
                for (final verdict in verdicts)
                  ListTile(
                    dense: true,
                    key: ValueKey('verdict-${verdict['name']}'),
                    leading: Icon(
                      verdict['passed'] == true
                          ? Icons.check_circle
                          : Icons.cancel,
                      color: verdict['passed'] == true
                          ? Colors.green
                          : Colors.red,
                    ),
                    title: Text('${verdict['name']}'),
                    subtitle: '${verdict['message'] ?? ''}'.isEmpty
                        ? null
                        : Text('${verdict['message']}'),
                  ),
              ],
              if (app != null && '${app['spec'] ?? ''}'.isNotEmpty) ...[
                const SizedBox(height: 16),
                Text(
                  'Specification',
                  style: Theme.of(context).textTheme.titleSmall,
                ),
                SelectableText(
                  '${app['spec']}',
                  key: const ValueKey('app-spec'),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
