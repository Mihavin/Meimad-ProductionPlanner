# NC Viewer: собственный движок снятия материала и верификации

## 1. Цель

Заменить текущий движок снятия материала в NC Viewer на собственное 3D-ядро без коммерческих лицензий.

Новая система должна:

- моделировать stock как настоящий 3D-объём, а не как набор вертикальных столбцов;
- поддерживать параметрические инструменты без создания подробных CAD-моделей пластин;
- работать с 3-осевыми, 3+2 и simultaneous 5-axis движениями;
- показывать снятие материала плавно и с приемлемой скоростью;
- экспортировать полученный stock в STL;
- проверять столкновения инструмента, holder, шпинделя и fixture;
- находить gouge, остаточный материал, rapid-through-stock и выход за пределы станка;
- работать локально в Windows/WebView2 без внешнего сервиса и платной лицензии.

Цель проекта — полезная производственная верификация программы, а не полная замена VERICUT.

## 2. Ограничения принятого концепта

Инструменты описываются параметрами и простыми геометрическими примитивами. Не требуется:

- импортировать CAD-модель каждой пластины;
- моделировать реальную форму стружколома;
- учитывать микрогеометрию режущей кромки;
- строить точную 3D-модель производителя инструмента;
- рассчитывать силы резания, вибрации или износ.

Для первого производственного варианта достаточно следующих примитивов:

- flat end mill — цилиндр;
- ball mill — цилиндр с полусферическим концом;
- bull-nose mill — цилиндр с радиусом на конце;
- tapered/chamfer tool — конус или усечённый конус;
- drill/spot drill — цилиндр с конической вершиной;
- face mill — диск или цилиндр;
- holder, extension, shank, spindle nose — цилиндры, конусы и диски.

Режущая часть используется для снятия материала. Полная сборка инструмента используется для collision checking.

## 3. Что уже можно переиспользовать в Meimad

В текущем Windows-клиенте уже есть параметрическое описание инструмента `ToolShapeGeometry` и сегменты типа `HOLDER`, `CYLINDER`, `CUTTER`, `BALL`, `POINT`, `CONE`, `DISC`, `BLADE`, `DOVETAIL` и `SPHERE`.

Также есть описание spindle-side компонентов: taper, flange, pull stud и gauge line.

Новый simulation core не должен создавать вторую независимую модель инструмента. Нужно добавить адаптер:

```text
Meimad ToolShapeGeometry
        ↓
SimulationToolProfile
        ├── CuttingProfile
        └── CollisionProfile
```

`CuttingProfile` содержит только режущие сегменты. `CollisionProfile` содержит всю сборку от tip до spindle nose.

## 4. Предлагаемая архитектура

```text
NC text
  ↓
Existing NC parser
  ↓
Canonical motion blocks
  ↓
Machine kinematics / work offsets
  ↓
Simulation engine
  ├── Stock model
  ├── Tool swept volume
  ├── Material removal
  ├── Collision detection
  ├── Deviation analysis
  └── Report/events
  ↓
Mesh renderer and NC Viewer UI
```

Ядро должно быть отделено от DOM, WebView и конкретного renderer. Оно должно запускаться:

1. в тестах без UI;
2. в CPU reference mode;
3. в Web Worker;
4. в GPU/WebGPU mode.

Веб-страница должна получать только снимки изменённых блоков, события и результаты проверок.

## 5. Модель движения

После разбора NC каждый motion block должен быть преобразован в каноническую структуру:

```ts
type SimulationMove = {
  row: number;
  kind: "rapid" | "feed" | "arc" | "dwell" | "tool-change";
  startMachine: MachinePose;
  endMachine: MachinePose;
  startWork: WorkPose;
  endWork: WorkPose;
  feedMmMin?: number;
  spindleRpm?: number;
  toolNumber?: number;
  workOffset?: string;
};

type WorkPose = {
  tip: Vec3;
  axisTowardSpindle: Vec3;
};
```

Все дальнейшие операции должны работать с `WorkPose`, а не напрямую с X/Y/Z/A/B/C словами.

Причина: для simultaneous 5-axis позиция инструмента и ориентация меняются одновременно. Конечные точки NC-блоков недостаточны.

## 6. Интерполяция simultaneous 5-axis

Для каждого motion block нужно построить промежуточные poses.

Позиция интерполируется линейно:

```text
P(t) = P0 + t × (P1 − P0)
```

Ориентация должна интерполироваться через нормализованные векторы или quaternion/slerp. Нельзя независимо интерполировать A, B и C во всех случаях: это может дать неверный путь около wrap-around и singularity.

Размер шага должен быть адаптивным. Минимальный критерий:

```text
stepLength ≤ min(
    cellSize × 0.5,
    toolDiameter × 0.25,
    allowedAngularError × localToolRadius
)
```

Шаг уменьшается, если:

- движение длинное;
- инструмент быстро меняет угол;
- диаметр инструмента маленький;
- инструмент находится рядом с fixture или finished part;
- включён режим точной проверки.

Для каждого промежуточного pose проверяются swept volume и collision. Нельзя проверять только начальную и конечную точки: инструмент может пересечь fixture между ними.

## 7. Представление stock

### 7.1. Voxel blocks

Stock хранится в 3D voxel grid, разбитой на блоки, например 8×8×8 или 16×16×16 voxels.

Каждый блок содержит:

```ts
type VoxelBlock = {
  key: bigint;             // координаты блока
  origin: Vec3;
  resolution: number;      // mm per voxel
  material: Uint8Array;    // active/material mask
  distance?: Float32Array; // optional SDF/narrow band
  dirty: boolean;
  meshVersion: number;
};
```

Пустые блоки не создаются. Это позволяет моделировать большую заготовку без выделения полной 3D-матрицы.

### 7.2. Материал

Для первого варианта достаточно бинарного состояния:

```text
1 = material exists
0 = material removed / outside stock
```

Позже можно добавить signed distance field для более гладкой поверхности, но SDF не должен быть обязательным для первой корректной версии.

### 7.3. Начальная заготовка

Поддержать:

- box;
- cylinder;
- imported STL;
- stock exported from previous operation.

Для STL нужно выполнить voxelization в work coordinate system с учётом Work CS и stock shift.

## 8. Параметрические инструменты

Внутренний профиль инструмента должен быть единым:

```ts
type ToolProfile = {
  id: string;
  cutting: ToolSegment[];
  collision: ToolSegment[];
  gaugeToTip: number;
};

type ToolSegment = {
  kind: "cylinder" | "cone" | "sphere" | "hemisphere" | "disc";
  z0: number;
  z1: number;
  radius0: number;
  radius1: number;
  cornerRadius?: number;
  cutting: boolean;
};
```

Профиль ориентирован вдоль локальной оси инструмента. При каждом pose он преобразуется в workpiece coordinates.

Для material removal разрешить только сегменты с `cutting=true`. Для collision использовать все сегменты.

## 9. Снятие материала

Для каждого адаптивного промежуточного pose:

1. найти bounding box swept tool;
2. найти пересекающиеся voxel blocks;
3. для каждого активного voxel вычислить расстояние до swept tool;
4. удалить voxel, если он находится внутри режущего объёма;
5. пометить изменённые blocks как dirty;
6. обновить material volume и removed volume;
7. передать dirty blocks на remesh.

Для прямого движения между `pose0` и `pose1` нельзя ограничиваться tool volume только в конечной точке. Использовать swept capsule/union of interpolated tool poses. На первом этапе допустима дискретизация с гарантированным максимальным расстоянием между poses.

## 10. GPU/WebGPU реализация

GPU-режим добавляется после CPU reference implementation.

### Compute pass

Один invocation обрабатывает один voxel или одну группу voxels:

```text
input: active blocks, tool pose samples, tool profile
output: material mask, dirty flag, collision flag
```

GPU не должен изменять UI напрямую. Compute shader пишет structured buffers, а renderer получает только готовые mesh chunks.

### WebGPU workflow

```text
CPU: select affected blocks
CPU: upload block metadata and poses
GPU: classify/remove voxels
GPU: mark dirty blocks
CPU/GPU: rebuild mesh for dirty blocks
UI: display mesh and events
```

Для первого варианта можно оставить Marching Cubes на CPU. После проверки корректности перенести remeshing на GPU, если именно оно станет узким местом.

## 11. Collision control

### 11.1. Collision types

Разделить результат на отдельные типы:

```text
AllowedCut        cutter intersects stock
HolderCollision   holder intersects stock or finished part
FixtureCollision  tool/holder intersects fixture
MachineCollision  assembly intersects machine component
Gouge             cutter removes finished part
RapidInMaterial   rapid move intersects material
AxisLimit         machine axis outside configured travel
```

Пересечение cutter со stock само по себе не является ошибкой.

### 11.2. Два уровня проверки

**Broad phase** — быстрые AABB/sphere tests. Отбрасывает большинство объектов.

**Narrow phase** — точная проверка примитивов или voxel/SDF intersection только для кандидатов.

Fixture и machine components можно начать с STL meshes. Holder и spindle остаются параметрическими.

### 11.3. Collision policy

Для каждого объекта задаётся policy:

```text
cutting tool vs stock       = allowed
holder vs stock             = error/warning
tool vs fixture              = error
holder vs fixture            = error
holder vs finished part      = warning/error
rapid vs stock               = warning/error
```

Политику не зашивать в geometry engine. Она должна быть настройкой операции или machine definition.

## 12. Finished-part verification

Если доступна finished STL:

- stock остаётся отдельной моделью;
- finished model используется для deviation analysis;
- материал внутри target model после обработки — gouge;
- материал снаружи target model — remaining stock;
- допускаются отдельные tolerance values для gouge и rest material.

Результат должен содержать:

```ts
type DeviationResult = {
  maxGouge: number;
  maxRemainingStock: number;
  gougeVoxelCount: number;
  remainingVoxelCount: number;
  firstGougeRow?: number;
  firstRemainingStockRow?: number;
};
```

Это отдельный режим и не должен замедлять обычное playback, если пользователь его не включил.

## 13. План реализации по этапам

### Stage 0 — закрепить контракт

Создать интерфейсы `SimulationMove`, `ToolProfile`, `StockDefinition`, `MachinePose`, `SimulationEvent`.

Результат:

- parser и simulation core не зависят друг от друга;
- текущий NC Viewer может временно использовать старый engine;
- есть JSON fixture для тестов.

### Stage 1 — CPU 3D reference engine

Реализовать:

- box/cylinder stock;
- sparse voxel blocks;
- flat, ball, bull-nose, taper, drill;
- straight 3-axis moves;
- adaptive subdivision;
- STL export;
- deterministic tests.

Критерий готовности: один и тот же NC-файл даёт одинаковый результат при каждом запуске.

### Stage 2 — tool adapter

Подключить существующий `ToolShapeGeometry` и преобразовать его в `ToolProfile`.

Проверить:

- gauge-to-tip length;
- tool diameter;
- holder length/diameter;
- extension and shank;
- missing dimensions and defaults.

Не подставлять молча неверные размеры: default values должны помечаться в диагностике.

### Stage 3 — 3+2 and simultaneous poses

Добавить:

- work offset transformation;
- machine-to-workpiece transformation;
- rotary axes;
- quaternion/vector orientation interpolation;
- adaptive pose sampling;
- tests for A, B and C rotations.

Критерий готовности: tool tip и tool axis совпадают с контрольными pose на каждом sample.

### Stage 4 — collision engine

Добавить:

- holder vs stock;
- tool/holder vs fixture STL;
- holder vs finished part;
- rapid-through-stock;
- machine travel limits;
- collision events with NC row and pose.

Каждое событие должно содержать row number, tool number, object id, position, axis direction и severity.

### Stage 5 — finished-part deviation

Добавить:

- target STL voxelization;
- gouge detection;
- remaining-stock detection;
- tolerance settings;
- heat-map or colored mesh;
- summary report.

### Stage 6 — WebGPU acceleration

Перенести на GPU сначала только material classification/removal. CPU reference engine оставить для:

- tests;
- low-resolution fallback;
- machines without WebGPU;
- comparison/debugging.

### Stage 7 — mesh and UI optimization

Добавить:

- dirty-block remeshing;
- progressive playback;
- pause/resume;
- run from selected row;
- single-tool filter;
- resolution selector;
- progress and cancellation;
- export machined stock.

## 14. Минимальный набор тестов

### Геометрия

- flat mill cuts a box;
- ball mill leaves spherical bottom;
- bull-nose leaves corner radius;
- taper removes sloped wall;
- drill leaves conical tip;
- tool from below removes material correctly;
- undercut does not disappear incorrectly.

### Движения

- short and long linear move give equivalent result;
- long move does not tunnel through stock;
- rapid move does not remove material;
- arc is tessellated within tolerance;
- 3+2 rotation produces expected tilted cut;
- simultaneous rotary movement does not skip material.

### Collisions

- cutter/stock intersection is allowed;
- holder/stock is reported;
- fixture collision is reported;
- rapid through stock is reported;
- collision row is correct;
- no duplicate event spam for one continuous collision.

### Regression

- current NC examples;
- Haas VF-3;
- Haas UMC-500;
- Mazak Variaxis;
- Fanuc-style program with arcs and subprograms;
- program with work offsets;
- program with tool changes and repeated tool numbers.

## 15. Что считать готовым

Первая usable version готова, если она:

- корректно симулирует 3-axis и 3+2;
- поддерживает flat, ball, bull-nose, taper и drill;
- не пропускает material removal на длинных движениях;
- показывает holder/fixture collisions;
- работает с параметрическими инструментами Meimad;
- экспортирует machined stock STL;
- сообщает NC row для каждой ошибки;
- имеет CPU fallback;
- проходит deterministic regression tests.

Simultaneous 5-axis можно выпускать как отдельную capability после прохождения специальных тестов на интерполяцию и адаптивный sampling.

## 16. Главные риски

1. **Пропуск материала между sample points.** Решение: ограничить шаг по длине и углу, добавить тесты на длинные движения.
2. **Неверная кинематика.** Решение: тестировать сначала work pose, затем machine pose и rotary axes.
3. **Слишком большая память.** Решение: sparse blocks, dirty regions и ограничение active volume.
4. **Медленный mesh rebuild.** Решение: chunked meshes и progressive updates.
5. **Ложные collision events.** Решение: broad phase, tolerance и hysteresis для событий.
6. **Расхождение CPU и GPU.** Решение: CPU reference engine использовать как oracle для маленьких тестовых сцен.
7. **Неверные tool defaults.** Решение: показывать источник каждого размера и явно помечать estimated values.

## 17. Рекомендуемый порядок работы программиста

Не начинать с WebGPU, красивого UI или полной 5-осевой кинематики. Порядок должен быть таким:

1. Контракт данных.
2. CPU 3D voxel reference.
3. Flat tool и прямое 3-axis движение.
4. Остальные параметрические профили.
5. ToolShapeGeometry adapter.
6. 3+2 transformation.
7. Adaptive simultaneous pose sampling.
8. Collision engine.
9. Finished-part deviation.
10. GPU compute.
11. UI optimization and production rollout.

Каждый этап должен иметь работающий тестовый результат и не ломать предыдущий этап.
