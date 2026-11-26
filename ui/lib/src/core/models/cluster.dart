// A brain the signed-in person is in, as GET /identity/brains lists it: its id and its kind (Personal or Circle).
final class BrainEntry {
  const BrainEntry({required this.id, required this.kind});

  final String id, kind;
  bool get isCircle => kind == 'Circle';

  factory BrainEntry.fromJson(Map<String, dynamic> json) => BrainEntry(
    id: json['id'] as String? ?? '',
    kind: json['kind'] as String? ?? 'Personal',
  );
}
