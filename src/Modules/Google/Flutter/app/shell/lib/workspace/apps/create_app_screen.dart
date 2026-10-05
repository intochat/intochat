import 'package:flutter/material.dart';

import 'app_spec_view.dart';
import 'apps_screen.dart';

// The Creator: describe the app and it exists — installed, running, in this brain — then
// iterate: the Builder implements it in place, scenarios run against a committed snapshot,
// and Save checkpoints it. A red app is a state to revisit, never a refusal.
class CreateAppScreen extends StatefulWidget {
  const CreateAppScreen({
    super.key,
    required this.request,
    required this.workspaceId,
    this.app,
  });

  final AppsRequest request;
  final String workspaceId;
  // "owner/name" of an existing live app to reopen; null starts a new one.
  final String? app;

  @override
  State<CreateAppScreen> createState() => _CreateAppScreenState();
}

class _CreateAppScreenState extends State<CreateAppScreen> {
  final _describe = TextEditingController();
  final _instruct = TextEditingController();
  String? _app;
  Map<String, dynamic> _content = {};
  bool _busy = false;
  String? _activity;
  String? _error;
  String? _notice;

  static Map<String, dynamic> _map(dynamic value) =>
      value is Map ? Map<String, dynamic>.from(value) : <String, dynamic>{};

  String get _brainPath => '/brains/${Uri.encodeComponent(widget.workspaceId)}';
  String get _creatorPath =>
      '$_brainPath/packages/${_app!.split('/').map(Uri.encodeComponent).join('/')}/creator';

  @override
  void initState() {
    super.initState();
    _app = widget.app;
    if (_app != null) {
      _perform(
        'Opening the app…',
        () async => _content = _map(
          await widget.request('GET', '$_creatorPath/content'),
        ),
      );
    }
  }

  @override
  void dispose() {
    _describe.dispose();
    _instruct.dispose();
    super.dispose();
  }

  Future<void> _perform(String activity, Future<void> Function() call) async {
    if (_busy) return;
    setState(() {
      _busy = true;
      _activity = activity;
      _error = null;
      _notice = null;
    });
    try {
      await call();
    } catch (error) {
      if (mounted) setState(() => _error = error.toString());
    } finally {
      if (mounted) {
        setState(() {
          _busy = false;
          _activity = null;
        });
      }
    }
  }

  Future<void> _start() => _perform(
    'The Author is shaping the app. It will be alive in a moment…',
    () async {
      final started = _map(
        await widget.request('POST', '$_brainPath/creator/apps', {
          'text': _describe.text,
        }),
      );
      _app = '${started['owner']}/${started['name']}';
      _content = _map(started['content']);
    },
  );

  Future<void> _implement() => _perform(
    'The Builder is writing the behaviors. This can take a few minutes…',
    () async {
      _content = _map(
        await widget.request('POST', '$_creatorPath/implement', {
          'text': _instruct.text,
        }),
      );
      _instruct.clear();
    },
  );

  Future<void> _run() => _perform('Running the scenarios…', () async {
    await widget.request('POST', '$_creatorPath/run', <String, dynamic>{});
    _content = _map(await widget.request('GET', '$_creatorPath/content'));
  });

  Future<void> _save() => _perform('Saving…', () async {
    final saved = _map(
      await widget.request('POST', '$_creatorPath/save', <String, dynamic>{}),
    );
    _notice = 'Saved as $_app@${saved['revision']}.';
  });

  @override
  Widget build(BuildContext context) {
    final manifest = _map(_content['manifest']);
    final files = _map(_content['files']);
    final verdicts = _content['verdicts'] is List
        ? (_content['verdicts'] as List).map(_map).toList()
        : <Map<String, dynamic>>[];
    final errors = _map(_content['behaviorErrors']);
    final spec = '${files['app.spec.md'] ?? ''}';
    return Scaffold(
      appBar: AppBar(title: Text(_app == null ? 'Create an app' : 'Creator · $_app')),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          if (_busy) ...[
            const LinearProgressIndicator(),
            if (_activity != null)
              Padding(
                padding: const EdgeInsets.symmetric(vertical: 8),
                child: Text(_activity!),
              ),
          ],
          if (_error != null)
            Text(
              _error!,
              key: const ValueKey('create-error'),
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          if (_notice != null) Text(_notice!, key: const ValueKey('create-notice')),
          if (_app == null) ...[
            TextField(
              key: const ValueKey('describe-app'),
              controller: _describe,
              minLines: 2,
              maxLines: 6,
              decoration: const InputDecoration(
                labelText: 'What should the app do?',
                border: OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 8),
            Align(
              alignment: Alignment.centerLeft,
              child: FilledButton(
                key: const ValueKey('draft-app'),
                onPressed: _busy ? null : _start,
                child: const Text('Create it'),
              ),
            ),
          ] else ...[
            Text(
              '${manifest['title'] ?? _app} · ${manifest['runtimeName'] ?? manifest['runtime'] ?? ''} app',
              style: Theme.of(context).textTheme.titleLarge,
            ),
            if ('${manifest['description'] ?? ''}'.isNotEmpty)
              Text('${manifest['description']}'),
            const SizedBox(height: 8),
            if (spec.isNotEmpty)
              AppSpecView(
                spec: spec,
                run: verdicts.isEmpty ? null : {'scenarios': verdicts, 'exitCode': null},
                current: verdicts.isNotEmpty &&
                    verdicts.every((verdict) => verdict['passed'] == true),
              ),
            if (errors.isNotEmpty) ...[
              const Divider(height: 24),
              for (final entry in errors.entries)
                Text(
                  '${entry.key}: ${entry.value}',
                  key: ValueKey('behavior-error-${entry.key}'),
                  style: TextStyle(color: Theme.of(context).colorScheme.error),
                ),
            ],
            const Divider(height: 24),
            TextField(
              key: const ValueKey('revise-app'),
              controller: _instruct,
              decoration: const InputDecoration(
                labelText: 'Ask the Builder for a change (optional)',
              ),
            ),
            const SizedBox(height: 8),
            Wrap(
              spacing: 8,
              children: [
                FilledButton(
                  key: const ValueKey('build-app'),
                  onPressed: _busy ? null : _implement,
                  child: const Text('Implement'),
                ),
                FilledButton.tonal(
                  key: const ValueKey('run-scenarios'),
                  onPressed: _busy ? null : _run,
                  child: const Text('Run scenarios'),
                ),
                OutlinedButton(
                  key: const ValueKey('save-app'),
                  onPressed: _busy ? null : _save,
                  child: const Text('Save'),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}
