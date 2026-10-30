import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:intochat_os_ui/intochat_os_ui.dart';

void main() {
  testWidgets(
    'IcBreadcrumbBar renders segments, chevrons, and fires callbacks',
    (tester) async {
      String? clickedKey;
      var copiedUri = false;

      await tester.pumpWidget(
        MaterialApp(
          home: IcTheme(
            colors: IcColors.dark,
            child: Scaffold(
              body: SizedBox(
                width: 900,
                child: IcBreadcrumbBar(
                  items: [
                    IcBreadcrumbItem(
                      id: 'crumb-root',
                      label: 'Storages',
                      icon: Icons.dns_rounded,
                      onTap: () => clickedKey = 'crumb-root',
                    ),
                    IcBreadcrumbItem(
                      id: 'crumb-storage',
                      label: 'Local Disk',
                      onTap: () => clickedKey = 'crumb-storage',
                    ),
                    IcBreadcrumbItem(
                      id: 'crumb-0',
                      label: 'Projects',
                      onTap: () => clickedKey = 'crumb-0',
                    ),
                    IcBreadcrumbItem(
                      id: 'crumb-1',
                      label: 'IntoChat',
                      onTap: () => clickedKey = 'crumb-1',
                    ),
                  ],
                  neuronUri:
                      'neuron://owner/local-disk.files/projects/intochat',
                  trailingActions: [
                    TextButton(
                      onPressed: () => copiedUri = true,
                      child: const Text('Copy neuron:// URI'),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      );

      expect(find.text('Storages'), findsOneWidget);
      expect(find.text('Local Disk'), findsOneWidget);
      expect(find.text('Projects'), findsOneWidget);
      expect(find.text('IntoChat'), findsOneWidget);
      expect(
        find.text('neuron://owner/local-disk.files/projects/intochat'),
        findsOneWidget,
      );

      await tester.tap(find.text('Projects'));
      await tester.pump();
      expect(clickedKey, equals('crumb-0'));

      await tester.tap(find.text('Copy neuron:// URI'));
      await tester.pump();
      expect(copiedUri, isTrue);
    },
  );

  testWidgets(
    'IcBreadcrumbBar collapses intermediate items into ellipsis when narrow',
    (tester) async {
      await tester.pumpWidget(
        MaterialApp(
          home: IcTheme(
            colors: IcColors.dark,
            child: Scaffold(
              body: SizedBox(
                width: 450,
                child: IcBreadcrumbBar(
                  items: [
                    IcBreadcrumbItem(
                      id: 'crumb-root',
                      label: 'Storages',
                      onTap: () {},
                    ),
                    IcBreadcrumbItem(
                      id: 'crumb-storage',
                      label: 'Local Disk',
                      onTap: () {},
                    ),
                    IcBreadcrumbItem(
                      id: 'crumb-0',
                      label: 'Workspace',
                      onTap: () {},
                    ),
                    IcBreadcrumbItem(
                      id: 'crumb-1',
                      label: 'Projects',
                      onTap: () {},
                    ),
                    IcBreadcrumbItem(
                      id: 'crumb-2',
                      label: 'IntoChat',
                      onTap: () {},
                    ),
                    IcBreadcrumbItem(
                      id: 'crumb-3',
                      label: 'Modules',
                      onTap: () {},
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      );

      expect(find.text('Storages'), findsOneWidget);
      expect(find.text('Modules'), findsOneWidget);
      expect(
        find.byKey(const ValueKey('breadcrumb-overflow-button')),
        findsOneWidget,
      );
    },
  );

  testWidgets(
    'IcFilesFinderView renders Finder sidebar, toolbar, sortable list, grid toggle, and QuickView inspector',
    (tester) async {
      String? openedItem;
      var askedIno = false;

      await tester.pumpWidget(
        MaterialApp(
          home: IcTheme(
            colors: IcColors.dark,
            child: Scaffold(
              body: SizedBox(
                width: 1180,
                height: 680,
                child: IcFilesFinderView(
                  storages: [
                    IcFilesStorageEntry(
                      key: 'storage-0',
                      label: 'Local Files',
                      quotaText: 'Local folder',
                      icon: Icons.storage_rounded,
                      selected: true,
                      unavailable: false,
                      onTap: () {},
                    ),
                  ],
                  breadcrumbs: [
                    IcBreadcrumbItem(
                      id: 'crumb-root',
                      label: 'Storages',
                      onTap: () {},
                    ),
                    IcBreadcrumbItem(
                      id: 'crumb-storage',
                      label: 'Local Files',
                      onTap: () {},
                    ),
                    IcBreadcrumbItem(
                      id: 'crumb-0',
                      label: 'Workspace',
                      onTap: () {},
                    ),
                  ],
                  activeNeuronUri:
                      'neuron://owner/local-files.files/workspace/notes.md',
                  activeDisplayPath: 'Local Files/Workspace',
                  items: [
                    IcFilesItemEntry(
                      key: 'item-row-0',
                      name: 'Docs',
                      isFolder: true,
                      selected: false,
                      kindLabel: 'Folder',
                      sizeLabel: '--',
                      modifiedLabel: '2026-10-30 12:00',
                      whereLabel: 'Local Files/Workspace',
                      onOpen: () => openedItem = 'Docs',
                      onSelect: () => openedItem = 'Docs',
                    ),
                    IcFilesItemEntry(
                      key: 'item-row-1',
                      name: 'notes.md',
                      isFolder: false,
                      selected: true,
                      kindLabel: 'File',
                      sizeLabel: '14.2 KB',
                      modifiedLabel: '2026-10-30 13:05',
                      whereLabel: 'Local Files/Workspace',
                      onOpen: () => openedItem = 'notes.md',
                      onSelect: () => openedItem = 'notes.md',
                    ),
                  ],
                  selectedName: 'notes.md',
                  selectedMeta:
                      'MD file · 14540 B · Local Files/Workspace/notes.md',
                  selectedNeuronUri:
                      'neuron://owner/local-files.files/workspace/notes.md',
                  previewText: '# Architecture Notes\nFinder-grade desktop UI.',
                  onAskIno: () => askedIno = true,
                ),
              ),
            ),
          ),
        ),
      );

      // Sidebar sections
      expect(find.text('FAVOURITES'), findsOneWidget);
      expect(find.text('LOCATIONS'), findsOneWidget);
      expect(find.text('Local Files'), findsWidgets);

      // List rows & QuickView inspector
      expect(find.text('Docs'), findsOneWidget);
      expect(find.text('notes.md'), findsWidgets);
      expect(find.text('14.2 KB'), findsOneWidget);
      expect(find.textContaining('Architecture Notes'), findsOneWidget);
      expect(find.text('1 of 2 selected'), findsOneWidget);

      // Click row to trigger callback
      await tester.tap(find.text('Docs'));
      await tester.pump();
      expect(openedItem, equals('Docs'));

      // Click Ask Ino in QuickView inspector
      await tester.tap(find.text('Ask Ino about this file'));
      await tester.pump();
      expect(askedIno, isTrue);
    },
  );
}
