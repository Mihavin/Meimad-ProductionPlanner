# Cimatron → Meimad: определение автоматического экспортера simulation package

## 1. Назначение

Создать автоматический exporter, который запускается из Cimatron после подготовки NC operation и без ручного повторного позиционирования передаёт в Meimad:

- постпроцессированный G-code;
- tool table;
- cutter/holder/material data;
- initial stock или remaining stock;
- fixture geometry;
- finished-part geometry;
- Cimatron procedure UCS;
- номер операции, имя операции и целевую машину;
- manifest с ролями файлов, координатной системой, версиями и хешами.

Meimad должен принять один пакет и открыть его в NC Viewer уже с правильными stock, fixture, part, tools и координатами.

Оператор Cimatron не должен вручную:

- выбирать каждый STL;
- переименовывать файлы;
- вводить origin и rotation;
- повторно указывать G54/G55 для геометрии Cimatron;
- вручную связывать fixture с операцией;
- вручную создавать manifest.

## 2. Граница ответственности

### Cimatron exporter отвечает за

- выбор текущего NC файла и procedure;
- получение procedure UCS;
- запуск postprocessor;
- экспорт G-code;
- экспорт tool/cutter/holder information;
- экспорт stock;
- экспорт fixture;
- экспорт target part;
- построение manifest;
- сохранение относительных путей;
- вычисление SHA-256;
- atomic completion marker.

### Meimad отвечает за

- поиск и импорт пакета;
- проверку manifest;
- связывание с Case и Case Operation;
- сопоставление с Machine и Postprocessor;
- загрузку NC и tool table;
- отображение stock/fixture/part;
- machine kinematics;
- simulation и collision checking;
- сохранение package metadata;
- получение и хранение фактического machine G54/G55.

### Не входит в exporter

- генерация траектории;
- изменение G-code после postprocessing;
- расчёт machine kinematics;
- модель станка;
- измерение реального G54 на CNC;
- material removal simulation;
- collision simulation.

## 3. Доступные Cimatron API-возможности

Exporter должен использовать Cimatron E API через C# или Python interop.

### 3.1. Получение приложения

Типовой способ подключения:

```csharp
var appAccess = new interop.CimAppAccess.AppAccess();
var application = (interop.CimatronE.IApplication)
    appAccess.GetApplication();
```

Конкретный способ получения активного document/NC model зависит от версии Cimatron и установленных interop assemblies. Он должен быть изолирован в `CimatronApiAdapter`.

### 3.2. Постпроцессинг по UCS

Использовать `PostByUcsToAllNcProcedures` для постпроцессинга относительно конкретного UCS:

```text
PostByUcsToAllNcProcedures(
    postName,
    outputPath,
    startHome,
    ucsName
)
```

Это предпочтительнее общего post вызова, потому что exporter может явно зафиксировать связь между NC output и procedure UCS.

Если требуется обработать все procedures без явного UCS, допускается `ExecutePostToAllNcProcedures`, но manifest всё равно должен содержать фактический procedure UCS.

### 3.3. Remaining stock

Для Cimatron 2026+ использовать:

```text
ExportProceduresRemainingStock(
    procedureId,
    ucsType,
    outputStlPath
)
```

Метод экспортирует remaining stock NC procedure в STL. Exporter обязан записывать в manifest `stockKind = remaining`.

### 3.4. Cutter и holder data

Использовать:

```text
ExportCuttersAndHoldersToCSV(
    selectedElements,
    cuttersCsvPath,
    holdersCsvPath,
    materialsCsvPath
)
```

Если список selected elements пустой, exporter может выгрузить все cutters текущего NC файла.

### 3.5. Job Manager

Использовать `NcExportJobsData` для XML-экспорта Job Manager. XML нужен для дополнительной связи между job/procedure/operation, если эта информация не полностью присутствует в tool table или NC header.

## 4. Как получить fixture и target part

Fixture и target part должны экспортироваться как отдельные mesh files.

Предпочтительный порядок:

1. Найти в текущем NC document part procedures.
2. Разделить процедуры на `TARGET`, `FIXTURE` и `OTHER`.
3. Экспортировать выбранные entities в STL или STEP.
4. Для нескольких fixture parts сохранить отдельные файлы.
5. Для одинаковой системы координат применить тот же procedure UCS.

Если прямой API-экспорт конкретных entities недоступен в установленной версии, использовать Cimatron Simulator/Data Interface export. В режиме Multi STL Cimatron уже умеет разделять target part, fixture parts и other parts на отдельные STL-файлы.

Нельзя объединять fixture и target part в один файл: Meimad должен различать:

- collision-only geometry;
- target/finished-part geometry;
- optional non-collision geometry.

## 5. Coordinate system policy

Есть два допустимых режима.

### Режим A — экспорт прямо в NC procedure coordinates

Это основной и предпочтительный режим.

Все STL/STEP экспортируются так, чтобы их координаты уже совпадали с координатами NC program/procedure UCS.

Manifest:

```json
"coordinateSystem": {
  "type": "nc-procedure-ucs",
  "name": "UCS_OP010",
  "filesAreIn": "nc-work-coordinates",
  "transform": "identity"
}
```

Преимущества:

- Meimad не выполняет дополнительное вращение;
- исключается ошибка направления transform;
- исключается ошибка порядка Euler angles;
- stock, fixture, part и toolpath сразу совпадают;
- проще проверять результат.

### Режим B — экспорт в Cimatron part-model coordinates

Если Cimatron API не позволяет экспортировать entity в procedure UCS, exporter сохраняет transformation UCS.

```json
"coordinateSystem": {
  "type": "part-model-coordinates",
  "name": "UCS_OP010",
  "filesAreIn": "part-model-coordinates",
  "originMm": { "x": 125.4, "y": -80.0, "z": 42.7 },
  "rotationDeg": { "x": 0.0, "y": 15.0, "z": 90.0 },
  "rotationOrder": "RzRyRx"
}
```

В долгосрочном варианте лучше хранить полную 4×4 matrix, а Euler values оставить для диагностики:

```json
"matrix": [
  0, -1, 0, 125.4,
  1,  0, 0, -80.0,
  0,  0, 1, 42.7,
  0,  0, 0, 1
]
```

Meimad должен валидировать consistency между matrix и origin/rotation, если оба значения присутствуют.

## 6. Cimatron UCS и фактический G54

Нельзя смешивать две разные системы координат.

### Cimatron procedure UCS

Это design/program coordinate system. Она определяет:

- как экспортировать geometry;
- относительно чего постпроцессор пишет NC;
- как Meimad размещает stock, fixture и part относительно toolpath.

### Реальный machine G54/G55

Это measured machine offset. Он определяется после установки детали на конкретном станке.

Разделение:

```text
Cimatron procedure UCS
    → exporter manifest
    → NC Viewer work coordinates

Measured G54/G55
    → DPRNT/GPRINT/MTConnect or manual import
    → Operation + Machine record
    → machine-coordinate simulation
```

Exporter не должен записывать в manifest измеренный G54, если он его не получил с реального станка.

## 7. Package layout

Рекомендуемый layout:

```text
<CaseWorkingFolder>\\Gcode\\<Case>\\<Operation>\\cimatron-export\\
  package.json
  manifest.json
  nc\\O1234.nc
  tools\\TP_MODEL.TOOLS.mht
  tools\\cutters.csv
  tools\\holders.csv
  tools\\materials.csv
  geometry\\stock.stl
  geometry\\fixture-001.stl
  geometry\\fixture-002.stl
  geometry\\part.stl
  metadata\\jobs.xml
  metadata\\export-log.json
  COMPLETE
```

`COMPLETE` создаётся только после успешного завершения всех экспортов. Meimad не должен импортировать пакет, пока marker отсутствует.

## 8. Manifest schema

```json
{
  "schemaVersion": 1,
  "exporter": {
    "name": "Meimad.CimatronExporter",
    "version": "0.1.0",
    "cimatronVersion": "2026.0"
  },
  "source": {
    "application": "Cimatron",
    "project": "Part-123",
    "ncFileName": "O1234.nc",
    "exportedAtUtc": "2026-10-12T06:00:00Z"
  },
  "operation": {
    "number": "010",
    "name": "Roughing",
    "procedureId": 17,
    "procedureName": "OP010_ROUGH"
  },
  "machine": {
    "name": "Haas UMC-500",
    "postprocessor": "HAAS_NGC_UMC500",
    "axisConfiguration": "B_C"
  },
  "units": "mm",
  "coordinateSystem": {
    "type": "nc-procedure-ucs",
    "name": "UCS_OP010",
    "workOffset": "G54",
    "filesAreIn": "nc-work-coordinates",
    "transform": "identity"
  },
  "files": {
    "nc": {
      "path": "nc/O1234.nc",
      "sha256": "..."
    },
    "toolTable": {
      "path": "tools/TP_MODEL.TOOLS.mht",
      "sha256": "..."
    },
    "cutters": {
      "path": "tools/cutters.csv",
      "sha256": "..."
    },
    "holders": {
      "path": "tools/holders.csv",
      "sha256": "..."
    },
    "stock": [
      {
        "path": "geometry/stock.stl",
        "kind": "initial",
        "sha256": "..."
      }
    ],
    "fixtures": [
      {
        "path": "geometry/fixture-001.stl",
        "name": "Base fixture",
        "collision": true,
        "sha256": "..."
      }
    ],
    "part": {
      "path": "geometry/part.stl",
      "role": "finished-part",
      "sha256": "..."
    },
    "jobsXml": {
      "path": "metadata/jobs.xml",
      "sha256": "..."
    }
  }
}
```

## 9. Initial stock и remaining stock

Manifest обязан различать:

```text
initial       исходная заготовка текущей операции
remaining     остаточный материал после предыдущей операции
machined      результат текущей симуляции
```

Примеры:

```json
{
  "path": "geometry/stock-op010.stl",
  "kind": "initial",
  "sourceOperation": null
}
```

```json
{
  "path": "geometry/stock-op020-from-op010.stl",
  "kind": "remaining",
  "sourceOperation": "010"
}
```

Если есть несколько stock candidates, Meimad должен показать их пользователю, но не выбирать молча неправильный файл.

## 10. Machine mapping

Имя машины из Cimatron не должно напрямую менять Meimad Machine без проверки.

Порядок сопоставления:

1. exact configured external machine id;
2. exact postprocessor id;
3. explicit mapping table;
4. compatible machine/postprocessor pair;
5. unresolved mapping — блокирующая диагностика.

Пример:

```json
"machine": {
  "externalId": "UMC500_01",
  "name": "Haas UMC-500",
  "postprocessor": "HAAS_NGC_UMC500"
}
```

Если найдено несколько машин, Meimad не должен выбирать первую. Он должен показать список кандидатов.

## 11. Exporter workflow

### Step 1 — Validate active Cimatron state

Проверить:

- открыт NC document;
- есть хотя бы одна procedure;
- procedure имеет toolpath;
- выбран postprocessor;
- выбран procedure UCS;
- есть target/part data;
- stock существует или может быть экспортирован;
- fixture selection определён.

### Step 2 — Create temporary directory

```text
<output>\\.staging\\{guid}\\
```

Все файлы сначала пишутся во staging directory.

### Step 3 — Export NC

Вызвать `PostByUcsToAllNcProcedures` для конкретного post/UCS. Сохранить фактическое имя созданного файла.

### Step 4 — Export tool data

Создать mht/CSV/XML exports. Все файлы должны сохранять один и тот же tool identifier/T-number.

### Step 5 — Export stock

Для remaining stock вызвать `ExportProceduresRemainingStock`. Для initial stock использовать stock procedure/Data Interface export.

### Step 6 — Export fixture and part

Экспортировать fixture и target procedures в отдельные STL/STEP files в той же coordinate policy.

### Step 7 — Read UCS transform

Считать name, origin, orientation и, если доступно, полную transformation matrix. Нормализовать units и rotation order.

### Step 8 — Build manifest

Добавить относительные paths, roles, operation, machine, UCS, API/exporter versions и hashes.

### Step 9 — Validate output

Проверить наличие файлов, STL readability, non-zero size, valid manifest JSON, valid hashes и NC file header.

### Step 10 — Commit package

Переместить staging directory в final directory атомарно и только затем создать `COMPLETE`.

## 12. Запуск без ручных действий

### Вариант A — Cimatron add-in

Добавить команду `Export to Meimad` в Cimatron. Команда запускает exporter через API, но оператору достаточно одного клика.

### Вариант B — postprocessor hook

После завершения postprocessor вызвать exporter. Это лучший вариант, если GPP/post configuration позволяет выполнить внешний command.

```text
Post complete
    → write NC
    → call Meimad.CimatronExporter.exe
    → export geometry and manifest
```

### Вариант C — existing BAT

Существующий `.bat` запускает exporter после создания NC:

```bat
Meimad.CimatronExporter.exe ^
  --active-cimatron ^
  --output "D:\Meimad\Exports\%PART_NUMBER%\OP%OP_NUMBER%" ^
  --post "%POST_NAME%" ^
  --ucs "%PROCEDURE_UCS%"
```

BAT только оркестрирует процесс. Чтение Cimatron objects, procedures и UCS выполняет C# API adapter.

### Вариант D — folder watcher

Meimad Server или Windows client наблюдает за output folder и автоматически импортирует только папки, в которых появился `COMPLETE`.

## 13. Meimad import workflow

1. Найти `COMPLETE`.
2. Прочитать manifest.
3. Проверить schemaVersion.
4. Проверить file paths — только внутри разрешённого Case/Network root.
5. Проверить SHA-256.
6. Определить Case по external part number, NC header или выбранному Case.
7. Определить Operation по operation number/name.
8. Сопоставить Machine/Postprocessor.
9. Создать или обновить fixture/stock links.
10. Сохранить Work CS/Cimatron setup.
11. Подготовить NC Viewer scene.
12. Сформировать import report.

Import должен быть idempotent: повторный импорт того же manifest/hash не должен создавать дубликаты.

## 14. Ошибки и поведение

### Блокирующие ошибки

- отсутствует NC;
- отсутствует manifest;
- invalid JSON;
- procedure id не найден;
- отсутствует UCS;
- файл fixture/stock не читается;
- units неизвестны;
- tool table не соответствует NC;
- Machine mapping неоднозначен;
- hash mismatch;
- export завершился без `COMPLETE`.

### Предупреждения

- part model отсутствует;
- fixture отсутствует;
- holder dimensions incomplete;
- stock найден только по fallback rule;
- Cimatron version отличается от протестированной;
- exporter использовал default mesh tolerance.

Нельзя автоматически продолжать при неизвестной системе координат. Неправильная геометрия в правильном viewer опаснее, чем остановленный импорт.

## 15. Runtime G54 import

Exporter передаёт design/work coordinates. Реальное measured G54 добавляется отдельно:

```text
Offset Loader / DPRNT / GPRINT
       ↓
Meimad machine adapter
       ↓
Operation + Machine + Work offset record
```

Record должен содержать:

```json
{
  "machineId": "haas-vf3-01",
  "operationId": "op-010",
  "workOffset": "G54",
  "x": 125.402,
  "y": -80.016,
  "z": 42.691,
  "a": 0.0,
  "b": 0.0,
  "c": 90.0,
  "source": "machine-dprnt",
  "measuredAt": "2026-10-12T06:30:00Z",
  "programHash": "...",
  "operatorId": "..."
}
```

Нельзя заменять новый measured offset старым только по имени G54. Связь должна включать Operation, Machine, program/release hash и timestamp.

## 16. Безопасность и файловые ограничения

- Разрешить только approved output roots.
- Запретить path traversal в manifest.
- Сохранять относительные paths.
- Не выполнять команды из manifest.
- Не принимать arbitrary UNC paths без проверки.
- Проверять размер файлов до загрузки.
- Ограничить количество fixture files.
- Логировать exporter version и Cimatron version.
- Сохранять export log при частичном сбое.

## 17. Tests

### API tests

- active NC application connection;
- one and multiple procedures;
- named UCS;
- missing UCS;
- post failure;
- stock export success/failure;
- cutter/holder CSV export;
- fixture export;
- repeated export to same directory.

### Coordinate tests

- identity UCS;
- translation only;
- X/Y/Z rotations;
- RzRyRx order;
- negative coordinates;
- rotated 3+2 procedure;
- comparison of NC toolpath bounds and exported STL bounds.

### Package tests

- complete package;
- missing file;
- invalid hash;
- duplicate import;
- interrupted export without COMPLETE;
- old schema version;
- multiple stock candidates;
- multiple fixtures.

### End-to-end tests

1. Open Cimatron sample NC.
2. Run exporter.
3. Import package into Meimad.
4. Open NC Viewer.
5. Confirm toolpath, stock, fixture and part align.
6. Run simulation.
7. Confirm machine view and collision frame.
8. Export resulting stock.
9. Import measured G54 test event.
10. Reopen the same Operation/Machine and confirm persistence.

## 18. Acceptance criteria

The feature is accepted when:

- operator initiates one export action or the configured post hook;
- no STL is manually selected in Meimad;
- no coordinate origin/rotation is manually entered;
- NC and geometry use the same procedure UCS;
- stock and fixture appear in the correct location;
- tool table is linked to the same NC release;
- multiple fixture files are supported;
- initial and remaining stock are distinguished;
- import is repeatable without duplicates;
- missing/ambiguous data blocks import with a clear error;
- the actual measured G54 is stored separately per Operation/Machine;
- Haas and Mazak test packages pass alignment and simulation checks.

## 19. Recommended implementation order

1. Build read-only Cimatron API probe: application, active document, NC model, procedures, UCS names.
2. Export one procedure's G-code by UCS.
3. Export cutter/holder CSV and Job Manager XML.
4. Export one stock STL.
5. Export one fixture STL and one part STL.
6. Write manifest with identity transform.
7. Import package into a standalone Meimad test command.
8. Validate coordinates with a known cube/fixture test.
9. Add multiple fixtures and remaining stock.
10. Add post hook or BAT launcher.
11. Add folder watcher and idempotent import.
12. Connect measured G54/DPRNT record.
13. Add production UI and audit log.

Сначала нужно доказать совпадение координат на одной реальной Cimatron operation. Только после этого добавлять автоматический watcher и массовое использование.

## 20. Импорт текущей системы координат со станка через DPRNT

### 20.1. Назначение

После установки детали оператор или setupist измеряет рабочий ноль на реальном станке. Контроллер передаёт фактическое положение рабочего смещения через `DPRNT`/`GPRINT`. Meimad должен принять это сообщение, проверить его принадлежность к правильной Operation и Machine и сохранить новую версию measured Work CS.

Этот поток не заменяет Cimatron UCS. Он добавляет к Cimatron package фактический machine offset:

```text
Cimatron procedure UCS
      = геометрия и NC work coordinates

Machine DPRNT/GPRINT G54
      = фактическое measured положение на конкретном станке
```

### 20.2. Рекомендуемый формат сообщения

Для каждого offset передавать одно атомарное сообщение с началом и концом:

```text
MEIMAD/OFFSET_START;machine=HAAS_VF3_01;program=O1234;operation=010;release=abc123;offset=G54
MEIMAD/OFFSET;offset=G54;x=125.402;y=-80.016;z=42.691;a=0.000;b=0.000;c=90.000;units=mm
MEIMAD/OFFSET_END
```

Для более короткой передачи допустим одно line-сообщение:

```text
MEIMAD/OFFSET;machine=HAAS_VF3_01;program=O1234;operation=010;release=abc123;offset=G54;x=125.402;y=-80.016;z=42.691;a=0;b=0;c=90;units=mm
```

Предпочтителен многострочный вариант: незавершённая передача не будет сохранена как valid offset.

### 20.3. Обязательные поля

```text
machine       стабильный Meimad Machine ID
program       O-number или другой program identity
operation     номер Case Operation
release       NC release id или program hash
offset        G54, G55, G54.1 Pn, G154 Pn и т.п.
x/y/z         измеренное положение в mm
a/b/c         rotary/work orientation, если используется
units         mm или inch, предпочтительно mm
sequence      уникальный номер сообщения/сессии
```

Нельзя связывать offset только по имени `G54`: один и тот же G54 на разных машинах и разных установках — разные физические данные.

### 20.4. Идентификация операции

В NC-файл, который Cimatron exporter создаёт из package, нужно добавить canonical Meimad header:

```text
(MEIMAD/PART: PART-123)
(MEIMAD/OPERATION: 010)
(MEIMAD/MACHINE: HAAS_VF3_01)
(MEIMAD/NC_RELEASE: abc123)
```

Если контроллер не передаёт эти комментарии через DPRNT, Offset Loader должен передавать их как параметры в сообщении. Meimad Server принимает offset только если найдено однозначное совпадение:

```text
machine + program/release + operation + offset
```

При неоднозначном совпадении запись блокируется и помещается в `Unmatched machine evidence`, а не применяется автоматически.

### 20.5. Haas NGC

Для Haas NGC предпочтительный путь:

1. Offset Loader или проверочный macro читает нужные work offset values.
2. Macro формирует `MEIMAD/OFFSET_START`.
3. Macro выводит значения через `DPRNT`.
4. Macro выводит `MEIMAD/OFFSET_END`.
5. Haas DPRNT TCP reader принимает строки.
6. Server валидирует и сохраняет offset.

Если контроллер не может передать все значения одним сообщением, передача должна иметь session id:

```text
MEIMAD/OFFSET_START;session=20261012-00017;offset=G54
MEIMAD/OFFSET_VALUE;session=20261012-00017;axis=X;value=125.402
MEIMAD/OFFSET_VALUE;session=20261012-00017;axis=Y;value=-80.016
MEIMAD/OFFSET_VALUE;session=20261012-00017;axis=Z;value=42.691
MEIMAD/OFFSET_END;session=20261012-00017
```

### 20.6. Fanuc

Для Fanuc, где DPRNT идёт через RS-232, использовать существующую стратегию serial-to-LAN bridge или локальный serial reader:

```text
Fanuc RS-232
    → RS232-to-LAN converter
    → Meimad DPRINT/DPRNT reader
    → Server offset importer
```

Если основной NC загружается через FTP/DNC, DPRNT-блоки могут оставаться в маленькой memory main program до и после вызова большого файла. Offset importer должен принимать только complete framed messages.

### 20.7. Mazak Matrix

Для Mazak Matrix 2 использовать serial bridge или file-based DPRNT source, например controller-created `print.txt` на network share. File reader должен:

- читать только новые строки;
- распознавать `MEIMAD/OFFSET_START` и `MEIMAD/OFFSET_END`;
- не применять неполную сессию;
- после успешного чтения очищать только подтверждённый consumed range;
- не удалять строку, добавленную одновременно во время чтения.

### 20.8. Reader protocol

Reader должен иметь общий интерфейс независимо от транспорта:

```csharp
public interface IDprntSource
{
    IAsyncEnumerable<string> ReadLinesAsync(
        CancellationToken cancellationToken);
}

public interface IOffsetEvidenceParser
{
    OffsetEvidenceParseResult Accept(string line);
}
```

Реализации транспорта:

```text
TcpDprntSource       Haas TCP или serial-to-LAN bridge
FileDprntSource      Mazak/Fanuc print file
SerialDprntSource    локальный COM port, если используется напрямую
```

Парсер должен быть общим для всех источников. Источник транспорта не должен влиять на формат сообщения.

### 20.9. Parser state machine

```text
Idle
  → Started(session)
  → Collecting(values)
  → Ended
  → Validated
  → Persisted
```

Ошибочные переходы:

```text
Value without Start       reject
End without Start         reject
Duplicate axis            reject or mark conflict
Missing required axis     keep pending, do not persist
Wrong machine             reject as unmatched
Old release               reject as stale
Invalid number/units      reject
```

### 20.10. Validation

Перед сохранением проверить:

- `machine` существует и активен;
- `operation` существует;
- `program/release` совпадает с выбранным release;
- offset name поддерживается dialect;
- все обязательные оси присутствуют;
- значения конечны и находятся в разумном диапазоне;
- units явно указаны;
- rotary values имеют правильную convention;
- timestamp и sequence не старше последней принятой записи;
- сообщение не является повтором.

Дополнительно можно сравнить новый offset с предыдущим:

```text
ΔX, ΔY, ΔZ > configured threshold
    → status = REVIEW_REQUIRED
```

Большое изменение не должно автоматически удаляться. Оно сохраняется как candidate с предупреждением.

### 20.11. Persistence model

Хранить append-only history, а не только последнее значение:

```text
machine_work_offsets
  id
  machine_id
  case_operation_id
  program_release_id
  work_offset
  x_mm, y_mm, z_mm
  a_deg, b_deg, c_deg
  units
  source
  source_session
  raw_message_hash
  measured_at
  received_at
  received_by
  status
  supersedes_id
```

`status`:

```text
VALID
REVIEW_REQUIRED
REJECTED
SUPERSEDED
UNMATCHED
```

Текущим считается последний `VALID` offset для конкретного:

```text
Machine + Operation + NC release + Work offset
```

### 20.12. API Meimad

Пример read endpoint:

```http
GET /api/v1/machines/{machineId}/operations/{operationId}/work-offsets?offset=G54
```

Пример ingest endpoint для reader:

```http
POST /api/v1/machine-evidence/work-offset
Content-Type: application/json
```

```json
{
  "machineId": "HAAS_VF3_01",
  "operationNumber": "010",
  "program": "O1234",
  "releaseId": "abc123",
  "workOffset": "G54",
  "positionMm": { "x": 125.402, "y": -80.016, "z": 42.691 },
  "rotationDeg": { "a": 0, "b": 0, "c": 90 },
  "source": "DPRNT_TCP",
  "sourceSession": "20261012-00017",
  "rawMessageHash": "..."
}
```

Endpoint должен быть idempotent по `rawMessageHash`/`sourceSession`. Повторный poll не должен создавать вторую запись.

### 20.13. Связь с NC Viewer

После сохранения valid offset NC Viewer должен:

1. загрузить последний valid Work offset для Operation/Machine;
2. показать источник и время измерения;
3. применить offset к machine frame;
4. показать предупреждение, если offset отсутствует;
5. показать предупреждение, если offset относится к другому NC release;
6. позволить открыть историю измерений;
7. не заменять Cimatron Work CS.

В UI нужно явно показывать два значения:

```text
Cimatron / NC Work CS: design coordinate definition
Machine / G54 measured: physical machine offset
```

### 20.14. Ошибки и безопасность

Не принимать автоматически:

- offset без machine id;
- offset без operation/release identity;
- неполную DPRNT session;
- значение из неизвестного TCP source;
- значение за пределами допустимого диапазона;
- старое значение после нового release;
- сообщение с повторно использованным session id и другим содержимым.

Каждая принятая запись должна иметь raw hash и timestamp. Это позволяет доказать, какое именно сообщение станок передал Meimad.

### 20.15. Acceptance tests

1. Haas передаёт полный G54 frame — Meimad сохраняет VALID.
2. Haas обрывает transmission до END — запись не сохраняется.
3. Повторная передача того же session/hash — duplicate ignored.
4. Fanuc передаёт сообщение через serial-to-LAN — результат равен Haas parser result.
5. Mazak добавляет строки в print file во время чтения — новые строки не теряются.
6. Сообщение относится к другому Machine — UNMATCHED.
7. Сообщение относится к старому release — STALE/REVIEW_REQUIRED.
8. Большое изменение X/Y/Z — candidate сохраняется, но не становится current автоматически.
9. NC Viewer показывает отдельно Cimatron UCS и measured G54.
10. После перезапуска Server последнее VALID значение восстанавливается.

## 21. Изменения в текущей интеграции

Добавить следующие компоненты:

```text
Server/Application/MachineEvidence/
  DprntLineNormalizer.cs
  OffsetEvidenceParser.cs
  WorkOffsetIngestService.cs
  WorkOffsetRepository.cs

Server/Infrastructure/MachineSources/
  TcpDprntSource.cs
  FileDprntSource.cs
  SerialDprntSource.cs

Client-Windows/NcViewer/
  MachineWorkOffsetCard
  WorkOffsetHistoryDialog
  OffsetSourceBadge
```

Существующие Haas DPRNT reader, Mazak FILE source и source configuration нужно расширить новым типом `WORK_OFFSET`. Existing PartName/OffsetLoader events должны продолжить работать без изменений.

## 22. Рекомендуемый порядок реализации

1. Зафиксировать message grammar и identity fields.
2. Реализовать parser с unit tests без подключения к станку.
3. Реализовать `TcpDprntSource` для Haas.
4. Добавить ingest endpoint и append-only persistence.
5. Подключить sample macro/DPRNT на Haas.
6. Добавить FileDprntSource для Mazak.
7. Добавить serial-to-LAN/Fanuc test fixture.
8. Подключить operation/release matching.
9. Добавить UI current/history/review.
10. Подключить NC Viewer machine frame.
11. Провести end-to-end test на Haas VF-3 и Mazak.

Итоговый результат: Cimatron передаёт design package, станок передаёт measured offset, а Meimad объединяет их только по Machine + Operation + NC release и сохраняет полную историю измерений.
