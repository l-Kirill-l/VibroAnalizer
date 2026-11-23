# VibroAnalyzer — Windows x64

Исходный код: ветка `v2`, коммит `2d774b9`.

## Файлы

- `VibroAnalyzer-v2-win-x64.zip` — framework-dependent сборка.
  Требуется установленный **.NET 10 Desktop Runtime**.
- `VibroAnalyzer-v2-win-x64-self-contained.zip` — self-contained single-file.
  .NET устанавливать не нужно; внутри один `VibroAnalyzer.App.exe`.

## Запуск

1. Распакуйте архив в отдельную папку.
2. Запустите `VibroAnalyzer.App.exe`.

Для framework-dependent варианта распаковывайте **все** файлы архива, не только `.exe`.

## Проверка целостности

```bash
sha256sum -c SHA256SUMS
```

## Примечание

Архивы собраны из коммита `2d774b9` без PDB-файлов.
