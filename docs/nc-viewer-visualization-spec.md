# NC Viewer — визуальная спецификация

## Цель

Пользователь должен сразу понимать, где находится инструмент, что уже обработано, что будет обработано дальше, какой инструмент активен и есть ли столкновение. Визуализация является отдельным слоем над simulation core.

## Toolpath visibility

В sidebar добавить `Toolpath visibility`:

```text
Hidden                 скрыть весь toolpath
Current tool           только траектории активного T-number
Passed                 только уже выполненные движения
Remaining              только ещё не выполненные движения
Passed + remaining     весь путь разными цветами
Selected tool          выбранный инструмент независимо от playback
Selected operation     путь выбранной операции
All                    весь toolpath
```

Рекомендуемые цвета:

```text
Passed                 зелёно-синий или серый
Remaining              жёлтый/оранжевый
Current move           белый или ярко-жёлтый
Rapid                  пунктирный голубой
Cutting feed           цвет текущего инструмента
Collision              красный
Selected tool          фиолетовый highlight
```

Parser один раз создаёт массив сегментов:

```ts
type RenderedSegment = {
  row: number;
  operationId?: string;
  toolNumber?: number;
  start: Vec3;
  end: Vec3;
  moveKind: "rapid" | "cut" | "arc";
  playbackStart: number;
  playbackEnd: number;
  collision?: boolean;
};
```

Переключение режима меняет только visibility/color buffers и не запускает повторный NC parse или material simulation.

## Shaded scene

Добавить независимые флаги:

```text
Tool
Holder
Stock
Machined stock
Finished part
Fixture
Machine
Axes and limits
Collision markers
```

Рекомендуемый вид:

```text
Finished part   solid shaded, 25–50% opacity при видимом stock
Stock           semi-transparent или скрыт при финальной проверке
Fixture         тёмно-серый opaque
Machine         серый металлический shaded
Tool            светлый металл
Holder          более тёмный металл
Collision       красный emissive overlay
```

Режимы камеры: `ISO`, `Top`, `Front`, `Right`, `Machine view`, `Fit all`. Добавить orbit, pan, zoom, `Follow tool` и `Follow camera`.

## 3D-модель станка

Модель должна быть иерархией кинематических узлов:

```text
Machine root
 ├── Base
 ├── Table / Rotary table
 │    ├── C axis
 │    └── B or A axis
 ├── Column
 ├── Head / Spindle
 │    ├── Spindle nose
 │    ├── Holder
 │    └── Tool
 └── Fixture / Workpiece
```

Каждый STL/STEP mesh прикрепляется к node id и получает transform от machine kinematics. Не объединять движущиеся части в одну STL.

Пример machine definition:

```json
{
  "machineId": "haas-umc-500",
  "units": "mm",
  "nodes": [
    { "id": "base", "mesh": "base.stl", "parent": null },
    { "id": "table", "mesh": "table.stl", "parent": "base" },
    { "id": "c-axis", "mesh": "c-axis.stl", "parent": "table", "joint": "C" },
    { "id": "b-axis", "mesh": "b-axis.stl", "parent": "c-axis", "joint": "B" },
    { "id": "spindle", "mesh": "spindle.stl", "parent": "base", "joint": "XYZ" }
  ]
}
```

Static parts загружаются один раз. При playback меняются только transforms кинематических узлов.

## Scene groups

```text
StaticScene       base, column, enclosure, fixed fixture
KinematicScene    table, rotary axes, spindle, holder, tool
StockScene        dirty voxel chunks and finished model
ToolpathScene     GPU line buffers
OverlayScene      axes, labels, collision markers, selected row
```

Для длинных программ нельзя создавать отдельный 3D object для каждого NC-блока. Toolpath должен храниться в line buffers, разбитых по operation/tool/move kind.

## Playback synchronization

Один `playbackPosition` управляет одновременно:

- current machine pose;
- current tool pose;
- passed/remaining toolpath;
- stock state;
- collision markers;
- NC row selection.

Seek назад выполняется из ближайшего stock checkpoint, а не всегда пересчётом от начала. Например checkpoint создаётся каждые 500–2000 executed motions.

```ts
type StockCheckpoint = {
  row: number;
  stockBlocks: SerializedBlock[];
  machinePose: MachinePose;
  activeTool: number;
  modalState: ModalState;
};
```

## Rendering quality

Минимальный shaded renderer должен иметь:

- нормали STL;
- directional key light;
- ambient fill light;
- anti-aliasing;
- ambient occlusion или screen-space approximation;
- transparency для stock/finished part;
- material для machine, fixture, holder и tool;
- red overlay для collision;
- optional wireframe/edge overlay.

## UI controls

```text
Toolpath: [Hidden ▼]
Tool filter: [All tools ▼]
Show rapid moves: [on/off]
Show machine: [on/off]
Show fixture: [on/off]
Show stock: [on/off]
Show finished part: [on/off]
Show collision markers: [on/off]
View: [Shaded / Wireframe / X-ray]
Camera: [ISO ▼]
Fit all
```

Playback bar также показывает current row, current tool, machine axes, collision count, gouge count и remaining stock amount.

## Import validation

Для STL/STEP проверять units, bounds, normals, triangle count, node id, parent node и transform при нулевой позе. STEP можно tessellate вне renderer и сохранить mesh cache.

Если node id не найден, mesh можно показать в preview, но нельзя включать его в animation или collision до исправления привязки.

## Критерии готовности

- toolpath можно полностью скрыть;
- можно показать только passed, remaining, current tool или весь путь;
- toolpath, stock и machine pose синхронизированы;
- 3D-модель станка движется по machine kinematics;
- machine, fixture, stock и finished part включаются независимо;
- есть shaded, wireframe и X-ray режимы;
- collision видна красным и связана с NC row;
- visibility changes не запускают повторный material simulation;
- длинная программа не создаёт тысячи отдельных scene objects;
- проверены реальные Haas и Mazak программы в WebView2.

В документации репозитория уже описаны mesh-и на кинематических узлах и отдельные machine/fixture/stock сцены. Но реальное качество отображения нужно проверить в WebView2 на настоящей программе и STL-моделях: наличие архитектурного кода ещё не доказывает, что финальный shaded view работает правильно.

## Dashboard view

Основной экран для оператора должен открываться в компактном dashboard-режиме. Технические таблицы, координаты и подробные сообщения не должны занимать главный экран.

### Основная карточка операции

```text
┌──────────────────────────────────────────────────────────────┐
│  PART IMAGE                         PART NUMBER               │
│  [large preview]                    ABC-12345                 │
│                                      OPERATION 010             │
│                                      MACHINE Haas UMC-500      │
│                                      STATUS  Ready / Running   │
└──────────────────────────────────────────────────────────────┘
```

Показывать крупно:

- Part Number;
- картинку детали;
- Operation number/name;
- Machine name;
- текущий статус;
- активный tool number;
- один главный результат: `Ready`, `Running`, `Collision`, `Completed` или `Error`.

### Источник картинки

Приоритет источников:

1. preview image Case;
2. finished-part STL thumbnail из Cimatron package;
3. generated screenshot из NC Viewer;
4. нейтральный placeholder, если изображения нет.

Изображение детали не должно зависеть от material-removal расчёта. Thumbnail можно построить один раз при импорте и сохранить в Case/Operation cache.

### Dashboard buttons

Основные действия показывать icon buttons с tooltip:

```text
▶ Play / Pause
⏮ Reset
⏭ Single step
⌖ Follow tool
▣ Toolpath visibility
◈ Machine view
⚠ Collisions
▤ Details
```

Не использовать длинные текстовые кнопки, если действие можно однозначно представить иконкой. Tooltip появляется при наведении и содержит текстовое объяснение.

### Вторичный information strip

Внизу карточки показывать только компактные показатели:

```text
Tool T12     Row N1840     Progress 62%     Collisions 0     Gouges 0
```

Подробные machine coordinates, tool dimensions, offsets и parser messages открываются через `Details`.

### Статусы

```text
Ready       синий/нейтральный
Running     зелёный
Paused      жёлтый
Collision   красный
Gouge       красный
Completed   зелёный
Error       красный с объяснением
```

Цвет не должен быть единственным индикатором: рядом всегда показывать короткий текст и иконку.

### Dashboard acceptance criteria

- оператор видит Part Number без открытия дополнительных окон;
- картинка детали занимает заметную область карточки;
- Operation и Machine видны рядом с изображением;
- основной статус понятен без чтения технических логов;
- toolpath, machine view и collision открываются иконками;
- подробные цифры остаются доступными, но не перегружают dashboard;
- карточка сохраняет работоспособность при отсутствии preview image;
- thumbnail не пересчитывается при каждом playback frame.

## TV Dashboard: картинка детали в строке станка

TV Dashboard уже использует одну компактную fullscreen-полосу на каждый display-enabled Machine. В текущей реализации показываются Machine, connection, current Part, Batch/Operation, Operation name, статус и progress bar; изображения деталей сейчас намеренно скрыты. Изменение должно расширить существующую GET-only projection, а не создавать отдельный dashboard.

### Компактная строка станка

```text
┌──────┬────────────────────┬──────────────┬──────────┬────────────┬─────────────┐
│ IMG  │ PART NUMBER        │ OPERATION    │ MACHINE  │ STATUS     │ PROGRESS    │
│ 96px │ ABC-12345          │ OP010 Rough  │ VF-3     │ RUNNING    │ 62%         │
└──────┴────────────────────┴──────────────┴──────────┴────────────┴─────────────┘
```

Показывать в каждой строке:

- thumbnail детали;
- Part Number крупным шрифтом;
- Case/part name при наличии;
- Operation number и короткое имя;
- Machine name;
- статус `RUNNING`, `PAUSED`, `WAITING`, `COMPLETED`, `IDLE` или `ERROR`;
- progress bar и calculated completion;
- маленькую иконку предупреждения при conflict/collision/error.

Технические координаты, полный tool list, raw DPRNT, длинные сообщения и историю событий на TV не показывать. Они остаются в Windows client и открываются по выбору строки.

### Источник thumbnail

Использовать следующий приоритет:

1. Case preview image;
2. thumbnail finished-part STL из Cimatron package;
3. cached screenshot/preview, созданный при импорте;
4. нейтральный placeholder с иконкой детали.

Server должен отдавать thumbnail как read-only image resource или URL с коротким cache lifetime. Не передавать полный STL в каждую TV-загрузку.

Пример projection:

```json
{
  "machineId": "VF3-01",
  "machineName": "Haas VF-3",
  "status": "RUNNING",
  "partNumber": "ABC-12345",
  "partName": "Housing",
  "operationNumber": "010",
  "operationName": "Roughing",
  "thumbnailUrl": "/api/v1/tv-dashboard/parts/ABC-12345/thumbnail",
  "thumbnailVersion": "sha256:...",
  "progress": 0.62,
  "estimatedFinish": "2026-10-12T08:45:00+03:00",
  "warning": null
}
```

### Поведение на TV

- строка должна оставаться читаемой на расстоянии;
- thumbnail не должен уменьшать Part Number до мелкого текста;
- при отсутствии текущей операции показывать `IDLE` и картинку последней назначенной детали только если это явно разрешено настройкой;
- при offline состоянии оставить последний snapshot и показать `OFFLINE`;
- обновлять status/progress часто, thumbnail — только при изменении `thumbnailVersion`;
- не загружать изображение заново на каждом refresh;
- не показывать внутренние пути файлов или технические IDs.

### Критерии готовности TV Dashboard

- в каждой активной строке видны thumbnail и Part Number;
- изображение соответствует текущей Operation, а не просто последней Case;
- смена Operation меняет thumbnail атомарно вместе с Part Number;
- при отсутствии картинки используется понятный placeholder;
- TV Dashboard не получает полный STL;
- thumbnail кэшируется по `thumbnailVersion`;
- status/progress продолжают обновляться независимо от изображения;
- существующий read-only/offline/ETag workflow сохраняется.
