# MM1: техническая справка по MM.EXE, MAZEDATA.DTA, ROSTER.DTA и *.OVR

Документ описывает не весь формат Might and Magic I вообще, а то, что известно
на данный момент автору и уже реализовано в `Dream_MM_Map_Editor` для открытия
оверлеев, чтения карты, сопоставления блоков `MAZEDATA.DTA`, просмотра патчей,
поиска текстов, боев, телепортов, лута и эффектов партии.

Если ниже написано "известно на данный момент автору", это означает:
соответствующая информация извлечена из текущей реализации и реально участвует
в разборе. Если формат файла напрямую не парсится проектом, это отмечено
отдельно.

## 1. Обозначения

| Обозначение | Смысл |
| --- | --- |
| `file offset` | Смещение байта внутри файла `.OVR`, `MM.EXE`, `MAZEDATA.DTA` |
| `runtime address` | 16-битный адрес, который встречается в машинном коде игры |
| `S` | `StartAddress` оверлея: file offset начала объектной таблицы или текстовой области |
| `TextBaseAddr` | Разность `0xC972 - S`, используемая для перевода runtime text/data address в file offset |
| `packed coord` | Один байт координаты: `packed = (Y << 4) | X`, где `X` и `Y` от 0 до 15 |
| `patch key` | 16-битное значение из объектной таблицы, из которого считается адрес патча |
| `patch address` | Runtime/file address точки входа кода события: `(patchKey + 0x0B7F) & 0xFFFF` |

Локальные координаты клетки внутри текущего `.OVR` считаются сеткой 16x16.
Исключения ниже относятся не к клеткам этой сетки, а к другим координатным
системам: global `X/Y` для переходов между оверлеями, `SURFACE X/Y` metadata и
`MAP SECTOR`.

## 2. Общий порядок загрузки

1. Пользователь выбирает `.OVR`.
2. Рядом с ним ищутся companion-файлы `MAZEDATA.DTA` и `MM.EXE`.
3. Из `MAZEDATA.DTA` читаются два слоя статической карты для этого оверлея.
4. Соответствие "оверлей -> блок MAZEDATA" берется из таблицы имен оверлеев в `MM.EXE`.
5. Из заголовка и кода `.OVR` вычисляется `S`.
6. По `S` читаются служебные байты оверлея, объектная таблица и, если нужно, no-object-table режим.
7. Каждый patch address дизассемблируется и символически исполняется.
8. Дополнительно анализируется default path для клеток, не покрытых объектной таблицей.
9. Переходы между оверлеями обогащаются метаданными из `MM.EXE` и целевых `.OVR`.

## 3. Формат *.OVR

### 3.1. Заголовок

Известный на данный момент автору размер заголовка: `0x0E` байт.

| Offset | Размер | Назначение |
| --- | ---: | --- |
| `0x02` | word LE | Runtime load address первого кодового блока. Используется для перевода относительных `CALL/JMP` в runtime target. |
| `0x04` | word LE | Длина первого блока после заголовка. |
| `0x06` | word LE | Runtime load address второго блока. |
| `0x0E` | bytes | Начало первого блока. |
| `0x0E + [0x04]` | bytes | Начало второго блока в файле. |

Поля `0x00`, `0x08..0x0D` анализатором как самостоятельные структурные поля
не используются.

Формулы:

```text
overlayHeaderSize      = 0x0E
firstBlockFileOffset   = 0x0E
firstBlockLength       = u16le(file[0x04])
secondBlockFileOffset  = 0x0E + firstBlockLength
secondBlockLoadAddress = u16le(file[0x06])
codeLoadAddress        = u16le(file[0x02])
```

Для относительного `CALL` внутри файла:

```text
fileTarget     = callFileOffset + 3 + signed16(relative)
runtimeOffset  = fileTarget - 0x0E
runtimeTarget  = codeLoadAddress + runtimeOffset
```

### 3.2. Определение `StartAddress` (`S`)

Стандартный путь:

1. Проверяется, что файл не меньше `0x0E`.
2. Читается `firstBlockLength` и `secondBlockLoadAddress`.
3. `secondBlockFileOffset = 0x0E + firstBlockLength`.
4. В позиции `0x2E` ожидается инструкция:

```text
3A 1E ll hh    cmp bl, byte ptr [hhhhll]
```

5. `countMemoryAddress = u16le(file[0x30])`.
6. `S = secondBlockFileOffset + (countMemoryAddress - secondBlockLoadAddress)`.

Если инструкция в `0x2E` не найдена, анализатор допускает no-object-table
оверлей. Тогда он пробует сопоставить runtime address `0xC972` со вторым
блоком:

```text
S = secondBlockFileOffset + (0xC972 - secondBlockLoadAddress)
```

Такой `S` принимается только если байт по этому offset похож на начало текста:
`0x00`, CR, LF или ASCII `0x20..0x7E`.

### 3.3. Перевод runtime address в file offset

После определения `S`:

```text
TextBaseAddr = 0xC972 - S
fileOffset   = runtimeAddress - TextBaseAddr
             = S + (runtimeAddress - 0xC972)
```

Эта формула используется для чтения overlay-текстов, таблиц монстров и
постоянных байтов оверлея, если runtime address попадает в файл.

### 3.4. Служебная область перед `S`

Большая часть параметров оверлея лежит перед объектной таблицей. В таблице
показаны file offsets относительно `S` и соответствующие runtime addresses
при стандартной базе `0xC972`.

| File offset | Runtime | Назначение |
| ---: | ---: | --- |
| `S - 0x32` | `0xC940` | `OverlayId` / `MapId`; бит `0x80` означает outdoor overlay. |
| `S - 0x31` | `0xC941` | Первый байт key для структуры сторон `bits=01`. |
| `S - 0x30` | `0xC942` | Второй байт key для структуры сторон `bits=01`. |
| `S - 0x2F` | `0xC943` | Первый байт key для структуры сторон `bits=10`. |
| `S - 0x2E` | `0xC944` | Второй байт key для структуры сторон `bits=10`. |
| `S - 0x2D` | `0xC945` | Первый байт key для структуры сторон `bits=11`. |
| `S - 0x2C` | `0xC946` | Второй байт key для структуры сторон `bits=11`. |
| `S - 0x2B` | `0xC947` | `tailByte` семейства описания сторон. |
| `S - 0x2A..S - 0x1F` | `0xC948..0xC953` | trailing bytes записи сторон, сейчас используются для идентификации/диагностики. |
| `S - 0x1B` | `0xC957` | Самая безопасная клетка: два байта `X`, `Y`, берутся нижние 4 бита. |
| `S - 0x18` | `0xC95A` | Самая опасная клетка: два байта `X`, `Y`, берутся нижние 4 бита. |
| `S - 0x15` | `0xC95D` | Raw шанс случайной встречи. |
| `S - 0x11` | `0xC961` | Максимальная сила случайных монстров. |
| `S - 0x10` | `0xC962` | Максимальное количество монстров в группе случайной встречи. |
| `S - 0x0F` | `0xC963` | Буква `MAP SECTOR`, закодирована как `0xC1 -> A`. |
| `S - 0x0E` | `0xC964` | Цифра `MAP SECTOR`, закодирована как `0xB1 -> 1`. |
| `S - 0x08` | `0xC96A` | `SURFACE X` для indoor overlay. |
| `S - 0x07` | `0xC96B` | `SURFACE Y` для indoor overlay. |
| `S - 0x04` | `0xC96E` | Map flags: бит `0x01` darkness, бит `0x02` teleport spell disabled. |
| `S - 0x03` | `0xC96F` | Максимальный уровень случайных монстров. |

Расчет процента случайной встречи:

```text
if raw == 0x00 or raw == 0xFF: chance = 0
else chance = (256 - raw) * 100 / 256
```

### 3.5. Object table

Если у оверлея есть объектная таблица, базовая структура такая:

```text
S + 0                    byte objectCount
S + 1                    objectCount bytes packed coordinates
S + 1 + N                objectCount bytes direction bytes
S + 1 + 2*N              objectCount words patch keys, little endian
```

Координата объекта:

```text
packed = file[coordinatesOffset + i]
X = packed & 0x0F
Y = packed >> 4
```

Patch address:

```text
patchAddress = (patchKey + 0x0B7F) & 0xFFFF
```

Direction byte у объекта хранит направления, в которых есть сообщение. Битовые
пары считаются установленными только когда оба бита пары равны 1:

| Pair index | Mask | Direction |
| ---: | ---: | --- |
| 0 | `0x03` | Left |
| 1 | `0x0C` | Bottom |
| 2 | `0x30` | Right |
| 3 | `0xC0` | Top |

Анализатор не всегда доверяет fallback-раскладке. Он умеет найти реальные
смещения таблиц по коду первого блока:

1. Первый блок: `file[0x0E .. 0x0E + firstBlockLength)`.
2. Вычисляется runtime address count-байта:

```text
countMemoryAddress = secondBlockLoadAddress + (S - firstBlockEnd)
```

3. В первом блоке ищется `3A 1E countLow countHigh`.
4. Перед ним в окне `0x20` байт ищется `3A 87 disp16` для таблицы координат.
5. После него ищется `22 87 disp16` для таблицы направлений.
6. После него ищется `8B BF disp16` для таблицы patch keys.
7. Каждый `disp16` переводится во file offset:

```text
fileOffset = firstBlockEnd + (loadedAddress - secondBlockLoadAddress)
```

8. Если найденные области валидны, используется detected layout. Если области
перекрываются, реальное число объектов ограничивается минимальным span между
`coordinates`, `directions` и `patchKeys`.

### 3.6. No-object-table overlay

Если object table не найдена, `HasObjectTable = false`.

Для такого оверлея:

```text
objectCount = 0
countOffset = coordinatesOffset = directionsOffset = patchKeysOffset = S
defaultPathStart = 0x20
```

### 3.7. Default path

Default path анализируется для клеток, не покрытых объектной таблицей, а также
для macro/static-map развилок.

Стандартный default path начинается с `0x34`, если по адресу `0x20` найден
пролог диспетчера объектов. Проверяемые байты пролога:

```text
0x20: A0 3A 3C BB 00 00 3A 87 ?? ?? 74 ?? FE C3 3A 1E ?? ?? 72 F2
```

Если пролог не найден, default path начинается с `0x20`.

Для анализа default path по клетке в регистры закладывается:

```text
BL = X
BH = 0
BX = X
```

Runtime-координаты при чтении из памяти:

| Runtime | Назначение |
| ---: | --- |
| `0x3C38` | текущая или целевая `X` |
| `0x3C39` | текущая или целевая `Y` |
| `0x3C3A` | packed coordinate `(Y << 4) | X` |

## 4. MAZEDATA.DTA

`MAZEDATA.DTA` состоит из блоков по `512` байт:

```text
16 * 16 bytes first layer
16 * 16 bytes second layer
```

Размер файла должен быть кратен `512`. Число блоков:

```text
blockCount = fileLength / 512
```

Порядок блоков сопоставляется с оверлеями через таблицу имен в `MM.EXE`:

1. Анализатор ищет последовательности zero-terminated ASCII-имен.
2. Имя должно быть длиной `3..8`, первый символ `a..z`, остальные `a..z` или `0..9`.
3. Берется run, где есть имя выбранного оверлея и минимум `blockCount` имен.
4. Индекс имени в run равен индексу блока карты.

Для выбранного overlay:

```text
blockOffset = overlayIndex * 512
firstLayer  = file[blockOffset .. blockOffset + 255]
secondLayer = file[blockOffset + 256 .. blockOffset + 511]
```

Каждый слой форматируется как 16 строк по 16 hex-байт.

### 4.1. Runtime-проекция слоев

При символическом исполнении статическая карта проецируется в runtime memory:

| Runtime base | Размер | Слой |
| ---: | ---: | --- |
| `0x3CFA` | `0x100` | first layer |
| `0x3DFA` | `0x100` | second layer |

Чтение:

```text
offset = runtimeAddress - layerBase
X = offset % 16
Y = offset / 16
value = layer[Y][X]
```

### 4.2. Биты сторон клетки

Для каждой клетки используются пары битов first layer и second layer. Номера
битов ниже 1-based "справа налево", как в коде анализатора.

| Direction | High bit | Low bit |
| --- | ---: | ---: |
| Left | 2 | 1 |
| Bottom | 4 | 3 |
| Right | 6 | 5 |
| Top | 8 | 7 |

Для направления:

```text
hasDoorBit  = bit(firstLayer, highBit)
hasWallBit  = bit(firstLayer, lowBit)
structureBits = (hasWallBit ? 1 : 0) | (hasDoorBit ? 2 : 0)

secondHighBit = bit(secondLayer, highBit)
secondLowBit  = bit(secondLayer, lowBit)
```

`structureBits` выбирает одно из трех описаний сторон из записи оверлея:

| `structureBits` | Смысл |
| ---: | --- |
| `0x00` | Нет структуры. Если `secondLowBit` установлен, показывается "Барьер". |
| `0x01` | Ключ `bit01Key` из side layout record. |
| `0x02` | Ключ `bit10Key` из side layout record. |
| `0x03` | Ключ `bit11Key` из side layout record. |

Известные типы проходов:

| Значение | Тип |
| ---: | --- |
| `0` | Нет прохода |
| `1` | Door |
| `2` | Grate |
| `3` | Secret passage |
| `8` | Rough terrain |
| `9` | Door2 |
| `10` | Grate2 |

Особые правила:

- Для secret wall `PassageType = 3`; если `secondLowBit` установлен, проход подавляется.
- Для door/grate `secondLowBit` означает closed state.
- Для water/desert/swamp отсутствие прохода превращается в rough terrain `8`.
- Если структура есть, `PassageType = 0`, `secondLowBit` не установлен, анализатор считает это implicit secret passage.

### 4.3. Биты всей клетки во втором слое

Помимо сторон, second layer задает некоторые свойства клетки:

| Bit справа | Маска | Назначение |
| ---: | ---: | --- |
| 2 | `0x02` | No magic |
| 4 | `0x08` | Dangerous cell |
| 6 | `0x20` | Dark cell |
| 8 | `0x80` | Random encounter central option |

## 5. MM.EXE

Анализатор использует `MM.EXE` тремя способами.

### 5.1. Сопоставление MAZEDATA и оверлеев

Для выбора блока `MAZEDATA.DTA` выполняется generic scan по zero-terminated
именам оверлеев, описанный в разделе 4.

### 5.2. Таблица переходов между оверлеями

Для enrichment переходов анализатор читает фиксированные таблицы `MM.EXE`:

| Offset в MM.EXE | Размер | Назначение |
| ---: | ---: | --- |
| `0x10B2B` | `55 * 2` | Таблица глобальных координат: пары `globalX`, `globalY`. |
| `0x10B99` | `55 * 2` | Таблица word-указателей на имена. |
| `0x109B0` | base | База строк имен оверлеев. |

Диапазоны записей по selector:

| Map selector | Start index | Count |
| ---: | ---: | ---: |
| `1` | `0` | `14` |
| `2` | `14` | `20` |
| `3` | `34` | `21` |

Чтение имени:

```text
namePointer = u16le(file[0x10B99 + index*2])
nameOffset  = 0x109B0 + namePointer
name        = zero-terminated ASCII a-z/0-9
overlayName = uppercase(name) + ".OVR"
```

Ключ таблицы:

```text
globalX:globalY:mapSelector -> overlayName
```

Для outdoor selector `2` есть fallback:

```text
letterIndex = globalX / 8       // 0..4 -> A..E
digit       = globalY / 8 + 1   // 1..4
overlayName = "AREA" + letter + digit + ".OVR"
```

### 5.3. Resident data

Для чтения resident text/data внутри `MM.EXE` анализатор сначала находит file
base resident-образа:

1. Ищет ASCII anchor `"ON THIS STONE STATUE OF "`.
2. Этот текст должен соответствовать runtime address `0x1296`.
3. `residentDataFileBase = anchorFileOffset - 0x1296`.
4. Проверяет validation anchor `"A HUMAN KNIGHT"` по runtime address `0x12D1`.
5. После этого любой resident runtime address переводится так:

```text
fileOffset = residentDataFileBase + runtimeAddress
```

Известные resident routine/data addresses:

| Runtime | Назначение |
| ---: | --- |
| `0x32EB` | Sorpigal statue resident handler |
| `0xCD52` | Индекс статуи Sorpigal |
| `0x1296` | Prefix text статуй |
| `0x12C1` | Таблица имен статуй |
| `0x12AF` | Plaque intro text |
| `0x0E4F` | Plaque table |
| `0x2D15` | Equipment shop handler |
| `0x2972` | Food shop handler |
| `0x30C3` | Tavern handler |
| `0x1538` | Shop inventory pointer table |
| `0x13D6` | Food price table |
| `0x0E45` | Tavern tip pointer table |
| `0xC416` | Tavern rumor text |
| `0x0DAE` | Tavern no-rumors text |
| `0x0DBF` | Tavern drink ok text |
| `0x0DCC` | Tavern drink sick text |
| `0x0DDC` | Tavern no drink/tip text |
| `0x0DFB` | Tavern tip retry text |
| `0x0E1D` | Tavern too sick text |

Города resident shop/tavern определяются по `S` оверлея:

| `S` | Город |
| ---: | --- |
| `0x0386` | SORPIGAL |
| `0x0412` | PORTSMIT |
| `0x041D` | ALGARY |
| `0x03D8` | DUSK |
| `0x0489` | ERLIQUIN |

## 6. Runtime memory map, известная на данный момент автору

### 6.1. Координаты, карта, ввод, текст

| Runtime | Назначение |
| ---: | --- |
| `0x0306` | Resident keyboard poll routine. |
| `0x3BBA` | Legacy input index. |
| `0xC9BB` | Overlay input index. |
| `0x3CB8` | Input buffer. |
| `0x3C38` | Runtime/current/target X. |
| `0x3C39` | Runtime/current/target Y. |
| `0x3C3A` | Packed coordinate `(Y << 4) | X`. |
| `0x3CFA..0x3DFF` | First static map layer. |
| `0x3DFA..0x3EFF` | Second static map layer. |
| `0x3BD4` | Active text pointer word. |
| `0x3BC4` | Text cursor column. |
| `0x4FB5` | Display text routine. |
| `0x4C60` | Positioned text routine. |
| `0x4FC8` | Current map event disable routine. |
| `0x5C1C` | Overlay transition routine. |
| `0x517C` | Resident random encounter / battle entry routine. |

Текст оверлея считается null-terminated ASCII с escape-представлением для CR,
LF, TAB и не-ASCII байтов. Максимум чтения overlay text в reader: 250 байт.

### 6.2. Параметры случайных встреч и боев

| Runtime | Назначение |
| ---: | --- |
| `0xC95D` | Raw шанс случайной встречи текущего оверлея. |
| `0xC961` | Максимальная сила случайных монстров. |
| `0xC962` | Максимальное количество монстров в группе случайной встречи. |
| `0xC96E` | Map flags: darkness, teleport disabled. |
| `0xC96F` | Максимальный уровень случайных монстров. |
| `0x3C1C` | Random encounter rubicon. |
| `0x3C1D` | Battle monster count. |
| `0x3C29` | Вторая таблица слотов монстров битвы. |
| `0x3C58` | Первая таблица слотов монстров битвы. |
| `0x3CA6` | Battle monster strength adjustment. |

Слотов монстров: `0x0F` (15). Непрямое копирование таблицы ограничено
`0x0E`.

Полностью определенный монстр собирается из пары байтов:

```text
val1 = first table byte, usually [0x3C58 + bx]
val2 = second table byte, usually [0x3C29 + bx]
monsterId = val1 + 16 * val2 - 17
```

Известные таблицы загрузки монстров:

| Runtime table | Размер/диапазон | Роль |
| ---: | --- | --- |
| `0xCDBD..0xCDC4` | 8 bytes | Первый индекс полностью определенного монстра. |
| `0xCDB5..0xCDBC` | 8 bytes | Второй индекс полностью определенного монстра. |
| `0xCDA9..0xCDB0` | 8 bytes | Частичный/диапазонный первый индекс. |
| `0xCDB1..0xCDB8` | 8 bytes | Частичный/диапазонный второй индекс. |
| `0xCA7F..0xCA83` | 5 bytes | Дискретный первый индекс шаблона битвы. |
| `0xCA84..0xCA88` | 5 bytes | Дискретный второй индекс шаблона битвы. |

Анализатор отслеживает инструкции вида `MOV AL,[BX+table]`,
`MOV BP,[BX+table]`, `MOV AL,[BX+table]` для low byte, а затем сохранения в
`[BX+3C58]`, `[BX+3C29]`, `[3C58]`, `[3C29]`.

### 6.3. Лут на полу

| Runtime | Назначение |
| ---: | --- |
| `0x3C77..0x3C7F` | Буфер лута, встречается при очистке диапазона. |
| `0x3C79` | Индекс контейнера. `0` при явной записи трактуется как уничтожение контейнера; при неявном луте может означать обычный неуказанный контейнер. |
| `0x3C7A` | Item-related byte. |
| `0x3C7B` | Item-related byte. |
| `0x3C7C` | Item code, основной байт предмета. |
| `0x3C7D` | Gold low byte. |
| `0x3C7E` | Gold high byte. |
| `0x3C7F` | Gems. |

Gold хранится как word, но отображаемое количество:

```text
rawGold = low | (high << 8)
gold    = rawGold >> 1
```

### 6.4. Внешние состояния/заклинания партии

Эти адреса анализатор помечает как unknown external state guards:

| Runtime | Назначение по отображению |
| ---: | --- |
| `0x3C97` | Protection from Fire |
| `0x3C98` | Protection from Poison |
| `0x3C99` | Protection from Acid |
| `0x3C9E` | Levitate |
| `0x3CA1` | Psychic Protection |

## 7. ROSTER.DTA и runtime-структура партии

В проекте нет отдельного загрузчика `ROSTER.DTA`. Поэтому ниже описан не
подтвержденный layout файла на диске, а runtime-представление персонажей,
которое анализатор использует при разборе событий.

Known runtime addresses:

| Runtime | Назначение |
| ---: | --- |
| `0x3CA8` | Таблица указателей на активных членов партии. 6 word-указателей. |
| `0x3BC0` | Текущее число членов партии. Анализатор трактует как диапазон `1..6`. |

Активных членов партии максимум `6`. Указатель из `0x3CA8 + memberIndex*2`
используется как base address структуры персонажа. Поля ниже являются offsets
от этой структуры.

### 7.1. Поля персонажа

| Offset | Поле |
| ---: | --- |
| `0x10` | Sex: `0x01` male, `0x02` female. |
| `0x11` | Innate alignment. |
| `0x12` | Current alignment. |
| `0x15` | Permanent intellect. |
| `0x16` | Temporary intellect. |
| `0x17` | Permanent might. |
| `0x18` | Temporary might. |
| `0x19` | Permanent personality. |
| `0x1A` | Temporary personality. |
| `0x1B` | Permanent endurance. |
| `0x1C` | Temporary endurance. |
| `0x1D` | Permanent speed. |
| `0x1E` | Temporary speed. |
| `0x1F` | Permanent accuracy. |
| `0x20` | Temporary accuracy. |
| `0x21` | Permanent luck. |
| `0x22` | Temporary luck. |
| `0x24` | Temporary level. |
| `0x25` | Age. |
| `0x2B` | SP low byte. |
| `0x2C` | SP high byte. |
| `0x33` | HP low byte. |
| `0x34` | HP high byte. |
| `0x35` | Max HP low byte. |
| `0x36` | Max HP high byte. |
| `0x3E` | Food. |
| `0x3F` | Status/condition. |
| `0x40..0x45` | Equipment/inventory slots 1..6. |
| `0x46..0x4B` | Backpack slots 1..6. |
| `0x6E` | Ranalou judgement score. |
| `0x71` | Ranalou quest line progress. |
| `0x75` | Lord Inspectron quest counter. |
| `0x76` | Lord Hacker quest counter. |
| `0x77` | Lord Ironfist quest counter. |
| `0x7B` | Permanent stat raise flags. |
| `0x7D` | Main quest completion field. |

Важное ограничение: анализатор не доказывает размер записи персонажа в
`ROSTER.DTA`. По известным offsets запись должна быть минимум `0x7E` байт, но
в коде нет самостоятельной константы "record size".

### 7.2. Значения и маски полей партии

Alignment:

| Value | Alignment |
| ---: | --- |
| `0x01` | GOOD |
| `0x02` | NEUTRAL |
| `0x03` | EVIL |

Status:

| Value/Mask | Смысл |
| ---: | --- |
| `0x00` | GOOD |
| `0xFF` | ERADICATED |
| `0x08` | DISEASED |
| `0x10` | POISONED |
| `0x20` | PARALYZED |
| `0x40` | UNCONSCIOUS |
| `0x80` | DEAD mask; значения `> 0x80`, кроме `0xFF`, считаются DEAD |

Permanent stat raise flags at `+0x7B`:

| Mask | Stat |
| ---: | --- |
| `0x01` | ENDURANCE |
| `0x02` | PERSONALITY |
| `0x04` | INTELLECT |
| `0x08` | MIGHT |
| `0x10` | ACCURACY |
| `0x20` | SPEED |
| `0x40` | LUCK |

Ranalou:

| Offset | Mask/Value | Смысл |
| ---: | ---: | --- |
| `0x71` | `0x01` | Quest started. |
| `0x71` | `0x7E` | Prisoner progress bits. |
| `0x6E` | `+0x20` | Засчитанный prisoner progress increment в некоторых событиях. |

Main quest field at `+0x7D`:

| Value/Mask | Смысл |
| ---: | --- |
| `0x1F` | Astral projectors completed. |
| `0x40` | Imposter defeated, transfer ready. |
| `0x80` | Main quest completed threshold/mask. |

Quest lord counters at `+0x75..+0x77` являются битовыми счетчиками. Если маска
содержит один бит, номер квеста равен номеру установленного бита, начиная с 1.

## 8. Анализ патчей

Анализатор дизассемблирует машинный код 8086 через Capstone и символически
исполняет достижимые пути.

Основные принципы:

- Адрес инструкции в дизассемблере равен file offset в `.OVR`.
- Для чтения overlay data/runtime text используется формула из раздела 3.3.
- Эмулятор отслеживает 8- и 16-битные регистры, диапазоны, discrete values,
  источники регистров и происхождение flags.
- Глубина рекурсии/вызовов ограничена, чтобы не уйти в бесконечные loops.
- Для table object patch стартовые `BL/BH/BX` равны нулю.
- Для default path patch стартовый `BX` связан с `X` клетки.
- Переходы `JE/JNE/JB/JBE/JA/JAE` применяются к диапазонам координат, памяти и
  known state.
- Runtime reads из `0x3C38/0x3C39/0x3C3A` считаются координатными.
- Reads из `0x3CFA/0x3DFA` связывают путь с `MAZEDATA.DTA`.

Тексты обнаруживаются несколькими способами:

- Прямые `MOV reg, imm16` или memory writes с адресом `>= 0xC972`.
- Указатель в `0x3BD4` перед вызовом display routine.
- Positioned text routine `0x4C60` с учетом cursor column `0x3BC4`.
- Resident handlers из `MM.EXE`.
- Immediate printed chars и небольшие inline-фрагменты.

Переход в другой overlay обнаруживается как `JMP 0x5C1C`:

```text
AL = globalX
BL или low(BX) = globalY
BP = mapSelector
```

После этого имя целевого `.OVR` берется из таблицы `MM.EXE` или outdoor fallback.

### 8.1. Известные цели `JMP` и `CALL`

Не каждый `JMP` в патче является вызовом внешней функции. Если цель прыжка
попадает внутрь текущего `.OVR`, анализатор просто продолжает трассировку с
этого адреса. Специальная семантика появляется только у известных resident
адресов или у прыжков за пределы текущего overlay-кода.

| Цель | Тип | Семантика |
| ---: | --- | --- |
| `0x5C1C` | `JMP` | Загрузка другого overlay. Перед прыжком ожидаются `AL = globalX`, `BL` или low(`BX`) = `globalY`, `BP = mapSelector`. |
| `0x517C` | `JMP` | Передача управления resident routine random encounter / battle. Если перед прыжком уже заполнены боевые таблицы или счетчик монстров, анализатор трактует это как вход в бой; без такой подготовки - как random encounter. |
| `0x32EB` | `JMP` | Sorpigal statue resident handler. Анализатор читает индекс статуи и resident texts из `MM.EXE`. |
| `0x2D15` | `JMP` | Equipment shop resident handler. |
| `0x2972` | `JMP` | Food shop resident handler. |
| `0x30C3` | `JMP` | Tavern resident handler. |
| `0x4FC8` | `CALL` | Сброс бита события текущей клетки во втором слое карты. |
| `0x4FB5` | `CALL` | Вывод текста по активному указателю `0x3BD4`. |
| `0x4C60` | `CALL` | Позиционированный вывод текста; учитывается колонка курсора `0x3BC4`. |
| `0x5101` | `CALL` | Получение пользовательского кода клавиши в `AL`. |

Для `JMP 0x517C` отдельного универсального "start battle" адреса в текущей
модели нет. Бой определяется по состоянию, подготовленному перед прыжком:
`[0x3C1D]` задает количество монстров, `[0x3C58+n]` и `[0x3C29+n]` задают две
компоненты слота монстра, `[0x3CA6]` задает модификатор силы, а `[0x3C1C]`
задает random encounter rubicon. Поэтому один и тот же resident entry `0x517C`
может быть как чистой случайной встречей, так и завершением патча, который уже
сформировал конкретную битву.

## 9. Минимальный алгоритм независимого анализатора

Чтобы повторить базовый функционал "открыть `.OVR`, увидеть карту и патчи":

1. Прочитать `.OVR`, проверить `fileLength >= 0x0E`.
2. Найти рядом `MAZEDATA.DTA` и `MM.EXE`.
3. Определить `blockCount = len(MAZEDATA)/512`.
4. Просканировать `MM.EXE` на run zero-terminated overlay names и найти индекс
   имени выбранного `.OVR`.
5. Извлечь два слоя `MAZEDATA` по `index * 512`.
6. Вычислить `S` через `cmp bl,[count]` в `0x2E`; если не вышло, попробовать
   no-object-table через `0xC972`.
7. Прочитать pre-object metadata относительно `S`.
8. Прочитать side layout record at `S - 0x32`, затем декодировать стороны каждой
   клетки по двум слоям `MAZEDATA`.
9. Если object table есть, прочитать count, packed coords, directions, patch keys.
10. Для каждого объекта вычислить `patchAddress = key + 0x0B7F`.
11. Дизассемблировать код от patchAddress и отслеживать:
    - обращения к текстам `>= 0xC972`;
    - записи в `0xC95D/0xC961/0xC962/0xC96E/0xC96F`;
    - battle tables `0x3C58/0x3C29`, count `0x3C1D`;
    - loot buffer `0x3C79..0x3C7F`;
    - teleport coords `0x3C38/0x3C39/0x3C3A`;
    - overlay transition `0x5C1C`;
    - party pointer table `0x3CA8` and character field offsets.
12. Проанализировать default path `0x34` или `0x20` для непокрытых клеток.
13. Обогатить overlay transitions таблицей `MM.EXE` `0x10B2B/0x10B99/0x109B0`.

## 10. Что пока не является подтвержденным форматом

- Полный формат `ROSTER.DTA` на диске не описан отдельным парсером. Известны
  runtime offsets полей персонажа и таблица указателей активной партии.
- Не все поля 14-байтового `.OVR` header имеют назначение в анализаторе.
- Side layout templates содержат набор известных семейств и file-specific
  overrides. Если встретится неизвестный family key, текущий анализатор падает
  с диагностикой и требует добавить запись в `OvrSideElementRegistry`.
- Часть сюжетных клеток имеет curated shortcuts и эвристики. Это знания
  анализатора о конкретных картах, а не универсальный binary format.

## 11. Основные исходники

| Файл | Что содержит |
| --- | --- |
| `MMMapEditor/OvrFileConfigs.cs` | Поиск companion-файлов, чтение MAZEDATA, определение `S`, overlay metadata offsets. |
| `MMMapEditor/OvrFileAnalyzer.cs` | Object table, default path, static map dispatch, patch address formula. |
| `MMMapEditor/OvrOverlayAddressReader.cs` | Перевод runtime overlay/resident addresses в file offsets, чтение текста. |
| `MMMapEditor/CodeExecutor.cs` | Символическое исполнение патчей, runtime memory map, party pointers, text/display, overlay transitions. |
| `MMMapEditor/InstructionAnalyzer.cs` | Распознавание текстов, лута, monster tables и записей в боевые структуры. |
| `MMMapEditor/OvrSideElementRegistry.cs` | Side layout record, known side key families, типы проходов. |
| `MMMapEditor/MainForm.cs` | Декодирование MAZEDATA bits в borders/passages/cell flags. |
| `MMMapEditor/OverlayTransitionResolver.cs` | Таблицы переходов в `MM.EXE` и metadata enrichment. |
| `MMMapEditor/Party*.cs` | Runtime offsets и семантика полей персонажей/партии. |
