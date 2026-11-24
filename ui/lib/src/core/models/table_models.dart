final class TableRequestException implements Exception {
  const TableRequestException(this.statusCode, this.message);
  final int statusCode;
  final String message;
  @override
  String toString() => 'Table request failed ($statusCode): $message';
}
