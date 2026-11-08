part of 'neuron_view.dart';

extension _NeuronLegacyRenderers on _NeuronViewState {
  Widget _buildLegacyNeuronKind(
    BuildContext context,
    Map<String, dynamic> data,
    Map<String, dynamic> definition,
    List<Widget> children,
    List rawChildren,
    bool enabled,
  ) {
    switch (widget.kind) {
      case 'person':
        final avatar = '${definition['avatarUrl'] ?? ''}';
        return ListTile(
          leading: CircleAvatar(
            backgroundImage: avatar.isEmpty ? null : NetworkImage(avatar),
            child: avatar.isEmpty ? const Icon(Icons.person) : null,
          ),
          title: Text('${definition['displayName'] ?? ''}'),
        );
      case 'composer':
        return UiComposer(
          key: ValueKey(
            '${widget.name}:${data['context']}:${data['generation']}',
          ),
          state: data,
          enabled: enabled && widget.onAction != null,
          dispatch: (event) =>
              _dispatch({...event, 'kind': 'composer', 'name': widget.name}),
        );
      case 'chat':
        final projection = data['projection'] as Map? ?? const {};
        final unreadIds = (data['unreadMessageIds'] as List? ?? const [])
            .whereType<String>()
            .toList();
        final transcript = UiChatTranscript(
          key: ValueKey('${projection['source']}:${projection['generation']}'),
          messages: [
            for (final message
                in (projection['messages'] as List? ?? const [])
                    .whereType<Map>())
              Map<String, dynamic>.from(message),
          ],
          markdown: _markdown,
          onRetry: enabled && widget.onAction != null
              ? (text) => unawaited(
                  ChatRunLinks.resend(widget.name)?.call(text) ??
                      Future<void>.value(),
                )
              : null,
          onOpenApp: enabled ? widget.onActivate : null,
        );
        void act(Map action) => unawaited(
          _dispatch({
            'kind': 'chat',
            'name': widget.name,
            'action': action['id'],
            'context': projection['source'],
            'revision': projection['generation'],
          }).catchError((Object _) {}),
        );
        bool usable(Map action) =>
            enabled && widget.onAction != null && action['enabled'] != false;
        var actions = (projection['actions'] as List? ?? const [])
            .whereType<Map>()
            .toList();
        // A following composer shows Stop in place of Send; the chat keeps
        // only its other actions.
        final stop = actions
            .where((action) => '${action['id']}'.startsWith('stop/'))
            .firstOrNull;
        final follower = ChatRunLinks.followed(widget.name);
        _publishStop(
          follower && stop != null && usable(stop)
              ? ('${stop['id']}', () => act(stop))
              : null,
        );
        if (follower) {
          actions = [
            for (final action in actions)
              if (!identical(action, stop)) action,
          ];
        }
        return Column(
          children: [
            if (unreadIds.isNotEmpty)
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 12),
                child: Wrap(
                  spacing: 8,
                  crossAxisAlignment: WrapCrossAlignment.center,
                  children: [
                    Text('${unreadIds.length} unread'),
                    TextButton(
                      onPressed: enabled && widget.onAction != null
                          ? () => unawaited(
                              _dispatch({
                                'kind': 'chat',
                                'name': widget.name,
                                'context': projection['source'],
                                'revision': projection['generation'],
                                'read': {
                                  'version': projection['version'],
                                  'messageIds': unreadIds,
                                },
                              }).catchError((Object _) {}),
                            )
                          : null,
                      child: const Text('Mark read'),
                    ),
                  ],
                ),
              ),
            Expanded(child: transcript),
            if (actions.isNotEmpty)
              Padding(
                padding: const EdgeInsets.symmetric(
                  horizontal: 12,
                  vertical: 4,
                ),
                child: Wrap(
                  spacing: 8,
                  runSpacing: 4,
                  children: [
                    for (final action in actions)
                      UiKitButton(
                        label: '${action['label'] ?? ''}',
                        icon: '${action['id']}'.startsWith('stop/')
                            ? 'square'
                            : null,
                        compact: true,
                        style: UiKitButtonStyle.secondary,
                        onPressed: usable(action) ? () => act(action) : null,
                      ),
                  ],
                ),
              ),
          ],
        );
      case 'surface':
      case 'layout':
        final content = _layout(definition, children, rawChildren);
        final inset = (definition['padding'] as num? ?? 0).toDouble().clamp(
          0.0,
          128.0,
        );
        if (definition['surface'] == 'floating' ||
            definition['surface'] == 'panel') {
          final colors = Theme.of(context).colorScheme;
          return Container(
            padding: EdgeInsets.all(inset),
            decoration: BoxDecoration(
              color: colors.surfaceContainerLow,
              borderRadius: BorderRadius.circular(
                UiTokens.of(context).surfaceRadius,
              ),
              border: Border.all(
                color: colors.outlineVariant.withValues(alpha: .65),
              ),
              boxShadow: definition['surface'] == 'floating'
                  ? [
                      BoxShadow(
                        color: Colors.black.withValues(alpha: .16),
                        blurRadius: 20,
                        offset: const Offset(0, 6),
                      ),
                    ]
                  : null,
            ),
            child: ClipRRect(
              borderRadius: BorderRadius.circular(
                UiTokens.of(context).controlRadius,
              ),
              child: content,
            ),
          );
        }
        return Padding(padding: EdgeInsets.all(inset), child: content);
      case 'text':
        return _markdown(definition['markdown'] as String? ?? '');
      case 'button':
        if (definition['visual'] case final Map visual) {
          return UiNeuronButton(
            label: definition['label'] as String? ?? '',
            visual: visual,
            onPressed: enabled ? () => _button(definition) : null,
          );
        }
        return TextButton(
          onPressed: enabled ? () => _button(definition) : null,
          child: Text(
            definition['label'] as String? ?? '',
            maxLines: 2,
            overflow: TextOverflow.ellipsis,
          ),
        );
      case 'textfield':
        return _NeuronTextField(
          key: ValueKey(widget.name),
          name: widget.name,
          definition: definition,
          enabled: enabled && widget.onAction != null,
          dispatch: _dispatch,
        );
      case 'select':
        final options = (definition['options'] as List? ?? [])
            .whereType<Map>()
            .toList();
        return UiNeuronSelect(
          label: definition['label'] as String? ?? '',
          options: options,
          selected: definition['selected'] as String?,
          visual: definition['visual'] as Map?,
          onSelected: enabled && widget.onAction != null
              ? (value) => unawaited(
                  _dispatch({
                    'kind': 'select',
                    'name': widget.name,
                    'value': value,
                  }).catchError((Object _) {}),
                )
              : null,
        );
      case 'fileinput':
        final label = definition['label'] as String? ?? '';
        final capture = enabled && widget.onAction != null ? _file : null;
        return LayoutBuilder(
          builder: (context, constraints) => constraints.maxWidth < 120
              ? IconButton(
                  tooltip: label,
                  onPressed: capture,
                  icon: const Icon(Icons.attach_file),
                )
              : TextButton.icon(
                  onPressed: capture,
                  icon: const Icon(Icons.attach_file),
                  label: Text(label),
                ),
        );
      case 'webbrowser':
        final send = widget.onAction;
        if (send == null) {
          return Center(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Text(
                definition['title'] as String? ?? '',
                textAlign: TextAlign.center,
                style: const TextStyle(fontWeight: FontWeight.w600),
              ),
            ),
          );
        }
        return UiWebBrowser(
          part: UiWebBrowserPart.fromMetadata({
            ...definition,
            'name': widget.name,
          }),
          height: null,
          connection: '${data['connection'] ?? ''}',
          error: '${data['error'] ?? ''}',
          actualUrl: '${data['actualUrl'] ?? ''}',
          onConnect: (port, sessionId) => send({
            'kind': 'webbrowser',
            'name': widget.name,
            'action': 'connect',
            'value': jsonEncode({'port': port, 'sessionId': sessionId}),
          }),
          onDisconnect: (sessionId) => send({
            'kind': 'webbrowser',
            'name': widget.name,
            'action': 'disconnect',
            'value': jsonEncode({'sessionId': sessionId}),
          }),
          onUnavailable: (reason) => send({
            'kind': 'webbrowser',
            'name': widget.name,
            'action': 'unavailable',
            'value': reason,
          }),
        );
      case 'voiceinput':
        return widget.voiceBuilder?.call(widget.name, definition) ??
            UiVoiceInput(
              label: definition['label'] as String? ?? '',
              enabled: enabled && widget.onAction != null,
              onAudio: (audio, mimeType) => _dispatch({
                'kind': 'voiceinput',
                'name': widget.name,
                'value': base64Encode(audio),
                'mimeType': mimeType,
              }),
            );
      case 'tabs':
        final tabs = (definition['tabs'] as List? ?? [])
            .whereType<Map>()
            .toList();
        final selected = definition['selectedId'];
        final active = tabs.where((tab) => tab['id'] == selected).firstOrNull;
        return Column(
          children: [
            SizedBox(
              height: 42,
              child: ListView(
                scrollDirection: Axis.horizontal,
                children: [
                  for (final tab in tabs)
                    Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 4),
                      child: ChoiceChip(
                        label: Text(tab['title'] as String? ?? ''),
                        selected: tab['id'] == selected,
                        onSelected: !enabled
                            ? null
                            : (_) => unawaited(
                                _dispatch({
                                  'kind': 'tabs',
                                  'name': widget.name,
                                  'value': tab['id'],
                                }).catchError((Object _) {}),
                              ),
                      ),
                    ),
                ],
              ),
            ),
            if (active?['child'] is Map)
              Expanded(child: _child(active!['child'] as Map)),
          ],
        );
      case 'table':
        final columns = (definition['columns'] as List? ?? [])
            .whereType<Map>()
            .toList();
        if (columns.isEmpty) return const SizedBox.shrink();
        final rows = (definition['rows'] as List? ?? [])
            .whereType<List>()
            .toList();
        return SingleChildScrollView(
          child: SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            child: DataTable(
              columns: [
                for (final column in columns)
                  DataColumn(label: Text('${column['title'] ?? column['id']}')),
              ],
              rows: [
                for (final row in rows)
                  DataRow(
                    cells: [
                      for (var i = 0; i < columns.length; i++)
                        DataCell(Text(i < row.length ? '${row[i]}' : '')),
                    ],
                  ),
              ],
            ),
          ),
        );
      case 'chart':
        return UiChart(part: UiChartPart.fromMetadata(definition));
      case 'scene':
        return SceneView(
          sceneKey: widget.name,
          state: SceneState.fromJson({'name': widget.name, ...data}),
        );
      case 'card':
        return Container(
          width: double.infinity,
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: Theme.of(context).colorScheme.surfaceContainerLow,
            borderRadius: BorderRadius.circular(
              UiTokens.of(context).surfaceRadius,
            ),
            border: Border.all(
              color: Theme.of(context).colorScheme.outlineVariant,
            ),
          ),
          child: LayoutBuilder(
            builder: (context, constraints) {
              final content = Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                mainAxisSize: MainAxisSize.min,
                children: [
                  if ((definition['title'] as String? ?? '').isNotEmpty)
                    Text(
                      definition['title'] as String,
                      style: Theme.of(context).textTheme.titleLarge,
                    ),
                  if ((definition['title'] as String? ?? '').isNotEmpty &&
                      (definition['body'] as String? ?? '').isNotEmpty)
                    const SizedBox(height: 8),
                  if ((definition['body'] as String? ?? '').isNotEmpty)
                    _markdown(definition['body'] as String),
                  ...children,
                ],
              );
              return constraints.hasBoundedHeight
                  ? SingleChildScrollView(primary: false, child: content)
                  : content;
            },
          ),
        );
      case 'collection':
        final items = (definition['items'] as List? ?? [])
            .whereType<Map>()
            .map((item) => Map<String, dynamic>.from(item))
            .toList();
        Future<void> activate(String id) async {
          try {
            await _dispatch({
              'kind': 'collection',
              'name': widget.name,
              'action': 'activate',
              'value': id,
              'revision': data['revision'],
            });
          } catch (_) {
            /* Dispatch presents the failure. */
          }
        }
        return UiCollectionView(
          items: items,
          breadcrumbs: (definition['breadcrumbs'] as List? ?? [])
              .whereType<Map>()
              .map(
                (item) => UiBreadcrumbEntry(
                  id: item['id'] as String,
                  label: item['label'] as String,
                ),
              )
              .toList(),
          onBreadcrumb: !enabled ? null : activate,
          fileMode: definition['fileMode'] != false,
          selectionEnabled: definition['selectionEnabled'] != false,
          selectedId: selection ?? definition['selection'] as String?,
          emptyLabel: definition['emptyLabel'] as String?,
          error: definition['error'] as String?,
          onRetry: _reload,
          onSelect: !enabled
              ? null
              : (id) async {
                  setState(() => selection = id);
                  try {
                    await _dispatch({
                      'kind': 'collection',
                      'name': widget.name,
                      'action': 'select',
                      'value': id,
                      'revision': data['revision'],
                    });
                  } catch (_) {}
                },
          onActivate: !enabled
              ? null
              : (item) => activate(item['id'] as String),
        );
      case 'imagecanvas':
        return widget.imageBuilder?.call(definition) ??
            const Center(child: Text('Image canvas unavailable.'));
      case 'form':
        return _FormRenderer(
          part: UiFormPart.fromMetadata(definition),
          name: widget.name,
          revision:
              (definition['revision'] as num?)?.toInt() ?? widget.revision,
          enabled: enabled,
          onAction: _dispatch,
          secretSaver: widget.secretSaver,
        );
      case 'toggle':
        return UiToggle(
          label: _NeuronViewState._text(definition['label']),
          on: definition['on'] == true,
          onChanged: enabled ? (_) => _act('toggle', 'flip') : null,
        );
      case 'entrycontrol':
        final spec = definition['spec'];
        if (spec is! Map) return const SizedBox.shrink();
        final brain = widget.name.split('/').first;
        return UiEntryControl(
          mode: EntryControlMode.parse(spec['mode']),
          label: _NeuronViewState._text(spec['label']),
          entryName: spec['entryName'] as String?,
          entries: widget.entries?.call(brain),
          enabled: enabled,
          onChanged: () => _dispatch({
            'kind': 'entrycontrol',
            'name': widget.name,
            'action': 'changed',
          }),
        );
      case 'preference':
        final spec = definition['spec'];
        if (spec is! Map) return const SizedBox.shrink();
        final store = PreferenceStore.parse(spec['store']);
        if (store == null) return const SizedBox.shrink();
        final choices = spec['choices'];
        return UiPreferenceControl(
          store: store,
          label: _NeuronViewState._text(spec['label']),
          kind: PreferenceKind.parse(spec['kind']),
          choices: [
            if (choices is List)
              for (final choice in choices.whereType<Map>())
                PreferenceChoiceItem(
                  _NeuronViewState._text(choice['id']),
                  _NeuronViewState._text(choice['label']),
                  enabled: choice['enabled'] != false,
                  detail: choice['detail'] as String?,
                ),
          ],
          value: definition['value'] as String?,
          local: widget.localPreferences,
          enabled: enabled,
          onSet: (value) async {
            final send = widget.onAction;
            if (send == null) throw StateError('No transport.');
            await _events.dispatch(send, {
              'kind': 'preference',
              'name': widget.name,
              'action': 'set',
              'value': value,
            });
          },
        );
      case 'progress':
        return UiProgress(
          determinate: definition['determinate'] == true,
          value: _NeuronViewState._number(definition['value']),
          label: _NeuronViewState._text(definition['label']),
        );
      case 'infobar':
        if (definition['visible'] == false) return const SizedBox.shrink();
        return UiInfoBar(
          severity: _NeuronViewState._text(definition['severity'], 'info'),
          title: _NeuronViewState._text(definition['title']),
          body: _NeuronViewState._text(definition['body']),
          onDismiss: enabled ? () => _act('infobar', 'dismiss') : null,
        );
      case 'image':
        return UiImage.network(
          url: _NeuronViewState._text(definition['url']),
          caption: _NeuronViewState._text(definition['prompt']),
        );
      case 'expander':
        return UiExpander(
          part: UiExpanderPart(
            name: widget.name,
            header: _NeuronViewState._text(definition['header']),
            expanded: definition['expanded'] == true,
          ),
          onToggle: enabled ? () => _act('expander', 'toggle') : null,
          children: children,
        );
      case 'graph':
        return UiGraph(part: UiGraphPart.fromMetadata(definition));
      default:
        return _FallbackRenderer(kind: widget.kind);
    }
  }
}

class _NeuronTextField extends StatefulWidget {
  const _NeuronTextField({
    super.key,
    required this.name,
    required this.definition,
    required this.enabled,
    required this.dispatch,
  });
  final String name;
  final Map<String, dynamic> definition;
  final bool enabled;
  final Future<void> Function(Map<String, dynamic>) dispatch;
  @override
  State<_NeuronTextField> createState() => _NeuronTextFieldState();
}

class _NeuronTextFieldState extends State<_NeuronTextField> {
  // A secret's typed value stays here: its state only says whether a value is set, and that is
  // never written into the editable text.
  bool get _secret {
    final kind = '${widget.definition['kind'] ?? ''}'.toLowerCase();
    return kind == 'secret' || kind == 'password';
  }

  bool get _secretIsSet =>
      _secret && (widget.definition['value'] as String? ?? '').isNotEmpty;

  late final TextEditingController _controller = TextEditingController(
    text: _secret ? '' : widget.definition['value'] as String? ?? '',
  );
  final _focus = FocusNode();
  int _edit = 0, _pending = 0;
  bool _failedEdit = false;
  @override
  void didUpdateWidget(_NeuronTextField old) {
    super.didUpdateWidget(old);
    if (_secret) return;
    final value = widget.definition['value'] as String? ?? '';
    if (_pending == 0 && !_failedEdit && _controller.text != value) {
      _controller.value = TextEditingValue(
        text: value,
        selection: TextSelection.collapsed(offset: value.length),
      );
    }
  }

  Future<void> _change(String value) async {
    final edit = ++_edit;
    _pending++;
    try {
      await widget.dispatch({
        'kind': 'textfield',
        'name': widget.name,
        'value': value,
      });
      if (edit == _edit) _failedEdit = false;
    } catch (_) {
      if (edit == _edit) _failedEdit = true;
    } finally {
      _pending--;
      if (mounted && edit == _edit) setState(() {});
    }
  }

  void _submit(String target) {
    _focus.unfocus();
    unawaited(
      widget
          .dispatch({'kind': 'button', 'name': target})
          .catchError((Object _) {}),
    );
  }

  @override
  Widget build(BuildContext context) {
    final kind = '${widget.definition['kind'] ?? ''}'.toLowerCase();
    final multiline = kind == 'multiline';
    final target = widget.definition['submitButton'] as String?;
    final label = widget.definition['label'] as String? ?? '';
    final helper = _secretIsSet && _controller.text.isEmpty
        ? 'A value is set'
        : null;
    // Server layouts may give the field a fixed extent that cannot hold the
    // label above the box; then the label moves into the placeholder.
    final needed = 8 + 27 + (multiline ? 60 : 48) + (helper == null ? 0 : 24);
    return LayoutBuilder(
      builder: (context, constraints) {
        final tight = constraints.maxHeight < needed;
        final field = UiKitField(
          fieldKey: ValueKey('textfield:${widget.name}'),
          label: tight ? '' : label,
          placeholder: tight && label.isNotEmpty ? label : null,
          controller: _controller,
          focusNode: _focus,
          enabled: widget.enabled,
          secret: kind == 'secret' || kind == 'password',
          multiline: multiline,
          textInputAction: target == null
              ? (multiline ? TextInputAction.newline : TextInputAction.done)
              : TextInputAction.send,
          helper: helper,
          onChanged: _change,
          onSubmitted: target == null ? null : (_) => _submit(target),
        );
        return Padding(
          padding: const EdgeInsets.all(4),
          child: target == null
              ? field
              : CallbackShortcuts(
                  bindings: {
                    const SingleActivator(LogicalKeyboardKey.enter): () =>
                        _submit(target),
                  },
                  child: field,
                ),
        );
      },
    );
  }

  @override
  void dispose() {
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }
}

/// Renders the declared fallback for a UI kind that has no dedicated renderer: the kind is named
/// and explained, never blank.
class _FallbackRenderer extends StatelessWidget {
  const _FallbackRenderer({required this.kind});

  final String kind;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text('${RendererRegistry.fallbackLabel}: $kind'),
            const SizedBox(height: 4),
            const Text(
              RendererRegistry.fallbackExplanation,
              textAlign: TextAlign.center,
            ),
          ],
        ),
      ),
    );
  }
}

/// Renders a declarative form from one read: typed fields, a date picker and a masked secret
/// input. Field edits dispatch a `form` event; Save submits the whole form atomically.
class _FormRenderer extends StatefulWidget {
  const _FormRenderer({
    required this.part,
    required this.name,
    required this.revision,
    required this.enabled,
    required this.onAction,
    this.secretSaver,
  });

  final UiFormPart part;
  final String name;
  final int revision;
  final bool enabled;
  final Future<void> Function(Map<String, dynamic>)? onAction;
  final SecretSaver? secretSaver;

  @override
  State<_FormRenderer> createState() => _FormRendererState();
}

class _FormRendererState extends State<_FormRenderer> {
  late int _nextRevision = widget.revision;
  late Map<String, String?> values = _initial();

  Map<String, String?> _initial() => {
    for (final field in widget.part.fields) field.name: field.value,
  };

  @override
  void didUpdateWidget(_FormRenderer old) {
    super.didUpdateWidget(old);
    if (old.revision != widget.revision) {
      _nextRevision = widget.revision;
      values = _initial();
    }
  }

  Future<void> dispatch(Map<String, Object?> event) async {
    // Each queued draft, secret or submit advances the form by one revision.
    // Count only our own writes so a concurrent writer still conflicts on Save.
    final expectedRevision = _nextRevision++;
    try {
      await widget.onAction?.call({
        'kind': 'form',
        'name': widget.name,
        'revision': expectedRevision,
        ...event,
      });
    } catch (_) {
      _nextRevision--;
      rethrow;
    }
  }

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (widget.part.title.isNotEmpty) ...[
            Text(
              widget.part.title,
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 8),
          ],
          for (final field in widget.part.fields) _field(context, field),
          const SizedBox(height: 12),
          Row(
            children: [
              FilledButton(
                onPressed: widget.enabled
                    ? () => dispatch({'action': 'submit'})
                    : null,
                child: const Text('Save'),
              ),
              if (widget.part.submitted) ...[
                const SizedBox(width: 8),
                const Text('Saved'),
              ],
            ],
          ),
        ],
      ),
    );
  }

  Widget _field(BuildContext context, UiFormField field) {
    final label = field.required ? '${field.label} *' : field.label;
    switch (field.kind) {
      case 'Secret':
        // The raw secret is posted once to the vault; only the returned reference reaches the
        // form. Without a vault saver there is no way to set it, so the handle stays read-only.
        final saver = widget.secretSaver;
        if (saver == null) {
          return Padding(
            padding: const EdgeInsets.symmetric(vertical: 4),
            child: TextFormField(
              key: Key('form_secret_${field.name}'),
              obscureText: true,
              readOnly: true,
              enabled: widget.enabled,
              decoration: InputDecoration(
                labelText: label,
                isDense: true,
                helperText: field.secretSet ? '•••• set' : 'Not set',
              ),
            ),
          );
        }
        return _SecretInput(
          key: Key('form_secret_${field.name}'),
          label: label,
          enabled: widget.enabled,
          isSet: field.secretSet,
          save: (value) async {
            final reference = await saver(field.name, value);
            if (reference != null && reference.isNotEmpty) {
              await dispatch({
                'action': 'secret',
                'field': field.name,
                'value': reference,
              });
            }
            return reference;
          },
        );
      case 'Date':
        final value = values[field.name];
        return Padding(
          padding: const EdgeInsets.symmetric(vertical: 4),
          child: Row(
            children: [
              Expanded(
                child: InputDecorator(
                  decoration: InputDecoration(labelText: label, isDense: true),
                  child: Text(
                    value == null || value.isEmpty ? 'Pick a date' : value,
                  ),
                ),
              ),
              IconButton(
                key: Key('form_date_${field.name}'),
                icon: const Icon(Icons.calendar_today),
                tooltip: 'Pick ${field.label}',
                onPressed: widget.enabled ? () => _pickDate(field) : null,
              ),
            ],
          ),
        );
      case 'Boolean':
        return SwitchListTile(
          contentPadding: EdgeInsets.zero,
          value: values[field.name] == 'true',
          title: Text(label),
          onChanged: widget.enabled
              ? (on) => dispatch({
                  'field': field.name,
                  'value': on ? 'true' : 'false',
                })
              : null,
        );
      case 'Choice':
        return Padding(
          padding: const EdgeInsets.symmetric(vertical: 4),
          child: DropdownButtonFormField<String>(
            initialValue: field.choices.contains(values[field.name])
                ? values[field.name]
                : null,
            decoration: InputDecoration(labelText: label, isDense: true),
            items: [
              for (final choice in field.choices)
                DropdownMenuItem(value: choice, child: Text(choice)),
            ],
            onChanged: widget.enabled
                ? (value) => dispatch({'field': field.name, 'value': value})
                : null,
          ),
        );
      default:
        return Padding(
          padding: const EdgeInsets.symmetric(vertical: 4),
          child: TextFormField(
            key: Key('form_field_${field.name}'),
            initialValue: values[field.name] ?? '',
            enabled: widget.enabled,
            decoration: InputDecoration(
              labelText: label,
              isDense: true,
              helperText: field.supported ? null : 'Shown as plain text.',
            ),
            onChanged: (value) =>
                dispatch({'field': field.name, 'value': value}),
          ),
        );
    }
  }

  Future<void> _pickDate(UiFormField field) async {
    final current = values[field.name];
    final parsed = current == null || current.isEmpty
        ? null
        : DateTime.tryParse(current);
    final picked = await showDatePicker(
      context: context,
      initialDate: parsed ?? DateTime.now(),
      firstDate: DateTime(1900),
      lastDate: DateTime(2200),
    );
    if (picked == null) {
      return;
    }
    final formatted = picked.toIso8601String().split('T').first;
    setState(() => values[field.name] = formatted);
    await dispatch({'field': field.name, 'value': formatted});
  }
}

/// A masked secret input that hands the raw value to the vault once and then drops it, keeping
/// only the returned reference. The field text is cleared on success so the raw value is not held.
class _SecretInput extends StatefulWidget {
  const _SecretInput({
    super.key,
    required this.label,
    required this.enabled,
    required this.isSet,
    required this.save,
  });

  final String label;
  final bool enabled;
  final bool isSet;
  final Future<String?> Function(String value) save;

  @override
  State<_SecretInput> createState() => _SecretInputState();
}

class _SecretInputState extends State<_SecretInput> {
  final controller = TextEditingController();
  bool set = false;
  bool saving = false;

  @override
  void initState() {
    super.initState();
    set = widget.isSet;
  }

  Future<void> submit() async {
    final value = controller.text;
    if (value.isEmpty || saving) return;
    setState(() => saving = true);
    try {
      final reference = await widget.save(value);
      if (!mounted) return;
      setState(() {
        set = reference != null && reference.isNotEmpty;
        saving = false;
        controller.clear();
      });
    } catch (_) {
      if (mounted) setState(() => saving = false);
      rethrow;
    }
  }

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.symmetric(vertical: 4),
    child: TextFormField(
      controller: controller,
      obscureText: true,
      enabled: widget.enabled && !saving,
      decoration: InputDecoration(
        labelText: widget.label,
        isDense: true,
        helperText: set ? '•••• set' : 'Not set',
        suffixIcon: IconButton(
          key: const Key('form_secret_save'),
          icon: const Icon(Icons.check),
          tooltip: 'Store secret',
          onPressed: widget.enabled && !saving ? submit : null,
        ),
      ),
      onFieldSubmitted: (_) => submit(),
    ),
  );

  @override
  void dispose() {
    controller.dispose();
    super.dispose();
  }
}
