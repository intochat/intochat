import '../theme/wire_node_values.dart';

/// Formats a wire model identifier for a message's model chip.
///
/// Device models (`device:{device}/{modelId}`) render as `"{cardName} · {device}"`
/// (falling back to the inner model id when [cardNames] has no entry).
/// Cloud and local models resolve through [cardNames] with [modelId] as fallback.
String modelLabel(String modelId, Map<String, String> cardNames) {
  final deviceRef = parseDeviceModelId(modelId);
  if (deviceRef != null) {
    final friendly = cardNames[deviceRef.modelId] ?? deviceRef.modelId;
    return '$friendly · ${deviceRef.device}';
  }
  return cardNames[modelId] ?? modelId;
}
