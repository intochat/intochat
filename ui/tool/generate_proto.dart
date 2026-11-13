import 'dart:io';

/// Regenerates the Dart client for the Neurons service from the Platform proto.
/// Run from ui with: `dart run tool/generate_proto.dart`
void main() {
  final scriptDir = File.fromUri(Platform.script).parent;
  final coreDir = scriptDir.parent;
  Directory.current = coreDir;

  final protoDir = Directory(
    '../../../../../IntoChat.OS.Authority/IntoChat.OS.Authority/Neurons',
  );
  if (!protoDir.existsSync()) {
    stderr.writeln('Proto directory not found at ${protoDir.path}');
    exit(1);
  }

  final generatedDir = Directory('lib/src/generated');
  if (!generatedDir.existsSync()) {
    generatedDir.createSync(recursive: true);
  }

  final pluginPath = Platform.isWindows
      ? '${scriptDir.path}\\protoc-gen-dart.bat'
      : '${scriptDir.path}/protoc-gen-dart';

  final protoFile = '${protoDir.path}/neurons.proto';
  final result = Process.runSync('protoc', [
    '--plugin=protoc-gen-dart=$pluginPath',
    '--proto_path=${protoDir.path}',
    '--dart_out=grpc:lib/src/generated',
    protoFile,
  ], runInShell: true);

  if (result.exitCode != 0) {
    stderr.write(result.stderr);
    stdout.write(result.stdout);
    exit(result.exitCode);
  }

  stdout.write(result.stdout);
  stdout.writeln('Generated proto files in lib/src/generated');
}
