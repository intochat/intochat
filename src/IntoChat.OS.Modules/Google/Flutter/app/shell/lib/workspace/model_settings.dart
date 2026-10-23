import 'package:intochat_os_flutter/intochat_os_flutter.dart';
import 'package:flutter/material.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

typedef ModelCatalogLoader = Future<ChatModelCatalog> Function();

const autoModelLabel = 'Auto, cheapest model with tools';
const unavailableModelReason = 'Add a key in API keys';
const noModelReason = 'Add an API key';

/// The same configured catalog is used for a default and a conversation override.
class ModelSettings extends StatefulWidget {
  const ModelSettings({
    super.key,
    this.load,
    this.selectedId,
    required this.onSelected,
    this.conversation = false,
    this.onAddKey,
    this.select = false,
  });
  final ModelCatalogLoader? load;

  /// Also offers the choice as a select above the rows (Settings, AI models).
  final bool select;
  final String? selectedId;
  final ValueChanged<String?> onSelected;
  final bool conversation;

  /// Opens Settings on the API keys tab; offered when no model is available.
  final VoidCallback? onAddKey;
  @override
  State<ModelSettings> createState() => _ModelSettingsState();
}

class _ModelSettingsState extends State<ModelSettings> {
  ChatModelCatalog? _catalog;
  String? _error;
  bool _loading = false;
  int _generation = 0;
  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void didUpdateWidget(ModelSettings oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.load != widget.load) {
      _catalog = null;
      _error = null;
      _loading = false;
      _load();
    }
  }

  Future<void> _load() async {
    final generation = ++_generation;
    if (widget.load == null) return;
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final catalog = await widget.load!();
      if (mounted && generation == _generation) {
        setState(() => _catalog = catalog);
      }
    } catch (_) {
      if (mounted && generation == _generation) {
        setState(() => _error = 'Models could not be loaded.');
      }
    } finally {
      if (mounted && generation == _generation) {
        setState(() => _loading = false);
      }
    }
  }

  // Auto always leads and is always selectable: the server picks the cheapest
  // available model with tools. Other models without a key are shown dimmed
  // with the reason and cannot be chosen.
  Widget _option(ChatModelOption option) {
    final automatic = option.id == null;
    // Ruling 8: Auto is always selectable; the server resolves it.
    final selectable = option.available || automatic;
    final active = widget.selectedId == option.id;
    final c = UiColors.of(context);
    final type = UiType.of(context);
    final reason = option.reason ?? unavailableModelReason;
    // Figma `.model-option`: auto / 1fr / auto, gap 10, hairline, radius 10,
    // padding 11; the active row is an accent border on accentTint.
    final tile = Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Material(
        color: active ? c.accentTint : c.surface,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(
            UiTokens.of(context).controlRadius,
          ),
          side: BorderSide(color: active ? c.accent : c.hairline),
        ),
        clipBehavior: Clip.antiAlias,
        child: ListTile(
          contentPadding: const EdgeInsets.all(11),
          horizontalTitleGap: 10,
          selected: active,
          enabled: selectable,
          leading: Icon(
            active ? Icons.radio_button_checked : Icons.radio_button_off,
            color: active ? c.accent : c.muted,
          ),
          title: Text(
            automatic ? autoModelLabel : option.label,
            style: type.body.copyWith(
              color: c.strong,
              fontWeight: active ? FontWeight.w700 : FontWeight.w500,
            ),
          ),
          subtitle: Text(
            style: type.meta.copyWith(color: c.muted),
            [
              if (option.provider != null) option.provider!,
              if (option.model != null) option.model!,
              if (!option.available)
                automatic
                    ? noModelReason
                    : option.reason ?? unavailableModelReason,
              if (option.capabilities.isNotEmpty)
                option.capabilities.join(' · '),
            ].join(' · '),
          ),
          onTap: selectable && !_loading
              ? () => widget.onSelected(option.id)
              : null,
        ),
      ),
    );
    final shown = selectable ? tile : Opacity(opacity: 0.5, child: tile);
    if (option.available || widget.onAddKey == null) return shown;
    // Only a missing key is fixed in API keys; other server reasons are not.
    if (!automatic && option.reason != null) return shown;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        shown,
        TextButton(
          key: automatic
              ? const Key('model-add-api-key')
              : Key('model-add-key-${option.id}'),
          onPressed: widget.onAddKey,
          child: Text(automatic ? noModelReason : reason),
        ),
      ],
    );
  }

  // The default model as a select; unavailable models are listed, disabled.
  Widget _select(List<ChatModelOption> options) => Padding(
    padding: const EdgeInsets.only(bottom: 20),
    child: UiKitSelect<String?>(
      key: const Key('default-model-select'),
      label: 'Default model',
      value: widget.selectedId,
      items: [
        for (final option in options)
          UiKitSelectItem<String?>(
            option.id,
            option.id == null ? autoModelLabel : option.label,
            enabled: option.available || option.id == null,
            detail: option.available
                ? option.provider
                : option.id == null
                ? noModelReason
                : option.reason ?? unavailableModelReason,
          ),
      ],
      onChanged: _loading ? null : widget.onSelected,
    ),
  );

  @override
  Widget build(BuildContext context) {
    final options = _catalog == null
        ? <ChatModelOption>[]
        : [_catalog!.automatic, ..._catalog!.models];
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          widget.conversation ? 'Model for this conversation' : 'Default model',
          style: Theme.of(context).textTheme.titleLarge,
        ),
        const SizedBox(height: 8),
        Text(
          widget.conversation
              ? 'Applies to your next message. Previous messages stay in the conversation.'
              : 'Used for new conversations on this device. You can choose a different model beside the chat composer.',
        ),
        const SizedBox(height: 20),
        if (widget.load == null)
          const Text('Connect to your workspace to see configured models.'),
        if (_loading) const LinearProgressIndicator(),
        if (_error != null) ...[
          Text(_error!),
          TextButton(
            onPressed: _loading ? null : _load,
            child: const Text('Retry'),
          ),
        ],
        if (_catalog != null &&
            widget.selectedId != null &&
            !options.any((m) => m.id == widget.selectedId))
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 12),
            child: Text(
              'Your saved model is no longer available. Choose another model or Automatic.',
            ),
          ),
        if (widget.select && options.isNotEmpty) _select(options),
        for (final option in options) _option(option),
        if (_catalog != null && _catalog!.models.isEmpty)
          const Text(
            'No selectable models are configured. Providers are managed by your workspace administrator.',
          ),
        if (!widget.conversation) ...[
          const SizedBox(height: 16),
          const Text(
            'Only configured chat models are listed. Provider credentials stay on the server.',
          ),
        ],
      ],
    );
  }
}

class ConversationModelPicker extends StatelessWidget {
  const ConversationModelPicker({
    super.key,
    required this.load,
    this.selectedId,
    required this.onSelected,
    this.enabled = true,
    this.onAddKey,
  });
  final ModelCatalogLoader load;
  final String? selectedId;
  final ValueChanged<String?> onSelected;
  final bool enabled;
  final VoidCallback? onAddKey;
  @override
  Widget build(BuildContext context) => Tooltip(
    message: 'Choose model',
    child: TextButton.icon(
      key: const Key('conversation-model-picker'),
      icon: const Icon(Icons.tune, size: 14),
      label: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 115),
        child: Text(
          selectedId == null ? 'Auto' : selectedId!.split(':').last,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          style: const TextStyle(fontSize: 12),
        ),
      ),
      onPressed: !enabled
          ? null
          : () => showDialog<void>(
              context: context,
              builder: (dialogContext) => Dialog(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(
                    maxWidth: 520,
                    maxHeight: 640,
                  ),
                  child: SingleChildScrollView(
                    padding: const EdgeInsets.all(24),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Align(
                          alignment: Alignment.centerRight,
                          child: IconButton(
                            tooltip: 'Close model picker',
                            onPressed: () => Navigator.pop(dialogContext),
                            icon: const Icon(Icons.close),
                          ),
                        ),
                        ModelSettings(
                          load: load,
                          selectedId: selectedId,
                          conversation: true,
                          onAddKey: onAddKey == null
                              ? null
                              : () {
                                  Navigator.pop(dialogContext);
                                  onAddKey!();
                                },
                          onSelected: (id) {
                            onSelected(id);
                            Navigator.pop(dialogContext);
                          },
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
    ),
  );
}
