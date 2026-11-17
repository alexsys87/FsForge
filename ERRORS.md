# Ошибки и предупреждения FsForge

Справочник всех сообщений, которые может показать программа, с объяснением причины и способом
исправления, если он есть.

## Как читать сообщения

- **Ошибка** прерывает операцию. Окно с заголовком «<операция> — ошибка» (например, «Открытие
  rootfs.img — ошибка»); в `fsforge-cli` — строка `error: …` и код выхода 1. Образ на диске при этом
  не меняется: новый файл пишется во временный `<имя>.fsforge-tmp` и заменяет прежний только после
  успешной записи и проверки.
- **Предупреждение** не мешает работе: операция выполнена, но что-то пришлось пропустить или
  изменить. После открытия, сохранения, добавления, импорта и извлечения внизу окна появляется
  полоса «… с предупреждениями: N»; кнопка на ней открывает полный список. В `fsforge-cli` — строки
  `warning: …`. Показ предупреждений после открытия можно выключить: Настройки → «Показывать
  предупреждения после открытия».
- Сообщения ядра (чтение и запись форматов) всегда на английском. Сообщения интерфейса выводятся на
  языке, выбранном в Настройки → «Язык интерфейса (Language)»; ниже они приведены по-русски, в
  английском интерфейсе текст тот же по смыслу. Английские тексты ядра приведены дословно; `<…>` —
  подставляемые значения (путь, номер, размер).
- «Corrupt …» во всех вариантах означает одно: структура в файле не такая, какой должна быть.
  Программа не пытается угадать недостающее и останавливается, чтобы не записать испорченные данные.

Общие способы, если файл повреждён:

1. Проверьте, что файл скопирован целиком (сравните размер и контрольную сумму с источником,
   скачайте заново).
2. Для ext — восстановите файловую систему в Linux: `e2fsck -fy образ.img`, затем откройте снова.
3. Для SquashFS — попробуйте `unsquashfs -d папка образ.sqfs` в Linux; что удалось распаковать,
   можно собрать обратно в FsForge через «Новый образ из папки».
4. Для tar — `tar -xvf архив.tar.gz` покажет, до какой записи архив читается.

---

## 1. Открытие файла

| …`It looks like a cpio archive in the old binary format, which is not supported (only newc, crc and odc).` | Архив cpio старого двоичного формата (`cpio -H bin`). | Перепакуйте: распакуйте `cpio -idm < a.cpio` и упакуйте заново `find . \| cpio -o -H newc > b.cpio`. |
| Сообщение | Причина | Что делать |
|---|---|---|
| `The file cannot be opened: <текст системы>` | Файла нет, нет прав на чтение, файл занят другой программой без общего доступа. | Проверьте путь и права; закройте программу, которая держит файл (эмулятор, программа записи карт); скопируйте файл в другую папку. |
| `The file is not a SquashFS, EROFS, CramFS, romfs, ext2/3/4, F2FS, FAT, UBIFS or JFFS2 image, UBI image, disk image with such a partition, tar or cpio archive.` (для сжатого файла или архива к тексту добавляется `It was unpacked from a <вид> file, but the content is not a known image.`) | Формат не распознан. | Убедитесь, что это образ файловой системы, а не, например, образ прошивки в собственном формате производителя. Включите Настройки → «Искать SquashFS и UBI внутри прошивок, если файл не образ». |
| …`It looks like LZ4 / LZMA / compress (.Z) compressed data.` | Файл сжат методом, который программа не распаковывает (gzip, xz, zstd, bzip2, zip и 7z распаковываются сами). | Распакуйте его сторонней программой (7-Zip, `lz4 -d`, `unlzma`) и откройте результат. |
| …`It was unpacked from a <gzip/xz/zstd> file, but the content is not a known image.` | Внешнее сжатие снято, но внутри не образ. | Проверьте, что внутри: возможно, это архив другого типа или прошивка производителя. |
| `The <gzip/xz/zstd> data is damaged: <подробности>` | Сжатый файл `*.img.gz`, `*.ext4.xz`, `*.zst` повреждён или обрезан. | Скачайте файл заново; проверьте `gzip -t`, `xz -t`, `zstd -t`. |
| `The image is damaged or uses a layout that is not supported (<подробности>).` | Внутренняя структура не прошла проверки так, что это не удалось отнести к конкретной ошибке. | Проверьте образ эталонными утилитами (`e2fsck -fn`, `unsquashfs -s`). Если они считают его исправным — это ошибка программы: сообщите о ней, приложив по возможности образ. |
| `The image is truncated: cannot read <N> bytes at offset <M>.`, `The image is truncated.` | Файл короче, чем следует из его собственных заголовков. | Файл скопирован или скачан не полностью — получите его заново. |
| `The image is corrupt: it refers to data before its own start.` | Структура ссылается на отрицательное смещение. | Файл повреждён (см. общие способы). |
| Предупреждение: `The file is an Android sparse image. Saving writes an ordinary (raw) image of the filesystem.` | Открыт образ Android sparse (simg). Он читается, но в том же формате не пишется. | Сохраните через «Сохранить как» в обычный образ. Если устройству нужен sparse, преобразуйте результат `img2simg образ.img образ.simg`. |
| Предупреждение: `The <формат> filesystem was found inside the file at offset 0x<…>, and the file has no partition table that would tell how to rebuild it. Saving writes the filesystem alone, without the surrounding container.` | Файловая система найдена внутри прошивки с заголовками и контрольными суммами производителя. Такой контейнер не пересобирается. | Сохраните файловую систему отдельным файлом («Сохранить как») и соберите прошивку инструментами производителя. Перезаписать исходный файл нельзя — «Сохранить» всегда спросит новое имя. |
| `Not an Android sparse image.`, `Unsupported Android sparse image header.` | Заголовок sparse-образа неизвестной версии. | Преобразуйте файл в обычный образ: `simg2img образ.simg образ.img`. |
| `Corrupt sparse image: bad raw chunk size.`, `Corrupt sparse image: unknown chunk type 0x<…>.` | Повреждённый sparse-образ. | Получите файл заново или преобразуйте `simg2img`, если она его принимает. |

### Образы дисков (с таблицей разделов)

| Сообщение | Причина | Что делать |
|---|---|---|
| `None of the partitions of this disk image holds SquashFS, EROFS, F2FS, CramFS, romfs, ext2/3/4 or FAT. Its partitions: <список>.` | На диске нет раздела, который программа умеет редактировать (только NTFS, exFAT, swap и т. п.). | Редактируются разделы SquashFS, EROFS, F2FS, CramFS, romfs, ext2/3/4 и FAT12/16/32. Раздел NTFS или exFAT можно править в Windows, подключив образ (например, в «Управлении дисками» для `.vhd` или сторонней программой). |
| `The disk image has no partition <N>. Its partitions: <список>.` | В `fsforge-cli` указан несуществующий `part=<N>`. | Посмотрите номера разделов: `fsforge-cli info образ.img`. |
| `Partition <N> holds <формат>, not SquashFS, EROFS, ext2/3/4 or FAT, and cannot be edited.` | Выбран раздел с неподдерживаемой файловой системой. | Выберите другой раздел. |
| `The selected partition does not hold SquashFS, EROFS, ext2/3/4 or FAT.`; в окне выбора — «В этом разделе нет SquashFS, EROFS, ext2/3/4 или FAT — его нельзя открыть для редактирования.» | То же в окне выбора раздела. | Выберите раздел с пометкой SquashFS, EROFS, ext или FAT. |
| «Выберите раздел.» | В окне выбора раздела ничего не выбрано. | Выберите раздел в списке. |
| Открылся корневой раздел, а нужен загрузочный FAT (`config.txt`, `cmdline.txt`) | По умолчанию выбирается самый большой раздел не FAT — корневая система. | В окне выбора раздела выберите раздел FAT; в `fsforge-cli` — `part=1` (номер раздела показывает `fsforge-cli info`). |
| `The file has no partition table, so there is no partition <N> to open.` | `part=<N>` указан для файла без таблицы разделов. | Уберите `part=`. |
| `The disk image is truncated: its end cannot be read.` | Образ диска короче, чем указано в таблице разделов. | Файл обрезан — получите его заново. |

## 2. Чтение SquashFS

| Сообщение | Причина | Что делать |
|---|---|---|
| `Not a SquashFS image (bad magic).`, `The file is too small to be a SquashFS image.` | Это не SquashFS. | Проверьте файл; см. «Открытие файла». |
| `Big-endian or pre-4.0 SquashFS images are not supported.` | Старый (до 4.0) или big-endian SquashFS — встречается в старых прошивках. | Распакуйте его `unsquashfs` (поддерживает старые версии) или `sasquatch` и соберите заново через «Новый образ из папки». |
| `SquashFS version <X.Y> is not supported (only 4.0).` | Неизвестная версия формата. | То же, что выше. |
| Предупреждение: `The image is compressed with LZO / LZMA, which can be read but not written. Saving uses <gzip/xz> unless another method is chosen.` | Читать LZO и устаревший LZMA программа умеет, записывать — нет. | Ничего делать не нужно: при сохранении будет gzip или xz. Если устройство требует именно LZO или LZMA, соберите образ `mksquashfs` в Linux. |
| Предупреждение: `The image is shorter than the size recorded in its superblock.` | Файл обрезан в конце. | Часть файлов может не читаться. Получите полный файл. |
| Предупреждение: `Extended attribute with unknown namespace id <N> was skipped.` | Расширенный атрибут нестандартного пространства имён. | Атрибут будет потерян при сохранении. Обычно это безопасно. |
| Предупреждение: `Invalid or duplicate directory entry '<имя>' in '<папка>' was skipped.` | В папке две записи с одним именем или имя содержит `/`. | Образ повреждён или собран нестандартной программой; запись пропущена. |
| `Corrupt SquashFS superblock: invalid block size.` и все `Corrupt SquashFS image: …` (metadata block is out of range, invalid metadata block size, lookup table is truncated, the id table is empty, xattr value is too large, id index is out of range, the root inode is not a directory, truncated directory header / entry, too many entries in a directory header, a directory entry name is too long, a directory is referenced more than once, negative file size, file has too many blocks, fragment index is out of range, symlink target is too long, unknown inode type, invalid data block size, data block is shorter than expected, file data is truncated, fragment is out of range) | Повреждённые таблицы или данные SquashFS. | См. общие способы. Если ошибка возникает только при чтении отдельного файла (извлечение, сохранение), остальное содержимое обычно доступно: извлеките, что читается. |
| `Unknown SquashFS compressor id <N>.` | Метод сжатия неизвестен. | Распакуйте образ `unsquashfs` (если он его знает) и соберите заново. |

## 3. Чтение ext2 / ext3 / ext4

| Сообщение | Причина | Что делать |
|---|---|---|
| `Not an ext2/ext3/ext4 image (bad magic).`, `The file is too small to be an ext image.` | Это не ext. | Проверьте файл; см. «Открытие файла». |
| `Encrypted ext4 images are not supported.` | Включено шифрование ext4 (fscrypt). | Не поддерживается: расшифровать содержимое без ключа нельзя. |
| `This is an external ext journal device, not a filesystem.` | Открыт отдельный журнал, а не файловая система. | Откройте сам образ файловой системы. |
| `The image uses unsupported ext features (incompat 0x<…>).` | Включены редкие возможности (сжатие ext2, dirdata). | Преобразуйте образ в Linux (например, скопируйте содержимое в новый `mke2fs`) или распакуйте и соберите заново. |
| Предупреждение: `The image file is shorter than the filesystem it contains.` | Файл обрезан, или образ снят с раздела меньшего размера. | Данные за концом файла не читаются. Если это образ раздела, снимите его полностью. |
| `Corrupt ext superblock: invalid block size / invalid geometry / invalid group count.` | Испорчен суперблок. | `e2fsck -fy -b 32768 образ.img` (резервный суперблок; для блоков 1 КБ — `-b 8193`), затем откройте снова. |
| `Corrupt ext image: …` (block / inode is out of range, bad extent header / tree in inode, extent of inode is out of range, inode is unexpectedly large, directory inode is too large, inode has an invalid type, symlink inode is too long, the root inode is not a directory) | Повреждены иноды или карты блоков. | `e2fsck -fy образ.img`. |
| Предупреждение: `'<путь>' was skipped: <причина>` | Отдельный объект не читается (причина — одна из ошибок выше). Остальное открыто. | Объект будет потерян при сохранении. Чтобы его сохранить, восстановите образ `e2fsck -fy`. |
| Предупреждение: `'<путь>' points to a deleted inode and was skipped.` | Запись каталога указывает на удалённый инод — типично после некорректного выключения. | Обычно безопасно. Для порядка — `e2fsck -fy`. |
| Предупреждение: `Directory '<путь>' is linked more than once and was skipped.` | Одна папка встречается в двух местах (повреждение). | `e2fsck -fy`. |
| Предупреждение: `Invalid or duplicate directory entry '<имя>' in '<папка>' was skipped.` | Повтор имени или `/` в имени. | `e2fsck -fy`. |
| Предупреждение: `Inode <N>: invalid / unreadable extended attribute block was ignored.`, `Inode <N>: unreadable large extended attribute was skipped.` | Повреждён блок расширенных атрибутов. | Атрибуты этого объекта (SELinux-метки, capabilities) будут потеряны. При необходимости восстановите образ `e2fsck -fy` или задайте атрибуты вручную в «Свойствах». |
| Предупреждение: `Inode <N>: extended attribute with unknown name index <K> was skipped.` | Атрибут нестандартного пространства имён. | Атрибут будет потерян; обычно безопасно. |

### Журнал (ext3 / ext4 после некорректного выключения)

| Сообщение | Причина | Что делать |
|---|---|---|
| Предупреждение: `The filesystem was not cleanly unmounted; its journal was replayed while reading (<N> transaction(s), <M> block(s)). The image file itself is unchanged.` | Файловую систему не размонтировали (выдернули карту, пропало питание). Программа воспроизвела журнал в памяти — показывает и сохраняет состояние как после восстановления ядром. | Ничего делать не нужно. Сохранение запишет чистый образ. Сам файл не меняется, пока вы его не сохраните. |
| Предупреждение: `The journal uses fast commits, which are not replayed: the very last changes may be missing.` | В журнале есть записи fast commit (ext4 с `fast_commit`); они не воспроизводятся. | Если важны последние секунды работы, восстановите образ в Linux: `e2fsck -fy образ.img`, потом откройте. |
| Предупреждение: `Journal transaction <N> has a damaged descriptor block / a bad commit checksum / a damaged revoke block; the journal was replayed up to the transaction before it.` | Последняя транзакция записана не до конца или повреждена. Ядро в этом случае поступает так же. | Обычно это и есть оборванная запись при выключении — ничего делать не нужно. |
| Предупреждение: `Journal: <N> block(s) with a bad checksum were not replayed.`, `Journal: <N> block(s) outside the filesystem were not replayed.` | Отдельные блоки журнала повреждены. | Проверьте результат; при сомнениях — `e2fsck -fy` в Linux. |
| Предупреждение: `The filesystem was not cleanly unmounted and its journal could not be replayed: <причина>. Changes recorded only in the journal are missing from what is shown and saved.` | Журнал не читается. Причины: `The journal is too small.`, `The journal superblock has a bad magic number / an unknown type / an invalid journal length / an invalid first log block / an invalid log start.`, `The journal block size differs from the filesystem block size.`, `The journal uses unsupported features (incompat 0x<…>).`, `The journal uses an unknown checksum type.`, `The journal superblock checksum does not match.`, `The journal fast-commit area leaves no room for the log.`, `Block <N> of the journal is not allocated.` | Показано состояние без журнала — последние изменения потеряны. Восстановите образ в Linux `e2fsck -fy образ.img` и откройте снова. |
| Предупреждение: `The filesystem was not cleanly unmounted and its replayed journal gives invalid metadata (<причина>), so it was not used.` | После воспроизведения журнала получился испорченный суперблок. | То же: `e2fsck -fy`. |
| Предупреждение: `The filesystem was not cleanly unmounted and its journal is on a separate device, so it was not replayed.` | Журнал хранится на отдельном устройстве (внешний журнал). | Восстановите в Linux, подключив и журнал: `e2fsck -fy -j журнал.img образ.img`. |

В окне «Информация об образе» строка «Состояние» показывает: «чистая», «не была корректно
размонтирована; журнал воспроизведён при чтении (…)», «…журнал пуст» или «журнал не воспроизведён
(требуется восстановление)».

## 4. Чтение UBIFS и UBI

UBIFS и UBI читаются и записываются (запись — в разделе 10). Образ UBI всегда сохраняется целиком,
с заменой открытого тома.

### Образы UBI (тома)

| Сообщение | Причина | Что делать |
|---|---|---|
| Предупреждение: `The UBI image was found inside the file at offset 0x<…>. Saving writes the UBI image alone, without the data in front of it.` | Образ UBI лежит в файле прошивки не с начала (перед ним ядро или заголовок производителя). Заголовок и контрольные суммы прошивки программа не пересобирает. | Сохраните под новым именем и соберите прошивку заново средствами производителя. |
| `None of the volumes of this UBI image holds UBIFS, SquashFS or EROFS. Its volumes: <список>.`, `The UBI image holds no volumes.` | В образе нет тома с файловой системой, которую программа читает (например, только ядро). | Проверьте, тот ли это файл: корневая файловая система может быть в другом разделе флеш-памяти. |
| `The UBI image has no volume '<имя>'. Its volumes: <список>.` | Параметр `vol=` в `fsforge-cli` называет том, которого нет. | Возьмите имя или номер из списка в сообщении (или из `fsforge-cli info`). |
| `Volume '<имя>' (<N>) holds <формат>, not UBIFS, SquashFS or EROFS, and cannot be opened.` | Выбран том без читаемой файловой системы (ядро, пустой том). | Выберите другой том. |
| `The selected volume does not hold UBIFS, SquashFS or EROFS.`; в окне выбора — «В этом томе нет UBIFS, SquashFS или EROFS — его нельзя открыть.», «Выберите том.» | То же в окне выбора тома. | Выберите том с пометкой UBIFS или SquashFS. |
| `The file is not a UBI image, so there is no volume '<имя>' to open.` | `vol=` указан для файла, который не является образом UBI. | Уберите параметр. |
| `The file is a UBI image and has no partition table, so there is no partition <N> to open.` | `part=` указан для образа UBI. | Тома UBI выбираются параметром `vol=`. |
| `The size of the erase blocks of this UBI image cannot be determined: no second erase-counter header was found. A flash dump that includes out-of-band (spare) data is not supported.` | В файле один блок стирания, либо это дамп NAND вместе с OOB-данными: блоки в нём идут не подряд. | Снимите дамп без OOB (`nanddump --omitoob`) или уберите OOB сторонней утилитой. |
| `Not a UBI image (bad erase-counter header).`, `UBI version <N> is not supported (only 1).`, `Corrupt UBI image: invalid header offsets.` | Испорчен заголовок первого блока или неизвестная версия UBI. | Получите файл заново. |
| Предупреждение: `<N> erase block(s) of the UBI image have damaged headers and were ignored.` | У части блоков испорчены заголовки (битые блоки флеш-памяти, оборванная запись, посторонние данные в дампе). | Если после этого файловая система читается без ошибок — ничего. Иначе снимите дамп заново. |
| Предупреждение: `<N> erase block(s) in the file belong to another UBI image and were ignored: only the first UBI image of the file is read.` | В одном файле несколько образов UBI (два слота прошивки, дамп всей флеш-памяти). Читается первый. | Чтобы открыть другой, вырежьте его раздел из дампа в отдельный файл (`dd` с нужным смещением). |
| Предупреждение: `The size of the UBI image is not a multiple of its erase block size (<N> bytes); the incomplete last block was ignored.` | За образом в файле есть хвост (подпись, выравнивание) или файл обрезан. | Обычно безопасно. Если затем появляются ошибки чтения — файл неполный. |
| Предупреждение: `The UBI image has no volume table; the names of its volumes are unknown.`, `The volume table of the UBI image is damaged; the names of its volumes are unknown.` | Не найдена или испорчена служебная таблица томов. | Тома всё равно открываются — по номерам. |

### UBIFS

| Сообщение | Причина | Что делать |
|---|---|---|
| `The UBIFS image is encrypted (fscrypt). Names and contents of its files cannot be read without the keys, so encrypted images are not supported.` | Файловая система создана с шифрованием. | Не поддерживается. |
| Предупреждение: `'<путь>' is encrypted and was skipped.` | Отдельный объект зашифрован. | Его содержимое недоступно без ключа. |
| Предупреждение: `The UBIFS image is authenticated (signed). The signatures are not verified.` | Образ подписан (`mkfs.ubifs --hash-algo … --auth-key …`). | Содержимое читается как обычно; подлинность не проверяется. |
| Предупреждение: `The image uses UBIFS format version <N>, which is newer than this program knows (5). It may be read incorrectly.` | Формат новее поддерживаемого. | Сверьте результат с Linux (`mount -t ubifs`). |
| `UBIFS key format <N> is not supported.`, сжатие: `The data node at erase block <N>, offset <M> uses an unknown compressor (<K>).` | Нестандартный вариант формата. | Не поддерживается. |
| `Not a UBIFS image (no superblock node).`, `The file is too small to be a UBIFS image.` | Это не UBIFS. | Проверьте файл; см. «Открытие файла». |
| `Corrupt UBIFS image: bad checksum of the superblock.`, `…invalid geometry in the superblock.`, `…no valid master node was found.`, `…the root directory is missing.` | Повреждены основные структуры. | Получите образ заново. Для дампа с устройства — снимите его повторно, без OOB и с исправлением ошибок (ECC). |
| `The UBIFS image is truncated: erase block <N>, offset <M> is not in the file.` | Файл короче файловой системы. | Файл скопирован не полностью. |
| `Corrupt UBIFS image: …` (the index points outside the filesystem, no node at…, unexpected node at…, bad checksum of the node at…, invalid index node at…, the index node at… is at the wrong level of the tree) | Повреждён корень индекса — открыть нечего. | То же. |
| Предупреждение: `A damaged node was skipped; the file or name it describes is missing. <причина>`, `A part of the index is damaged and was skipped; files may be missing. <причина>`, `... and <N> more damaged node(s) were skipped.` | Отдельный узел не прошёл проверку контрольной суммы (сбойные биты в дампе). Остальное открыто. | Объект будет отсутствовать и в сохранённом образе. Снимите дамп заново, если он нужен. |
| Предупреждение: `'<путь>' points to a missing inode and was skipped.` | У имени нет инода: следствие предыдущего пункта или некорректного выключения. | Обычно безопасно. |
| Предупреждение: `Invalid directory entry '<имя>' in '<папка>' was skipped.`, `Directory '<путь>' is linked more than once and was skipped.`, `'<путь>' has an unknown file type and was skipped.`, `'<путь>' has an invalid size and was skipped.`, `The symbolic link '<путь>' has no target and was skipped.`, `The extended attribute '<имя>' of '<путь>' cannot be read and was skipped.` | Повреждённые записи. | Объект (атрибут) пропущен; остальное открыто. |
| При чтении файла: `Corrupt UBIFS image: invalid data node at…`, `…the data node at… does not belong to the file.`, `…does not unpack to its recorded size.`, `Corrupt zlib data in a UBIFS data node.`, `Corrupt LZO block.`, `Corrupt Zstandard block.` | Повреждён блок данных файла. Ошибка появляется при извлечении, просмотре или сохранении. | Удалите этот файл из образа перед сохранением или получите исправный образ. |

### Журнал UBIFS (после некорректного выключения)

| Сообщение | Причина | Что делать |
|---|---|---|
| Предупреждение: `The filesystem was not cleanly unmounted: <N> journal node(s) were applied while reading. The file itself was not changed.` | Образ снят с работавшего или внезапно выключенного устройства. Программа воспроизвела журнал в памяти и показывает состояние, которое смонтировало бы ядро. | Ничего делать не нужно. |
| Предупреждение: `<N> journal erase block(s) of the UBIFS image end in damaged data (an interrupted write); the nodes before the damage were applied.` | Последняя запись оборвана выключением питания. Ядро отбрасывает её так же. | Ничего делать не нужно. |
| Предупреждение: `The log of the UBIFS image does not start with the record of its last commit; the journal was not replayed. Changes made shortly before the image was taken may be missing.`, `The master node of the UBIFS image points to a log outside the log area; the journal was not replayed.` | Журнал не соответствует последней фиксации (дамп снят во время записи, повреждён). | Показано состояние последней фиксации. Если нужны самые последние изменения — снимите дамп с остановленного устройства. |

В окне «Сведения об образе» строка «Состояние» показывает: «чистая», «не была корректно
размонтирована; журнал воспроизведён при чтении (…)» или «…журнал пуст».

## 5. Чтение JFFS2

JFFS2 — журнал узлов: каждое изменение на устройстве дописывает новые узлы. Программа просматривает образ
целиком и для каждого файла и записи каталога берёт последнюю версию, как ядро при монтировании; узлы
с ошибкой контрольной суммы (оборванная запись) и помеченные ядром как устаревшие пропускаются.

| Сообщение | Причина | Что делать |
|---|---|---|
| Предупреждение: `<N> damaged JFFS2 node(s) were skipped (bad checksums, usually the remains of an interrupted write).` | Узлы с неверной контрольной суммой — обычно последняя запись, оборванная выключением питания. Ядро пропускает их так же. | Ничего делать не нужно. Если таких узлов много, а файлов не хватает, дамп повреждён: снимите его заново. |
| Предупреждение: `Some files are compressed with rubinmips / dynrubin / lzma / copy, which this program cannot unpack; reading them fails.` | Редкие компрессоры JFFS2 (lzma — патч OpenWrt). Структура видна, но содержимое таких файлов не распаковывается: при извлечении или сохранении будет ошибка `JFFS2 data compressed with <метод> cannot be unpacked.` | Распакуйте образ на устройстве или в Linux с подходящим ядром (`mount -t jffs2` через `mtdram`/`block2mtd`) и соберите заново из папки. |
| `The JFFS2 image has a node of an unknown incompatible type 0x<…> at 0x<…>; it cannot be read safely.` | Узел неизвестного типа, без понимания которого образ нельзя читать (ядро тоже откажется монтировать). | Образ сделан нестандартной версией JFFS2 — используйте её инструменты. |
| Предупреждение: `The JFFS2 image has nodes of an unknown type 0x<…>; the kernel would mount it read-only.` | Узлы, которые можно пропустить при чтении. | Обычно безопасно. |
| Предупреждение: `The image has nodes of the pre-2001 JFFS2 format (magic 0x1984), which are not read.` | Узлы очень старого формата JFFS2. | Такие образы не поддерживаются; пересоберите их старым `mkfs.jffs2`/на устройстве. |
| `Corrupt zlib data in a JFFS2 node.`, `Corrupt LZO data in a JFFS2 node.`, `Corrupt rtime data in a JFFS2 node.`, `A JFFS2 data node is shorter than its size.` | Повреждены сжатые данные узла, хотя его контрольная сумма сошлась. | Повреждён конкретный файл; извлеките остальное. |
| Предупреждение: `'<путь>' points to inode <N>, which has no valid node, and was skipped.` | Запись каталога есть, а узлов самого файла нет (затёрты или повреждены). | Файл потерян; при сохранении его не будет. |
| Предупреждение: `'<путь>' was skipped: inode <N> has an invalid mode …`, `…the symbolic link of inode <N> has no target.`, `…the device node of inode <N> has <K> bytes of device number.` | Повреждённые метаданные объекта. | Объект потерян; остальное открыто. |
| Предупреждение: `Directory '<путь>' is linked more than once and was skipped.`, `Invalid or duplicate directory entry '<имя>' in '<папка>' was skipped.` | Повреждение структуры каталогов. | Обычно безопасно; проверьте содержимое папки. |
| Предупреждение: `Inode <N>: an extended attribute with the unknown prefix <P> was skipped.`, `Inode <N>: a malformed POSIX ACL was skipped.` | Атрибут неизвестного вида или испорченный ACL. | Атрибут потерян. |

В окне «Сведения об образе» видны порядок байтов, размер блока стирания (с пометкой «определён по
расположению узлов», если маркеров очистки нет или он один), сжатие и число действующих, устаревших и
повреждённых узлов. Оценку размера блока стоит сверить с устройством перед сохранением в JFFS2.

## 6. Чтение FAT

FAT12/16/32 читается из отдельного образа (`*.vfat`, `*.fat`, образ флешки без таблицы разделов) или
из раздела образа диска. Файл считается FAT, только если его загрузочный сектор проходит все проверки
(размер сектора, кластера, число FAT, байт носителя, размеры областей): случайное совпадение не
принимается за FAT. exFAT и NTFS не читаются.

| Сообщение | Причина | Что делать |
|---|---|---|
| `The file is too small to be a FAT filesystem.`, `Not a FAT filesystem (invalid boot sector).` | Загрузочный сектор повреждён или это не FAT (например, exFAT). | Проверьте файл `fsck.fat -n образ.vfat` в Linux; exFAT не поддерживается. |
| `Corrupt FAT filesystem: the FAT is too small for the number of clusters.` | Поля загрузочного сектора противоречат друг другу. | Файл повреждён: `fsck.fat -a образ.vfat` в Linux или `chkdsk` в Windows, затем откройте снова. |
| `The FAT filesystem is truncated: its FAT cannot be read.` | Файл обрезан до конца таблицы FAT. | Получите файл заново. |
| Предупреждение: `The file is shorter than the FAT filesystem it holds; the missing part reads as zeros.` | Файл короче, чем указано в загрузочном секторе (часто — образ, обрезанный после последнего занятого кластера). | Если файлы читаются целиком, это безопасно. При сохранении FsForge запишет образ полного размера. |
| Предупреждение: `The FAT filesystem was not cleanly unmounted (its "clean" flag is not set). It is read as it is; fsck.fat would check it.` | Карта или флешка извлечена без безопасного отключения, или питание пропало во время записи. В «Сведениях об образе» — «Состояние: не была корректно размонтирована». | Обычно данные целы. Для надёжности проверьте образ `fsck.fat -a` (Linux) или `chkdsk /f` (Windows). При сохранении FsForge записывает FAT заново и флаг снимается. |
| Предупреждение: `'<путь>': its cluster chain points outside the filesystem and was cut.`, `…loops or is longer than the file and was cut.`, `…ends in a free or bad cluster and was cut.` | Цепочка кластеров файла повреждена. | Файл, скорее всего, испорчен (обрезан). Проверьте его содержимое; `fsck.fat` исправит структуру. |
| Предупреждение: `'<путь>' is <N> bytes long but its clusters hold only <M>; the rest was cut.` | Размер в записи каталога больше, чем помещается в его кластеры. | Файл обрезан; сохранится с меньшим размером. |
| Предупреждение: `'<путь>' shares its clusters with '<другой путь>' (cross-linked) and was skipped.` | Два файла указывают на одни и те же кластеры — классическое повреждение FAT. | Пропущенный файл отсутствует в открытом образе. Если он нужен, исправьте образ `fsck.fat` и откройте снова. |
| Предупреждение: `Directory '<путь>' has no valid cluster and was skipped.` | Папка указывает на несуществующий кластер. | Её содержимое потеряно. |
| Предупреждение: `Invalid or duplicate directory entry '<имя>' in '<папка>' was skipped.` | Запись с недопустимым именем (`.`/`..` не на месте, `/`) или вторая запись с тем же именем. | Обычно безопасно; проверьте содержимое папки. |

Длинные имена, у которых не сошлась контрольная сумма с коротким именем (остатки после программы,
не знающей VFAT), не показываются: файл виден под коротким именем, как и в Windows.

## 7. Чтение EROFS

EROFS читается из отдельного образа (`*.erofs`, `system.img` и другие разделы Android после
распаковки), из раздела образа диска и из тома UBI. Читаются все виды образов, которые делает
`mkfs.erofs` до версии 1.9 включительно: без сжатия, lz4, lzma, deflate и zstd; кластеры до 1 МБ; хвосты
рядом с инодом (ztailpacking), фрагменты, дедупликация, файлы из чанков, блоки 512 байт – 4 КБ;
48-битная разметка (`-E48bit`) с записями экстентов, метабокс (`-m`), каталоги без «.».

| Сообщение | Причина | Что делать |
|---|---|---|
| `The file is too small to be an EROFS image.`, `Not an EROFS image (bad magic).`, `Corrupt EROFS superblock: invalid block size (2^<N>).` | Суперблок (по смещению 1024) повреждён. | Проверьте образ: `fsck.erofs образ.erofs`, `dump.erofs образ.erofs` в Linux. |
| `Corrupt EROFS image: inode <N> is said to lie in a metabox, but the image has none.`, `…inode <N> lies outside the metabox.`, `…the superblock is too short for a metabox.`, `…invalid metabox inode number.`, `…the metabox lies in itself.`, `…the packed inode lies in the metabox.` | Повреждены суперблок или ссылки на метабокс — отдельный (обычно сжатый) файл образа, в котором `mkfs.erofs -m` хранит иноды. | Получите файл заново; `fsck.erofs образ.erofs` (erofs-utils 1.9) покажет, цел ли он. |
| `Corrupt EROFS image: invalid number of extent records.`, `…the extent records are not in order.`, `…invalid extent position.`, `…an uncompressed extent is shorter than its data.`, `…a compressed file has no extents.`, `…a file has blocks but no address for them.` | Повреждены записи экстентов (так `mkfs.erofs` 1.9 с `-E48bit` описывает сжатые файлы) или адрес данных файла. | Ошибка относится к одному файлу; остальные читаются. |
| `The EROFS image uses features this program does not know (incompatible feature flags 0x<…>).`, `The EROFS image uses a compression method this program does not know (methods 0x<…>).`, `The EROFS image uses an LZMA variant this program does not know.`, `The EROFS inode <N> has a format / a data layout this program does not know (…).`, `The EROFS inode <N> uses a chunk format this program does not know (0x<…>).`, `The EROFS image uses directory blocks of their own size, which is not supported.` | Образ сделан более новой версией `mkfs.erofs`, чем знает программа. | Обновите FsForge; пока — распакуйте образ в Linux (`fsck.erofs --extract=папка`). |
| `The EROFS image keeps file data on <N> more device(s) (blob files), which are not part of this file.`, `The EROFS image keeps file data on another device (a blob file), which is not part of this file.` | Образ сделан с `--blobdev` (образы контейнеров Nydus, composefs): в этом файле только метаданные, содержимое файлов — в отдельных файлах. | Такой образ без его файлов данных прочитать нельзя; откройте образ, собранный одним файлом. |
| Предупреждение: `The checksum of the EROFS superblock does not match: the image is damaged or was changed without updating it. The kernel refuses to mount such an image.` | Суперблок изменён (например, метка тома исправлена в шестнадцатеричном редакторе) или повреждён. | Образ читается. Сохраните его в FsForge — контрольная сумма будет записана заново. Если суперблок не меняли намеренно, получите файл заново. |
| Предупреждение: `The file is shorter than the EROFS filesystem it holds; reading the missing part fails.` | Файл обрезан: суперблок описывает больше блоков, чем есть в файле. | Получите файл заново. Файлы из уцелевшей части читаются. |
| `Corrupt EROFS image: metadata lies outside the file.`, `…inode <N> lies outside the file.`, `…invalid inode number <N>.`, `…inode <N> has an invalid size.`, `…an empty metadata record.` | Ссылка на инод или служебную запись ведёт за пределы файла: образ обрезан или повреждён. | Получите файл заново; `fsck.erofs образ.erofs` покажет, цел ли он. |
| `Corrupt EROFS image: the root inode is not a directory.`, `…a directory is referenced more than once.`, `…the directory '<путь>' is larger than the image.`, `…has a truncated block.`, `…has an invalid entry table.`, `…has an entry whose name lies outside its block.`, `…a directory or link is too large.`, `…the symbolic link '<путь>' has an invalid length.` | Повреждён каталог или ссылка. | См. общие способы. |
| Предупреждение: `Invalid or duplicate directory entry '<имя>' in '<папка>' was skipped.`, `'<путь>' has an unknown file type (mode <…>) and was skipped.` | В каталоге запись с недопустимым или повторяющимся именем либо объект неизвестного типа. | Объект пропущен; остальное содержимое доступно. |
| Предупреждение: `An extended attribute of '<путь>' uses an unknown name prefix and was skipped.`, `…is of an unknown namespace (<N>) and was skipped.` | Атрибут из пространства имён, которого программа не знает. | Атрибут не показывается и при сохранении теряется. |
| `Corrupt EROFS image: invalid extended attribute area.`, `…an extended attribute runs past its area.`, `…an attribute name prefix is too long.` | Повреждены расширенные атрибуты. | См. общие способы. |
| `Corrupt EROFS image: inline data crosses a block boundary.`, `…invalid chunk size.`, `…a part of a file is not mapped to any data.` | Повреждено описание размещения файла. Ошибка появляется при чтении этого файла (извлечение, сохранение). | Остальные файлы читаются. Извлеките их выборочно. |
| `Corrupt EROFS image: invalid cluster size.`, `…a compressed file does not start with a head cluster.`, `…invalid offset in a cluster index.`, `…the cluster index is not in order.`, `…compact cluster indexes with a cluster size above 16 KiB.`, `…2-byte compact cluster indexes with a cluster size above 4 KiB.`, `…invalid distance in a compact cluster index.`, `…a compressed file has no clusters.`, `…a compressed cluster is larger than 1 MiB.`, `…an uncompressed cluster is shorter than its data.`, `…an inline cluster crosses a block boundary.`, `…an inline cluster is shorter than its data.`, `…invalid interlaced cluster.` | Повреждён индекс кластеров сжатого файла. | То же: ошибка относится к одному файлу. |
| `Corrupt EROFS image: a file uses the compression method <метод>, which the superblock does not announce.`, `…unknown compression method <N>.`, `…an empty compressed cluster.`, `…a compressed cluster unpacks to <N> bytes instead of <M>.`, `…a <метод> cluster cannot be unpacked (<подробности>).`, `Corrupt LZ4 data.`, `Corrupt LZMA data: …` | Сжатые данные файла повреждены. | Получите образ заново. Остальные файлы читаются. |
| `The EROFS image has a compressed cluster that stands for <N> MiB of data, which is more than this program unpacks at once.` | Один сжатый кластер описывает больше 64 МБ данных (ядро Linux допускает до 12 МБ). | Скорее всего образ повреждён; если нет — распакуйте его в Linux. |
| `Corrupt EROFS image: a file keeps data in the packed inode, but the image has none.`, `…a fragment lies outside the packed inode.`, `…the packed inode refers to itself.` | Повреждены фрагменты (хвосты файлов в служебном «упакованном» файле, `-Efragments`). | Ошибка относится к файлу, который читается; см. общие способы. |

## 7a. Чтение CramFS и romfs

| Сообщение | Причина | Что делать |
|---|---|---|
| `The CramFS image uses features this program does not know (flags 0x<…>).` | В суперблоке отмечены возможности новее известных (так же отказывается ядро). | Пересоберите образ из исходных файлов `mkfs.cramfs` или распакуйте его утилитой, которая эти возможности знает. |
| `The CramFS image is truncated: it should be <N> bytes, the file has <M>.`, `The romfs image is truncated…` | Файл короче, чем записано в суперблоке: образ недокачан или вырезан из прошивки не целиком. | Получите образ заново; при вырезании из прошивки возьмите столько байт, сколько указано в суперблоке. |
| Предупреждение: `The CRC of the CramFS image does not match: the image is damaged or was changed after it was made.` | Контрольная сумма суперблока не совпадает с содержимым. | Образ открыт; файлы, чьи блоки повреждены, не прочитаются (ошибка zlib). Сравните с исходной прошивкой. |
| Предупреждение: `The checksum of the romfs superblock is wrong: the kernel does not mount such an image.` | Первые 512 байт образа повреждены или изменены. | Образ открыт; сохраните его заново — FsForge запишет верную сумму. |
| Предупреждение: `The CramFS superblock has an unexpected signature.` | Подпись «Compressed ROMFS» искажена. | Обычно безопасно, если образ читается. |
| `Corrupt CramFS image: …` (the directory / the block list / a block of a file lies outside the image, a block decompresses to the wrong length, a directory is referenced more than once…), `Corrupt CramFS superblock: invalid size.` | Повреждённые метаданные или данные CramFS. | Получите образ заново. |
| `Corrupt romfs image: …` (an entry lies outside the image, a name is not terminated, the entries of a directory form a loop, the first entry is not a directory…), `Corrupt romfs superblock: invalid size.` | Повреждённые заголовки romfs. | Получите образ заново. |
| Предупреждение: `'<путь>': the hard link target is not in the image; the link was skipped.`, `'<путь>' is a hard link to the directory '<цель>' and was skipped.` | Жёсткая ссылка romfs указывает мимо заголовка объекта или на каталог. | Ссылка пропущена; при необходимости создайте файл вручную. |
| Предупреждение: `Invalid or duplicate directory entry '<имя>' in '<папка>' was skipped.`, `'<путь>' has an unknown file type (mode <…>) and was skipped.` | Запись с недопустимым или повторяющимся именем, неизвестный тип. | Запись потеряна; при необходимости создайте её вручную. |

Образ CramFS для другого порядка байтов (big-endian для MIPS и PowerPC) или с блоком больше 4 КБ
FsForge открывает и сохраняет, но ядро x86 или ARM такой образ не смонтирует — это не ошибка образа.

## 7c. Чтение F2FS

| Сообщение | Причина | Что делать |
|---|---|---|
| Предупреждение: `<N> file(s) are encrypted (file-based encryption, as on the userdata partition of Android): their content cannot be read and is shown empty.`, `<N> director(y/ies) have encrypted names: they are shown as they are stored.` | Файловое шифрование (fscrypt) раздела userdata: ключи хранятся в защищённом хранилище телефона, без них содержимое не расшифровать. | Снимите данные с работающего устройства (`adb pull`, резервная копия) — там файлы уже расшифрованы. При сохранении образа зашифрованные файлы станут пустыми. |
| Предупреждение: `The F2FS filesystem was not cleanly unmounted: what was written after its last checkpoint (fsync without a checkpoint) is not shown.` | Образ снят с работающего или внезапно выключенного устройства. | Показано состояние на последней контрольной точке — так же образ видит `fsck.f2fs`. Если нужны последние изменения, смонтируйте образ ядром Linux (оно восстановит записи после fsync) и отмонтируйте. |
| Предупреждение: `The checkpoint of the F2FS image records an error: the filesystem should be checked with fsck.f2fs.` | Ядро обнаружило ошибку и отметило это в контрольной точке. | Проверьте образ: `fsck.f2fs -f <образ>`; либо сохраните его в FsForge заново — будет записан новый образ. |
| Предупреждение: `The primary superblock of the F2FS image is damaged; the backup copy was used.` | Первая копия суперблока (блок 0) повреждена. | Образ открыт по резервной копии; сохраните его заново. |
| Предупреждение: `The F2FS image is shorter than its superblock says (<N> bytes): it may be truncated.` | Файл короче файловой системы: образ недокачан или снят не целиком. | Если обрезан только неиспользуемый хвост, файлы читаются; иначе получите образ заново. |
| `The F2FS image is truncated: block <N> lies outside the file.` | Нужный блок лежит за концом файла. | Получите образ заново целиком (`dd` всего раздела). |
| Предупреждение: `The F2FS image is made for a zoned device.` | Образ для зонированного накопителя (SMR, ZNS). | Чтение обычно работает; запись делает образ для обычного накопителя. |
| `The F2FS filesystem spans several devices, which is not supported.` | Файловая система на нескольких устройствах (`mkfs.f2fs -c`). | Распакуйте в Linux, смонтировав все устройства. |
| `The F2FS file is compressed with LZO-RLE, which is not supported.`, `…with an unknown method (<N>).` | Файл сжат методом, которого нет в программе (ошибка относится к этому файлу). | Распакуйте файл в Linux; остальные файлы читаются. |
| `Corrupt F2FS image: neither copy of the superblock is valid.`, `Corrupt F2FS image: no valid checkpoint.`, `Corrupt F2FS checkpoint: …` (invalid NAT bitmap size, invalid summary position, invalid NAT journal) | Повреждены обе копии суперблока или обе контрольные точки. | Получите образ заново; попробуйте `fsck.f2fs -f` в Linux. |
| `Corrupt F2FS image: …` (node <N> is beyond the NAT / has an invalid address / does not belong there, inode <N> has an invalid layout / extra size / compression cluster size, a directory is referenced more than once, a block lies outside the main area, an invalid directory entry…), `Corrupt compressed data in inode <N>.` | Повреждённые узлы, иноды, каталоги или сжатые данные. | Получите образ заново. Повреждение одного файла не мешает открыть образ: файл пропускается с предупреждением `'<путь>' was skipped: …`. |
| Предупреждение: `The extended attributes of inode <N> cannot be read: …` | Повреждён узел атрибутов. | Атрибуты объекта потеряны; при необходимости задайте их заново. |

## 7b. Контейнеры Android (super.img, boot.img, vendor_boot.img)

| Сообщение | Причина | Что делать |
|---|---|---|
| `Corrupt super image: neither copy of the geometry is valid.`, `… no copy of the partition metadata is valid.`, `… invalid geometry.`, `… a metadata table lies outside the metadata.`, `… the metadata lists no block device.`, `… a partition refers to extents or a group that do not exist.` | Метаданные LP повреждены во всех копиях. | Возьмите super.img из прошивки заново. |
| Предупреждение: `The primary geometry of the super image is damaged; the backup copy was used.`, `The first copy of the super metadata is damaged; another copy was used.` | Повреждена основная копия, резервная цела. | Образ открыт; при сохранении все копии записываются заново. |
| Предупреждение: `The metadata of slot <N> differs from slot 0 (the device was updated)…` | На A/B-устройстве после обновления слоты различаются (super снят с устройства). | Показаны разделы слота 0; после сохранения во всех слотах будут они. Для прошивки обычно так и нужно. |
| Предупреждение: `The super metadata has version 10.<N>, newer than this program knows (10.2).` | Метаданные новее известных. | Образ открыт; проверьте результат `lpdump` перед прошивкой. |
| `'<раздел>' holds nothing and cannot be opened.`, `… holds an unknown format…` | Пустой раздел (например, `system_b`) или раздел без поддерживаемой файловой системы. | Выберите другой раздел (`vol=`). |
| `The partition '<раздел>' lies on another block device than this super image.` | Retrofit-устройство: часть раздела на другом разделе GPT. | Не поддерживается. |
| `The … image has no partition '<имя>'. It holds: …`, `The file is neither a UBI image nor an Android image, so there is no volume or partition '<имя>' to open.` | Неверное имя в `vol=`. | Возьмите имя из списка (`fsforge-cli info`). |
| `The boot image has no ramdisk this program can read…` | Ramdisk пуст (boot.img с GKI-ядром без ramdisk: ramdisk в init_boot.img), сжат неподдерживаемым методом или имеет заголовок MediaTek. | Откройте init_boot.img или vendor_boot.img. |
| `Corrupt boot image: invalid header size.`, `… invalid page size.`, `Corrupt vendor boot image: invalid header.`, `… invalid vendor ramdisk table.`, `… a vendor ramdisk lies outside its section.`, `The vendor boot image has header version <N>; versions 3 and 4 are known.` | Повреждённый или незнакомый заголовок. | Получите образ заново. |
| `The boot image is truncated: its header describes <N> bytes, the file has <M>.` | Файл короче, чем указано в заголовке. | Снимите образ раздела целиком. |
| `Not a legacy LZ4 stream.`, `The LZ4 data is truncated.`, `Corrupt LZ4 data…` (в составе `The cpio archive is corrupt: …`) | Повреждённый ramdisk в lz4. | Получите образ заново; `lz4 -t` проверит данные. |

## 8. Чтение архивов tar

| Сообщение | Причина | Что делать |
|---|---|---|
| `The file is not a tar archive.` | Не tar. | Проверьте файл. |
| `The archive is compressed with <метод>, which is not supported. Use gzip, xz, zstd or bzip2.` | lz4 (кадровый формат), lzma, compress. | Перепакуйте: `unlzma -c a.tar.lzma \| gzip > a.tar.gz` или через 7-Zip. |
| `The archive is corrupt: <подробности>` | Ошибка в сжатых данных. | Скачайте заново; `gzip -t` / `xz -t` / `zstd -t`. |
| `XZ files with BCJ filters are not supported for streaming.` | Архив `.tar.xz` сжат с BCJ-фильтром. | Перепакуйте без фильтра: `xz -dc a.tar.xz \| xz -c > b.tar.xz`. |
| `The XZ dictionary is too large.` | Словарь xz больше 1 ГБ. | Перепакуйте с обычными параметрами `xz -6`. |
| `The tar archive is truncated.`, `Corrupt tar archive: …` (bad header checksum after <N> entries, oversized extended header / long name / long link name, negative member size, bad sparse map, sparse member data is truncated), `Corrupt tar header: invalid <поле> field.` | Архив обрезан или повреждён. | Скачайте заново. `tar -tvf` покажет, до какой записи архив цел. |
| Предупреждение: `A malformed pax extended header was ignored.` | Повреждён расширенный заголовок pax; запись прочитана без него. | У записи могут пропасть длинное имя, точное время или атрибуты. |
| Предупреждение: `'<имя>': the path contains '..' and was skipped.` | Путь выходит за пределы архива (защита от «path traversal»). | Запись намеренно пропущена — так и должно быть. |
| Предупреждение: `'<путь>' is replaced by a directory required by '<имя>'.` | В архиве есть файл и папка с одним путём. | Оставлена папка. Проверьте архив, если это неожиданно. |
| Предупреждение: `'<имя>': the hard link target '<цель>' is not in the archive; the link was skipped.` | Жёсткая ссылка на файл, которого нет в архиве (архив собран не целиком). | Добавьте недостающий файл вручную. |
| Предупреждение: `'<имя>' was skipped: <причина>` | Запись с недопустимым именем или конфликтом. | Запись потеряна; при необходимости добавьте её вручную. |

## 8a. Чтение архивов cpio (initramfs)

| Сообщение | Причина | Что делать |
|---|---|---|
| `Not a cpio archive (no newc, crc or odc header).` | Файл назван как cpio, но внутри не он. | Проверьте файл; `file` покажет, что внутри. |
| `The archive uses the old binary cpio format, which is not supported. Only newc, crc and odc archives are read.` | Старый двоичный формат (`-H bin`). | Перепакуйте GNU cpio в newc (см. раздел 1). |
| `The cpio archive is compressed with <метод>, which is not supported. Use gzip, xz, zstd or bzip2.` | Initramfs сжат lz4 (кадровый формат) или lzma. | Перепакуйте: `lz4 -dc initrd.img \| gzip -9 > initrd.gz`. |
| `The cpio archive is corrupt: <подробности>` | Ошибка в сжатых данных. | Скачайте заново; `gzip -t` / `xz -t` / `zstd -t`. |
| `The cpio archive is truncated.`, `Corrupt cpio header: invalid <поле> field.`, `Corrupt cpio header: invalid name size.`, `Corrupt cpio archive: no header where entry <N> should start.`, `Corrupt cpio archive: file data where a header should be.`, `Corrupt cpio archive: the symbolic link '<имя>' has an invalid length.` | Архив обрезан или повреждён. | Скачайте заново; `cpio -tv < a.cpio` покажет, до какой записи архив цел. |
| Предупреждение: `The file holds <N> cpio archives one after another (for example CPU microcode in front of the initramfs). They are shown together; saving writes a single archive, which the kernel then no longer reads as early microcode.` | Initramfs дистрибутива: несжатый архив с микрокодом процессора, за ним сжатая система. Архивы показаны одним деревом, как их видит ядро. | Если важна ранняя загрузка микрокода, удалите `kernel/` из образа, сохраните и допишите исходный архив микрокода спереди: `cat early.cpio new.cpio.gz > initrd.img` (так делают `dracut` и `mkinitramfs`). |
| Предупреждение: `The cpio archive ends without its trailer: it may be truncated.` | Нет записи `TRAILER!!!`. | Прочитанное показано; если файлов не хватает — архив обрезан. |
| Предупреждение: `<N> file(s) do not match the checksum stored in the archive: their content is damaged.` | Архив crc (070702): сумма байтов файла не совпала с заголовком. | Файлы повреждены; ядро такой initramfs тоже отвергнет. Получите архив заново. |
| Предупреждение: `The file goes on after the cpio archive with <что> (from offset 0x<…>); it was ignored.`, `The compressed data goes on after the cpio archive with something else; it was ignored.` | После архива лежат не нули и не следующий архив (например, подпись или сжатие, которое не поддерживается). | Обычно безопасно; часть initramfs, сжатая lz4 (кадровый формат) или lzma, при этом не прочитана. |
| Предупреждение: `'<имя>' has an unknown file type (mode <…>) and was skipped.`, `'<имя>': the symbolic link has an empty target and was skipped.` | Запись, которую нельзя представить. | Запись потеряна; при необходимости создайте её вручную. |
| Предупреждение: `'<имя>': the path contains '..' and was skipped.`, `'<путь>' is replaced by a directory required by '<имя>'.`, `'<имя>' was skipped: <причина>` | Как у tar (раздел 8). Поздняя запись с тем же путём заменяет раннюю — как при распаковке ядром. | См. раздел 8. |

## 9. Ошибки сжатых данных

Появляются при открытии, извлечении или сохранении, когда читается конкретный блок данных:

`Corrupt gzip block…`, `Corrupt XZ block…` / `Corrupt XZ data…` (bad stream header, checksum mismatch, truncated…),
`Corrupt LZMA block` / `Corrupt LZMA data…`, `Corrupt LZ4 block.`, `Corrupt LZO block.`,
`Corrupt Zstandard block…`, `Not an XZ stream.`, `Unsupported XZ stream flags.`,
`Unsupported XZ block header flags.`, `Unsupported XZ filter 0x<…>.`,
`XZ BCJ filters with a start offset are not supported.`, `The XZ filter '<имя>' is not supported.`

Причина — повреждённые данные или редкие параметры сжатия. Если ошибка возникает при извлечении
одного файла — повреждён именно он: извлеките остальное. Если при открытии — см. общие способы.

### Сжатые образы и архивы (gz, xz, zst, bz2, zip, 7z)

| Сообщение | Причина | Что делать |
|---|---|---|
| `There is not enough free space in the folder for temporary files ('<папка>') to unpack the image: it needs <N>, <M> are free. …` | Образ из zip или 7z распаковывается во временную папку, а на её диске не хватает места. | «Настройки → Папка для временных файлов»: выберите папку на диске, где места больше (например, `D:\Temp`). |
| `The disk with the folder for temporary files ('<папка>') ran out of space while the image was unpacked …` | Место кончилось во время распаковки gz, xz, zst или bz2 (их распакованный размер заранее не известен, обычно он в несколько раз больше файла). | То же: другая временная папка или освободите место. |
| `The <вид> archive has no file '<имя>'. It holds: <список>.` | В `fsforge-cli` указан `entry=` с именем, которого в архиве нет. | Возьмите имя из списка (регистр не важен, папку можно не указывать). |
| `The <вид> archive holds no files.`, `'<имя>' in the <вид> archive is empty.` | В архиве нет файлов или выбранный файл пуст. | Проверьте архив; образа в нём нет. |
| `The file is not a zip or 7z archive, so there is no file '<имя>' in it to open.` | `entry=` указан для файла, который не является архивом. | Уберите `entry=`. |
| `The 7z archive is encrypted (AES), which is not supported.` | Архив защищён паролем. | Распакуйте его 7-Zip с паролем и откройте образ (или перепакуйте без пароля). |
| `The 7z archive uses the compression method <метод>, which is not supported.`, `… uses the coder BCJ2, which is not supported.` | PPMd, BCJ2, Deflate64 и другие редкие методы. | Перепакуйте 7-Zip методом LZMA2 (по умолчанию): `7z a -m0=lzma2 new.7z образ.img`. |
| `The 7z archive applies the <фильтр> filter to <N> MiB of data; at most 512 MiB are supported.` | Фильтр для исполняемых файлов (BCJ, ARM…) обрабатывается в памяти. | Для образов эти фильтры не нужны: перепакуйте с `-mf=off`. |
| `'<имя>' in the zip archive cannot be unpacked: … Only the Deflate and Stored methods without encryption are supported.` | Файл в zip сжат LZMA, bzip2, zstd, Deflate64 или зашифрован. | Перепакуйте zip обычным сжатием (Deflate) или используйте 7z. |
| `Corrupt 7z archive: …`, `Corrupt zip archive: …`, `The CRC of '<имя>' in the archive does not match: the archive is damaged.`, `The archive is truncated or damaged: '<имя>' ends after <N> of <M> bytes.`, `The 7z archive is truncated: its data lies beyond the end of the file.` | Архив повреждён или скачан не полностью. | Скачайте архив заново и сверьте контрольную сумму (`sha256sum -c`). |
| `Corrupt bzip2 data: …` (a block CRC does not match, unexpected end of file, bad Huffman code…), `Corrupt LZMA2 data: …`, `Corrupt LZMA data: …` | Сжатые данные повреждены. | То же. |
| `The <вид> data is damaged: …`, `The <вид> archive is damaged: …` | Ошибка распаковки gzip, xz, zstd, bz2, zip или 7z при открытии. | То же. |

## 10. Сохранение

### Общие

| Сообщение | Причина | Что делать |
|---|---|---|
| `'<путь>' is a folder.` | В качестве имени выбрана существующая папка. | Укажите имя файла. |
| `'<путь>' is read-only. Remove the attribute or choose another name.` | У файла атрибут «Только чтение». | Снимите его (Свойства файла в Проводнике) или выберите другое имя. |
| `'<путь>' cannot be replaced: <текст системы>` | Файл открыт другой программой (эмулятор, программа записи карт, антивирус). | Закройте эту программу и повторите. |
| `The image could not be written: <текст системы>` | Нет места на диске, нет прав, ошибка ввода-вывода. | Освободите место (нужно не меньше размера нового образа плюс место под проверку), выберите другую папку. Для сжатых образов дополнительно нужно место во временной папке (Настройки → «Папка для временных файлов»). |
| `The image could not be saved as '<путь>': <текст системы>` | Временный файл записан, но переименовать его не удалось. | Проверьте права на папку и что файл не занят; повторите. |
| `The new image was written to '<путь>.fsforge-tmp' but could not replace '<путь>': <текст>. Rename the temporary file manually.` | Перезапись исходного файла: исходник уже закрыт, новый образ готов, но заменить файл не удалось. | Новый образ цел и проверен — он в `<путь>.fsforge-tmp`. Закройте программу, которая держит файл, и переименуйте временный файл вручную. Прежний файл, если включено «оставлять .bak», лежит в `<путь>.bak`. |
| «Отменено» | Операция отменена кнопкой «Отмена». | Исходный файл не изменён, временный удалён. |

### Проверка после записи

| Сообщение | Причина | Что делать |
|---|---|---|
| `The written image does not match the source and was discarded; the original file is untouched.` и список строк ниже | Записанный образ прочитан обратно и не совпал с деревом в программе. Исходный файл не тронут. | Это защита от ошибок записи. Попробуйте другой формат или параметры (например, другой размер блока). Если повторяется — это ошибка программы: сообщите о ней со списком отличий. В крайнем случае проверку можно выключить (Настройки → «После записи проверить» → «не проверять»), но тогда результат стоит проверить в Linux (`e2fsck -fn`, `unsquashfs -l`). |
| `<путь>: type is <A>, expected <B>.`, `…: mode is …`, `…: owner is …`, `…: modification time is …`, `…: extended attributes differ.`, `…: link target differs.`, `…: device is …`, `…: size is …`, `…: the hard link was not preserved.`, `…: the file content differs.` (только при проверке содержимого), `…: missing in the written image.`, `…: unexpected entry in the written image.` | Строки списка отличий: что именно не совпало. | См. строку выше. |
| `The written file is not a disk image with a partition table.`, `Partition <N> moved from offset <A> to <B>.`, `The number of partitions changed.`, `The data <до/после/вокруг> partition <N> differs from the source disk image.`, `The written disk image is shorter than expected: …` | При сохранении образа диска изменилось что-то вне редактируемого раздела. | Не должно происходить; запись отменена, исходник цел. Сообщите об ошибке. |
| `The compressed image cannot be unpacked again (<текст>) and was discarded; the original file is untouched.`, `The compressed image does not unpack to the data that was written and was discarded; the original file is untouched.` | Сжатый `.gz/.xz/.zst` не распаковался обратно в то, что было записано. | Повторите; проверьте место во временной папке и исправность диска (ошибки памяти и диска тоже дают такой результат). |

### Образ диска (раздел внутри SD-карты)

| Сообщение | Причина | Что делать |
|---|---|---|
| `The content does not fit into partition <N> (<размер>), which cannot be enlarged <почему>. Remove some files or save the filesystem alone.` | Новое содержимое больше раздела, а раздел нельзя увеличить: за ним идут другие разделы, это GPT, логический раздел MBR или в файле после раздела есть данные. | Удалите лишние файлы; для SquashFS выберите более сильное сжатие (xz, большой блок); либо сохраните файловую систему отдельно (в окне параметров выключите «Сохранить образ диска целиком…»; в `fsforge-cli` — `container=0`) и запишите её на карту другим способом. |
| `The filesystem takes <размер>, but partition <N> holds only <размер> and cannot be enlarged <почему>. Remove some files, choose a stronger compression or save the filesystem alone.` | То же для SquashFS. | То же. |
| `The requested filesystem size of <размер> exceeds partition <N> (<размер>), which cannot be enlarged <почему>.`; в окне параметров — «Раздел <N> вмещает <размер> и не может быть увеличен — укажите размер не больше этого.» | Задан размер ext больше раздела. | Укажите размер не больше раздела или «минимальный». |
| `The filesystem takes <размер>; partition <N> cannot be made that large in an MBR partition table.` | Раздел MBR не может быть больше 2 ТиБ. | Уменьшите содержимое. |
| Предупреждение: `The filesystem no longer fits into partition <N> (<размер>): the partition was enlarged to <размер>. The card must hold at least <размер>.` | Последний раздел MBR увеличен, образ диска стал больше. | Убедитесь, что карта памяти не меньше указанного размера. |
| Предупреждение: `The data that followed the old filesystem in the partition (the writable overlay of the system) was cleared, because the size of the filesystem changed. The device creates a new, empty overlay on first boot.` | OpenWrt: за SquashFS в разделе хранились настройки (overlay); при изменении размера они стёрты. | Ожидаемо: устройство загрузится «как после сброса». Сохраните настройки заранее (в OpenWrt — «Backup»), чтобы восстановить их после прошивки. |
| Предупреждение: `Less than 1 MiB is left in partition <N> behind the filesystem. An OpenWrt system keeps its settings there; without that room it runs with a temporary overlay in memory.` | После SquashFS почти не осталось места под настройки. | Уменьшите содержимое или выберите более сильное сжатие, иначе настройки не будут сохраняться между перезагрузками. |

### Запись ext2 / ext3 / ext4

| Сообщение | Причина | Что делать |
|---|---|---|
| `The content does not fit into an image of <N> MiB. Increase the image size or let it be chosen automatically.` | Задан фиксированный размер, содержимое не помещается. | Увеличьте размер или выберите «минимальный» с запасом. |
| `The requested ext image size is too small.`, `The requested ext image size is too small for the filesystem metadata and the journal.` | Размер слишком мал даже для служебных структур. | Увеличьте размер или отключите журнал. |
| `The image cannot hold <N> inodes with this geometry; increase the image size.` | Файлов больше, чем помещается инодов. | Увеличьте размер образа. |
| `Ext images larger than 2^32 blocks are not supported.` | Образ больше 16 ТиБ (при блоке 4 КБ). | Уменьшите размер. |
| `The target of the symbolic link '<путь>' is <N> bytes long; with <B>-byte blocks ext can store at most <B−1>. Choose a larger block size.` | Очень длинная цель ссылки. | Выберите блок 4096 байт. |
| `'<путь>' has <N> hard links; ext supports at most 65000.` | Слишком много жёстких ссылок на один файл. | Удалите лишние жёсткие ссылки (число ссылок видно в «Свойствах») или сохраните в SquashFS или tar. |
| `The file is too large for an ext2/ext3 block map; use ext4.` | Очень большой файл не описывается картой блоков ext2/ext3. | Сохраните в ext4 или с блоком 4096. |
| Предупреждение: `'<путь>' has more than 64998 subdirectories: the dir_nlink feature was enabled, the image needs the ext4 driver to be mounted.` | В одной папке очень много подпапок. | Образ смонтирует только драйвер ext4 (ext3-драйвер старых ядер — нет). Обычно не важно. |
| Предупреждение: `The inode count was raised from <A> to <B> to hold all files.` | Заданное число инодов меньше числа файлов. | Ничего делать не нужно. |
| Предупреждение: `<путь>: extended attribute '<имя>' cannot be stored in ext and was skipped.` | Атрибут из пространства имён, которого нет в ext. | Атрибут потерян. Если он нужен — выберите формат, который его хранит (tar pax), или переименуйте атрибут. |
| Предупреждение: `<путь>: malformed POSIX ACL '<имя>' was skipped.` | Испорченное значение ACL. | Задайте ACL заново после записи (`setfacl` в Linux) или удалите атрибут в «Свойствах». |
| Предупреждение: `<путь>: the extended attributes do not fit into one <B>-byte block and were skipped.` | Атрибуты объекта слишком велики для одного блока. | Выберите блок 4096 и иноды 256 байт, или уменьшите атрибуты. |
| `The ext block size must be 1024, 2048 or 4096 bytes.`, `The inode size must be 128 or 256 bytes.`, `The image size cannot be negative.`, `The reserved block percentage must be between 0 and 50.`, `The volume label is longer than 16 bytes.` | Недопустимые параметры (обычно из `fsforge-cli`). | Исправьте параметр. |

### Запись SquashFS

| Сообщение | Причина | Что делать |
|---|---|---|
| `The SquashFS block size must be a power of two between 4 KiB and 1 MiB.` | Неверный `block=`. | Выберите 4096 … 1048576 (степень двойки). |
| `Writing with the <метод> compressor is not supported.`, `Writing LZO compressed SquashFS images is not supported; choose another compressor.`, `Writing the legacy LZMA SquashFS compression is not supported; choose XZ instead.` | LZO и старый LZMA только читаются. | Выберите gzip, xz, lz4 или zstd. |
| `Invalid XZ lc/lp/pb values…`, `The XZ dictionary size must be between 8 KiB and the block size.` | Неверные параметры xz (`fsforge-cli`). | Исправьте параметры или уберите их. |
| `SquashFS supports at most 65536 distinct user and group ids.` | Слишком много разных владельцев. | Включите в параметрах сохранения «Сделать владельцем всех объектов root (0:0)» (`allroot=1` в `fsforge-cli`) или уменьшите число разных UID/GID. |
| `The inode table is too large for SquashFS.`, `The directory table is too large for SquashFS.` | Предел формата (сотни миллионов объектов). | Разделите содержимое на несколько образов. |
| Предупреждение: `<путь>: extended attribute '<имя>' cannot be stored in SquashFS and was skipped.` | SquashFS хранит только `user.`, `trusted.` и `security.`. | Атрибут потерян; если нужен — ext или tar pax. |

### Запись UBIFS и UBI

| Сообщение | Причина | Что делать |
|---|---|---|
| `The UBIFS min. I/O unit size must be a power of two.`, `The UBIFS erase block (LEB) size must be at least <N> bytes.` / `at most <N> bytes.`, `…must be a multiple of the min. I/O unit size and of 8.` | Неверные размеры страницы или блока стирания UBIFS. | Возьмите значения у вашей флеш-памяти (`mtdinfo -a`, `cat /proc/mtd`): размер логического блока = размер блока стирания минус заголовки UBI (для NAND 128 КБ со страницей 2048 — 126976, с подстраницами 512 — 129024, для NOR 64 КБ — 65408). |
| `The maximum number of UBIFS erase blocks must be at least <N>.`, `The maximum number of UBIFS erase blocks is too low for the fixed areas of the filesystem.` | Задан слишком малый предел размера файловой системы. | Увеличьте «Макс. число блоков стирания» или оставьте 0. |
| `The UBIFS index fanout must be between 3 and <N>.`, `UBIFS needs at least one erase block for orphans.`, `Unknown UBIFS compressor.`, `UBIFS sizes cannot be negative.` | Неверные параметры. | Верните значения по умолчанию. |
| `The UBIFS log is too small for the journal: at least <N> erase blocks are needed.` | Заданный журнал не помещается в лог. | Уберите свои размеры журнала и лога. |
| `The content needs <N> erase blocks, more than the maximum of <M> set for the filesystem.` | Содержимое не помещается в заданный предел. | Увеличьте «Макс. число блоков стирания» или удалите файлы. |
| `The UBIFS geometry is not usable: the table of LEB properties does not fit into an erase block.` | Слишком малый блок стирания для заданного максимального размера. | Уменьшите максимальный размер или возьмите больший блок. |
| `The target of the symbolic link '<путь>' cannot be stored in UBIFS: it must be 1 to 4095 bytes long.` | Цель ссылки слишком длинная. | Сократите цель или удалите ссылку. |
| Предупреждение: `'<путь>': the extended attribute '<имя>' was not stored. UBIFS keeps only the user, trusted and security namespaces.`, `…is too large for UBIFS (4096 bytes at most) and was not stored.` | У атрибута пространство имён, которого нет в UBIFS (например, `system.posix_acl_access`), или он слишком велик. | Атрибут потерян в сохранённом образе. |
| `'<путь>' is too large for UBIFS.`, `The content of '<путь>' changed while the image was being written.` | Файл больше 16 ТБ; либо исходный файл изменился во время записи. | Повторите сохранение. |
| `The UBIFS erase block size (<N>) must stay the erase block size of the UBI volume (<M>).` | Для файловой системы в томе UBI указан иной блок стирания, чем у образа. | Верните размер блока из образа: ядро не смонтирует иначе. |
| Предупреждение: `The filesystem no longer fits into volume <том> (<N> erase blocks): the volume was enlarged to <M> erase blocks. The flash must have room for it.` | Файловая система выросла. Том в образе UBI увеличен. | Проверьте, что в флеш-памяти хватит блоков; иначе удалите файлы. |
| `Only UBIFS and SquashFS can be wrapped into a UBI image.` | Для нового образа UBI выбрана ext или tar. | Выберите UBIFS или SquashFS. |
| `The UBI min. I/O unit size must be a power of two.`, `The UBI sub-page size must be a power of two that is not larger than the min. I/O unit.`, `The UBI erase block size must be between 4 KiB and 16 MiB and a multiple of the min. I/O unit.`, `The UBI erase block is too small for the headers.` | Неверная геометрия образа UBI. | См. выше: размеры берутся из параметров флеш-памяти. |
| `The UBI volume name must be 1 to 127 bytes long.`, `The UBI volume number is out of range.`, `The UBI volume number <N> is used twice.`, `The UBI volume name '<имя>' is used twice.`, `A UBI volume name is longer than 127 bytes.` | Неверное имя или номер тома. | Исправьте. |
| `Invalid UBI geometry.` | Внутренняя проверка заголовков. | Сообщите об ошибке. |
| `The written image does not match the source and was discarded; …` с `Volume <том> of the UBI image is missing or differs in the written image.`, `The content of volume <том> differs from the source UBI image.`, `The written file is not a UBI image.` | Проверка после записи: остальные тома образа UBI в новом файле не совпали с исходными. | Исходный файл не изменён. Если повторяется — это ошибка программы. |

### Запись JFFS2

| Сообщение | Причина | Что делать |
|---|---|---|
| `The JFFS2 erase block size must be a power of two between 4 KiB and 16 MiB.` | Неверный размер блока стирания. | Возьмите размер блока стирания вашей флеш-памяти (`cat /proc/mtd`, столбец `erasesize`); у NOR обычно 64 КБ, у NAND 128 КБ. |
| `The JFFS2 page size must be a power of two of at least 512 bytes and at most half an erase block.`, `The JFFS2 clean marker size must be a multiple of 4 between 12 and 256 bytes.`, `Writing JFFS2 with the <метод> compressor is not supported.` | Неверные параметры (обычно из `fsforge-cli`). | Уберите эти параметры: значения по умолчанию совпадают с `mkfs.jffs2`. |
| `'<путь>': JFFS2 names are at most 254 bytes long.` | Имя длиннее предела JFFS2. | Сократите имя. |
| `'<путь>': owner <U>:<G> cannot be stored in JFFS2, which keeps 16-bit user and group ids.` | UID или GID больше 65535. | Смените владельца в «Свойствах» или включите «Сделать владельцем всех объектов root (0:0)» (`allroot=1`). |
| `'<путь>': the link target is longer than <N> bytes.`, `'<путь>': a symbolic link needs a target.` | Цель ссылки длиннее страницы данных (4 КБ по умолчанию) или пустая. | Сократите цель ссылки. Учтите: `mkfs.jffs2` и ядро сами создают ссылки не длиннее 254 байт. |
| `'<путь>' is larger than 4 GiB, the limit of JFFS2.` | Файл больше 4 ГБ. | Такой файл в JFFS2 не помещается. |
| `A JFFS2 node of <N> bytes does not fit into an erase block of <M> bytes.` | Блок стирания слишком мал для узла (большой атрибут или страница). | Увеличьте размер блока стирания или уменьшите страницу данных. |
| Предупреждение: `Extended attributes of <N> object(s) were not written: JFFS2 attributes need a kernel built with CONFIG_JFFS2_FS_XATTR, so they are written only when the option to store them is on.` | Атрибуты (SELinux-метки, capabilities, ACL) по умолчанию не пишутся: ядро без поддержки атрибутов JFFS2 отказывается монтировать образ с ними. | Если ядро устройства собрано с `CONFIG_JFFS2_FS_XATTR`, включите «Записывать расширенные атрибуты и ACL» (`xattrs=1`). |
| Предупреждение: `<путь>: extended attribute '<имя>' cannot be stored in JFFS2 and was skipped.`, `…is too large for JFFS2 and was skipped.`, `<путь>: malformed POSIX ACL '<имя>' was skipped.` | JFFS2 хранит только user, security, trusted и POSIX ACL; атрибут больше половины блока стирания не помещается. | Атрибут потерян в сохранённом образе. |
| `'<путь>' changed while it was being written.` | Исходный файл изменился во время сохранения. | Повторите сохранение. |
| `Only UBIFS and SquashFS can be wrapped into a UBI image.` | Для JFFS2 выбрано «Упаковать в образ UBI» (`ubi=1`). | JFFS2 работает прямо на флеш-памяти, без UBI: сохраните отдельным файлом. |

В окне «Сохранить как» под размером блока стирания может появиться подсказка: размер исходного образа
определён по расположению узлов. Это не ошибка, но перед записью на устройство проверьте размер блока.

### Запись FAT

| Сообщение | Причина | Что делать |
|---|---|---|
| `'<путь>' cannot be stored in FAT: it contains '<символ>'. Rename it first.`, `…it ends with a space or a dot…`, `…it is longer than 255 characters…` | Имя недопустимо в FAT: символы `" * / : < > ? \ \|` и управляющие, пробел или точка в конце, больше 255 символов. | Переименуйте объект (F2) и сохраните снова. |
| `'<путь>' and '<путь>' differ only in case, which FAT does not distinguish. Rename one of them first.` | В одной папке есть, например, `README` и `readme`: в FAT это одно имя. | Переименуйте или удалите один из них. |
| `'<путь>' is larger than 4 GiB, the limit of a file in FAT.` | Файл больше 4 ГБ − 1 байт. | В FAT такой файл не помещается; сохраните образ в ext4 или разрежьте файл. |
| `The content does not fit into a <размер> FAT filesystem: it needs <N> clusters of <K> bytes, there are <M>.`, `The content does not fit into the FAT filesystem (<N> clusters of <K> bytes).` | Файлы не помещаются в заданный размер (или в раздел образа диска, который нельзя увеличить). | Удалите лишние файлы или увеличьте размер в «Сохранить как» (`size=` в `fsforge-cli`); можно также уменьшить кластер — на множестве мелких файлов это экономит место. |
| `FAT12 / FAT16 / FAT32 cannot be made <размер> large [with <K>-byte clusters]: it needs between <A> and <B> clusters. Choose another type, cluster size or size.` | Тип FAT определяется числом кластеров: FAT12 — до 4084, FAT16 — от 4085 до 65524, FAT32 — от 65525. При заданных размере и кластере выбранный тип получиться не может (например, FAT32 размером 16 МБ с кластером 4 КБ). | Выберите тип «по размеру», другой размер кластера или другой размер образа. FAT32 не бывает меньше ~33 МБ (и такой маленький — только с кластером 512 байт). |
| `No FAT layout fits <размер> [with <K>-byte clusters].`, `No FAT layout can hold the content.` | Размер слишком мал даже для служебных областей или слишком велик для FAT32 с этим кластером. | Задайте другой размер или кластер «авто». |
| `The FAT sector size must be 512, 1024, 2048 or 4096 bytes.`, `The FAT cluster size must be a power of two between the sector size and 64 KiB.`, `The FAT size cannot be negative.`, `A FAT filesystem has one or two FATs.`, `Invalid FAT layout parameters.` | Неверные параметры (обычно из `fsforge-cli`). | Уберите эти параметры: значения по умолчанию совпадают с `mkfs.fat`. |
| ``A FAT volume label has at most 11 characters: letters, digits, spaces and !#$%&'()-@^_`{}~.`` | Метка длиннее 11 символов или с недопустимыми символами (в том числе не латиница). | Сократите метку; используйте латинские буквы, цифры, пробел. |
| Предупреждение: `<N> object(s) cannot be stored in FAT (symbolic links, devices, FIFOs, sockets) and were left out: <список>.` | FAT хранит только файлы и папки. | Ожидаемо для загрузочного раздела. Если ссылки нужны, сохраните образ в ext4 или SquashFS. |
| Предупреждение: `FAT has no hard links: <N> additional name(s) of hard-linked files were stored as separate copies.` | Жёсткие ссылки записаны отдельными файлами с тем же содержимым. | Ожидаемо; учтите, что образ займёт больше места. |
| `'<путь>' changed while it was being written.` | Исходный файл изменился во время сохранения. | Повторите сохранение. |
| `Only UBIFS and SquashFS can be wrapped into a UBI image.`; в окне параметров — «FAT сохраняется отдельным файлом: в том UBI он не записывается.» | Для FAT выбрано «Упаковать в образ UBI» (`ubi=1`) или исходный образ — UBI. | FAT не работает поверх UBI: сохраните его отдельным файлом. |

При записи FAT теряются владельцы, права (кроме «только чтение»), расширенные атрибуты и время
точнее 2 секунд — об этом не предупреждается, это свойство формата. Проверка после записи сравнивает
только то, что FAT хранит. В окне «Сохранить как» для раздела образа диска размер FAT не задаётся:
он равен размеру раздела (подсказка «В образе диска FAT занимает раздел целиком»).

### Запись EROFS

| Сообщение | Причина | Что делать |
|---|---|---|
| `Unknown EROFS compression method.`, `The Zstandard level must be between 1 and 22.`, `The DEFLATE level must be between 1 and 9.` | Неверные `comp=` или `level=` в `fsforge-cli`. | `comp=` — `none`, `lz4`, `lz4hc`, `lzma`, `deflate` или `zstd`; уровень deflate 1–9, zstd 1–22. |
| `The EROFS cluster size must be a multiple of 4 KiB between 4 KiB and 1 MiB.` | Неверный `pcluster=`. | Возьмите 4096 … 1048576, кратно 4096. |
| `The LZ4HC level must be between 1 and 12.`, `The EROFS LZMA dictionary size must be between 4 KiB and 8 MiB.` | Неверные `level=` или `dict=`. | Исправьте или уберите параметр. |
| `An EROFS volume name has at most 16 bytes.`; в окне параметров — «Метка тома не длиннее 16 байт.» | Метка тома длиннее 16 байт (русская буква — 2 байта). | Сократите метку. |
| `The image would be larger than 16 TiB, the limit of this EROFS layout.` | Номера блоков в записываемом виде EROFS — 32-битные. | Разделите содержимое на несколько образов. |
| Предупреждение: `'<путь>': the extended attribute '<имя>' was dropped; EROFS holds only user.*, trusted.*, security.* attributes and POSIX ACLs.` | Атрибут из другого пространства имён (например, `system.` кроме ACL). | Атрибут потерян; если нужен — ext или tar pax. |
| Предупреждение: `'<путь>': the extended attribute '<имя>' was dropped; its name or value is too long for EROFS.`, `'<путь>': the extended attributes take more than 256 KiB and were dropped.` | Имя атрибута длиннее 255 байт, значение больше 65535 байт или все атрибуты объекта вместе больше 256 КБ. | Атрибуты потеряны; сохраните образ в ext4, если они нужны. |
| `Only UBIFS and SquashFS can be wrapped into a UBI image.` | Для EROFS выбрано «Упаковать в образ UBI» (`ubi=1`). | Новый образ UBI делается только вокруг UBIFS и SquashFS. EROFS, открытый из тома существующего образа UBI, сохраняется в этот том. |
| Смонтировать образ не удаётся: `erofs: unsupported …`, `can't find valid erofs superblock`, ошибка ввода-вывода при чтении файлов | Ядро устройства старше, чем требует выбранный вид образа: кластеры больше 4 КБ — Linux 5.13, lzma — Linux 5.16 (и `CONFIG_EROFS_FS_ZIP_LZMA`), deflate — Linux 6.6 (`CONFIG_EROFS_FS_ZIP_DEFLATE`), zstd — Linux 6.10 (`CONFIG_EROFS_FS_ZIP_ZSTD`); сам EROFS со сжатием lz4 — Linux 5.4. | Сохраните образ со сжатием lz4hc и кластером 4 КБ — его читает любое ядро с EROFS. |

EROFS хранит всё, что есть в дереве: владельцев, права, время (с секундной точностью), ссылки,
устройства, сокеты, жёсткие ссылки, расширенные атрибуты `user.`, `trusted.`, `security.` и POSIX ACL.
«Разреженность» файлов не сохраняется: нули сжимаются, а без сжатия записываются как есть.

### Запись F2FS

| Сообщение | Причина | Что делать |
|---|---|---|
| `The content does not fit into an F2FS filesystem of <N> MiB: it needs about <M> MiB of data in <K> segments, the filesystem has room for <L> MiB.`, `The content does not fit into the F2FS filesystem.` | Заданный размер (или раздел образа диска) меньше, чем нужно содержимому вместе с резервом под сборку мусора. | Оставьте размер пустым (наименьший подходящий), увеличьте его или удалите часть файлов. Последний раздел MBR образа диска увеличивается сам. |
| `An F2FS filesystem of <N> MiB is too small (or too large) to be made.` | Меньше 40 МБ F2FS не бывает: резерв и запас под сборку мусора занимают несколько десятков сегментов по 2 МБ. | Задайте больший размер или выберите ext4 / SquashFS для маленьких разделов. |
| `'<путь>': the name is longer than 255 bytes, the limit of F2FS.`, `A symbolic link target longer than 4096 bytes cannot be stored in F2FS.` | Ограничения формата. | Переименуйте объект или сократите ссылку. |
| `The directory '<путь>' holds too many entries for F2FS.`, `The content has more objects than this F2FS filesystem can hold.`, `A file is too large for F2FS.`, `The content is too large for an F2FS image.` | Превышены пределы формата (около 4 млрд объектов, файл до 3,94 ТБ). | Разделите содержимое. |
| Предупреждение: `The extended attributes of <N> object(s) do not fit into an F2FS xattr node (4 KiB) and were left out.` | Все атрибуты объекта вместе больше места в узле атрибутов. | Атрибуты этих объектов потеряны; если они нужны — ext4 или EROFS. |
| `The F2FS volume name must not be longer than 512 characters.`, `The F2FS size must not be negative.`; в окне параметров — «Размер образа — целое число мегабайт…», «Запас свободного места — целое неотрицательное число мегабайт.» | Неверные параметры. | Исправьте параметр. |
| `Only UBIFS and SquashFS can be wrapped into a UBI image.` | F2FS для блочных устройств; в томе UBI он не работает. | Для NAND-флеш выберите UBIFS. |

Файлы, которые ядро хранило встроенными в инод или сжатыми, FsForge записывает обычными блоками —
ядро читает их одинаково, а сжатие включается снова при следующей записи файла (если раздел смонтирован
с `compress_extension`). Образ F2FS, сохраняемый на место, сохраняет размер, метку и UUID.

### Запись tar

| Сообщение | Причина | Что делать |
|---|---|---|
| Предупреждение: `'<путь>': sockets cannot be stored in a tar archive; the entry was skipped.` | Формат tar не хранит сокеты. | Обычно безопасно: сокеты создаются программами при запуске. |
| Предупреждение: `Extended attributes of <N> entries were not stored: the GNU tar format cannot hold them (use pax).` | Выбран диалект GNU. | Выберите «pax (POSIX)». |
| `Only a directory can be exported as an archive.` | «Экспорт в архив» вызван для файла. | Выберите папку. |
| `The tar path prefix contains invalid characters.`, `…must be empty or end with '/'.`, `…must be relative and must not contain '..'.` | Неверный префикс путей. | Используйте `./`, пустой или вида `rootfs/`. |
| `'<путь>': unexpected end of the file data.` | Данные файла оборвались при чтении (исходный образ повреждён или добавленный файл изменился). | См. «Исходные файлы изменились» ниже. |

### Запись cpio

| Сообщение | Причина | Что делать |
|---|---|---|
| Предупреждение: `<N> object(s) have extended attributes (for example SELinux labels or capabilities), which cpio does not store: they were left out.` | Формат cpio не хранит xattr. | Для Android и систем с SELinux метки задаются политикой при загрузке; capabilities выставьте скриптом в initramfs (`setcap`). |
| Предупреждение: `<N> object(s) have a modification time before 1970 or after 2106, which a cpio header cannot hold; it was clamped.` | Время вне диапазона 32-битного беззнакового поля. | Обычно безопасно. |
| `'<путь>' is larger than 4 GiB, the limit of a file in a cpio archive.` | Поле размера в newc — 32 бита. | Выберите другой формат (SquashFS, EROFS, tar). |
| `Writing odc cpio archives is not supported; choose newc.`, `The cpio path prefix must be empty or "./".`, `Invalid compression level for the cpio archive.` | Неверные параметры (CLI). | Используйте `cpioformat=newc\|crc`, `prefix=` или `prefix=./`, `level=1..9` (gzip), `1..22` (zstd), `effort=0..2` (xz). |
| `Only a directory can be written as a cpio archive.` | Запись вызвана для файла. | Выберите папку. |
| `'<путь>' became shorter while it was being written.` | Добавленный файл изменился во время записи. | См. «Исходные файлы изменились» ниже. |

### Запись CramFS и romfs

| Сообщение | Причина | Что делать |
|---|---|---|
| `'<путь>': the name is longer than 252 bytes, the limit of CramFS.`, `… longer than 127 bytes, the limit of romfs.` | Имя не помещается в формат (romfs: ядро читает имена до 127 байт). | Переименуйте объект. |
| `'<путь>' is larger than 16 MiB, the limit of a file in CramFS.` | Размер файла в CramFS — 24 бита. | Выберите SquashFS или EROFS — их читают те же ядра. |
| `The data does not fit into a CramFS image (offsets are limited to 256 MiB).`, `The metadata does not fit…`, `The image is too large for CramFS.`, `The directory '<путь>' holds too many entries for CramFS…` | Ограничения формата CramFS. | Уменьшите содержимое или выберите SquashFS / EROFS. |
| `The image is too large for romfs (4 GiB).`, `'<путь>' is larger than 4 GiB, the limit of a file in romfs.` | Смещения romfs — 32 бита. | Уменьшите содержимое. |
| Предупреждение: `<N> object(s) have an owner above 65535…`, `<N> object(s) have a group above 255: CramFS keeps only 8 bits of it (as mkfs.cramfs does).` | CramFS хранит 16 бит владельца и 8 бит группы. | Задайте владельцев с меньшими номерами или «всё принадлежит root». |
| Предупреждение: `<N> device(s) have a major or minor number above 255 (CramFS) / 65535 (romfs)…` | Номер устройства не помещается в поле. | Создавайте такие устройства при загрузке (devtmpfs, mdev). |
| Предупреждение: `CramFS has no hard links: <N> additional name(s) of hard-linked files became separate files that share the data.` | В CramFS нет жёстких ссылок; данные хранятся один раз. | Обычно безопасно: содержимое то же, но изменение одного имени не меняет другое (образ всё равно только для чтения). |
| Предупреждение: `romfs stores no owners: …`, `romfs stores only whether a file is executable: the permissions of <N> object(s)… were not kept.` | romfs не хранит владельцев и права. | Если они важны (setuid, закрытые файлы), выберите CramFS, SquashFS или EROFS. |
| Предупреждение: `<N> object(s) have extended attributes…, which CramFS/romfs does not store: they were left out.` | Форматы без расширенных атрибутов. | Метки SELinux и capabilities задайте иначе или выберите SquashFS / EROFS. |
| `The CramFS block size must be a power of two from 4096 to 65536 bytes.`, `The CramFS compression level must be from 1 to 9.`, `The CramFS volume name must not be longer than 16 bytes.`, `The romfs data alignment must be a power of two from 16 to 65536 bytes.`, `The romfs volume name must not be longer than 127 bytes.` | Неверные параметры. | Исправьте параметр в окне сохранения или в командной строке. |

### Запись контейнеров Android

| Сообщение | Причина | Что делать |
|---|---|---|
| `The new content of '<раздел>' (<размер>) does not fit into the super image: <N> is free for it.` | В super не хватает места. | Уменьшите содержимое (сжатие EROFS lz4hc/lzma вместо ext4) или удалите лишнее из других разделов. Размер super задан разметкой устройства. |
| `The partitions of group '<группа>' would take <N>, more than its maximum of <M>.` | Группа динамических разделов ограничена (обычно половиной super на A/B-устройствах). | Уменьшите содержимое разделов группы. |
| `The partition metadata does not fit into the room the super image has for it.` | Слишком много экстентов. | Сохраните ещё раз: раздел получит экстенты заново; или уменьшите его. |
| Предупреждение: `The partition '<раздел>' no longer fits where it was: it was given <N> extents in the free space…` | Раздел вырос и разбит на части. | Обычно безопасно: так же поступает Android при обновлении. |
| `The new image is <N>, but the boot partition has room for <M> before its AVB signature data…` | Образ подписан AVB и имеет фиксированный размер раздела; новый ramdisk не помещается. | Выберите более сильное сжатие (lz4 → gzip уровня 9 или xz) или уменьшите ramdisk. |
| Предупреждение: `The boot image is signed with Android Verified Boot: the signature no longer matches…` | Образ изменён, подпись — нет. | На заблокированном загрузчике образ не загрузится; переподпишите своим ключом (`avbtool add_hash_footer`) или используйте разблокированный загрузчик. |
| Предупреждение: `The boot signature of the image (GKI) no longer matches the changed ramdisk.` | Подпись boot v4 (GKI) не совпадает. | Обычно не мешает на разблокированном загрузчике. |
| Предупреждение: `The boot image grew from <N> to <M>: the partition must have room for it.` | Образ стал больше исходного файла. | Сравните с размером раздела boot на устройстве перед прошивкой. |
| `The ramdisk is too large for a boot image.`, `The image is too large for the Android sparse format.` | Ограничения форматов (4 ГБ, 2³² блоков). | Уменьшите содержимое. |

### Запись в сжатый файл или архив

| Сообщение | Причина | Что делать |
|---|---|---|
| `There is not enough free space. The new image is first written uncompressed to the folder for temporary files ('<папка>'), then compressed into '<папка>'. …` | Новый образ сначала пишется во временную папку несжатым, а затем упаковывается рядом с целевым файлом. | Освободите место или выберите временную папку на другом диске в настройках. |
| `The compressed image cannot be unpacked again (…) and was discarded; the original file is untouched.`, `The compressed image does not unpack to the data that was written and was discarded; …` | Проверка готового архива не прошла (сбой диска или памяти). | Исходный файл не изменён; повторите сохранение, проверьте диск. |
| `'<имя>' changed its size while it was being packed …` | Временный файл изменился во время упаковки (антивирус, другая программа). | Повторите сохранение; исключите временную папку из проверки «на лету». |
| `A tar archive can be compressed with gzip, xz, zstd, bzip2 or lz4, not put into a zip or 7z archive.`, то же для cpio | Архив tar или cpio сохраняется со своим собственным сжатием. | Выберите сжатие tar/cpio в окне сохранения; при необходимости упакуйте готовый `.tar` в zip отдельно. |
| Предупреждение: `The block map '<файл>' could not be updated (…); it no longer matches the image and should be deleted.`, `The checksum file '<файл>' could not be updated (…); it no longer matches '<образ>'.` | Файл рядом с образом защищён от записи или занят. | Удалите или исправьте этот файл вручную: старая карта блоков или сумма не подходят к новому образу, FlashTool откажется с ними записывать. |

Файлы, обновлённые рядом с образом (`.sha256`, `SHA256SUMS`, `.bmap`), перечисляются в сообщении о
сохранении («обновлены файлы рядом с ним: …»), в `fsforge-cli` — строками `updated: <файл>`. Это не ошибка.

### Исходные файлы изменились

| Сообщение | Причина | Что делать |
|---|---|---|
| `The added file '<путь>' can no longer be read: <текст>` | Файл, добавленный с диска, удалён, переименован или заблокирован до сохранения. Образ ссылается на него и читает только при записи. | Верните файл на место или добавьте его заново. Чтобы такого не было, включите Настройки → «Сразу копировать добавляемые файлы во временную папку». |
| `The file '<путь>' has changed since it was added to the image. Add it again.` | Размер добавленного файла изменился. | Добавьте файл заново (или включите ту же настройку). |

## 11. Правка образа

Сообщения в окнах «Создать», «Свойства», «Переименовать» и при вставке. Ядро даёт английский текст,
окна показывают русский перевод.

| Сообщение | Что делать |
|---|---|
| «Введите имя.» / `The name is empty.` | Введите имя. |
| «Имена «.» и «..» зарезервированы.» / `'.' is not allowed as a name.` | Выберите другое имя. |
| «Имя не может содержать символ «/».» / `A name cannot contain '/'.` | Уберите `/`. Вложенные папки создавайте по одной. |
| «Имя не может содержать нулевой символ.» / `A name cannot contain a NUL character.` | Уберите символ. |
| «Имя длиннее 255 байт.» / `The name is longer than 255 bytes.` | Сократите имя (кириллица занимает 2 байта на символ). |
| «Объект с таким именем уже существует…» / `'<имя>' already exists in '<папка>'.` | Выберите другое имя или удалите существующий объект. |
| «Нельзя переместить папку внутрь самой себя.», «нельзя скопировать папку внутрь самой себя» / `A directory cannot be moved / copied into itself.` | Выберите другую целевую папку. |
| «Целевой объект не является папкой.» / `'<путь>' is not a directory.`, `'<путь>' exists and is not a directory.` | Выберите папку; уберите файл, мешающий созданию папки. |
| `The root directory cannot be removed / renamed / moved.` | Корневую папку менять нельзя. |
| `A directory cannot be hard linked.` | Жёсткие ссылки возможны только на файлы; используйте символическую ссылку. |
| «Укажите, на что указывает ссылка.» / `A symbolic link target cannot be empty.` | Заполните цель ссылки. |
| `A symbolic link target cannot contain a NUL character.`, «Цель ссылки длиннее 4095 байт.» / `The symbolic link target is longer than 4095 bytes.` | Исправьте цель. |
| «Права задаются восьмеричным числом, например 0755.», «Укажите права доступа или верните прежнее значение.» | Введите права вида `0644`, `0755`, `4755`. |
| «UID — целое число от 0 до 4294967295.», «GID — …» | Введите число. Имена из `/etc/passwd` показываются подсказкой, но поле принимает только число. |
| «Время задаётся в виде ГГГГ-ММ-ДД чч:мм:сс.» | Например, `2024-05-01 12:00:00`. |
| «Старший и младший номера устройства — целые неотрицательные числа.» | Например, `1` и `3` для `/dev/null`. |
| «Имя атрибута «…» должно начинаться с пространства имён: user., security., trusted. или system.» / `An attribute name needs a namespace prefix such as "user." or "security.".`, `The attribute name is invalid.` | Например, `security.selinux`, `user.comment`. |
| «Атрибут «…» указан дважды.» | Удалите повторную строку. |
| «Значение атрибута «…»: после 0x должны идти пары шестнадцатеричных цифр…» | Двоичное значение — `0x` и чётное число шестнадцатеричных цифр (`0x0100000200`); текст вводится как есть. |
| `'<путь>' is not a regular file / not a symbolic link / not a device node.` | Действие не применимо к объекту этого типа. |
| «Не удалось переименовать» (уведомление) | Причина указана в тексте — см. строки выше. |
| «Вставка» со списком | Часть объектов не вставлена; причина указана для каждого. |
| «Путь не найден» (уведомление) | Путь, введённый в адресной строке, не существует в образе. Проверьте написание; пути начинаются с `/`. |

## 12. Добавление файлов и сборка из папки

| Сообщение | Причина | Что делать |
|---|---|---|
| `The folder '<путь>' does not exist.` | Папку удалили или переименовали. | Выберите существующую папку. |
| Предупреждение: `'<путь>' could not be read: <текст>` | Нет прав на папку или она занята. | Запустите программу с нужными правами или скопируйте папку в доступное место. |
| Предупреждение: `'<путь>' does not exist.` | Перетащенный или выбранный объект исчез. | Добавьте заново. |
| Предупреждение: `'<путь>' is a link on the host and was skipped.` | Символическая ссылка или точка соединения (junction) Windows, которую нельзя превратить в объект образа. | Добавьте сам файл, на который она указывает. Ссылки Linux, записанные в `.fsforge-meta.json` при извлечении, восстанавливаются автоматически. |
| Предупреждение: `'<путь>': the symbolic link has no target and was skipped.` | У ссылки (или заглушки ссылки) нет цели. | Создайте ссылку в программе вручную («Создать → Символическая ссылка»). |
| Предупреждение: `'<путь>' was skipped: <текст>` | Файл не читается (занят, нет прав) или имя недопустимо. | Закройте программу, держащую файл, и добавьте его снова. |
| Вопрос «Объекты с такими именами уже существуют» | Совпадение имён при добавлении. | Выберите действие; поведение по умолчанию — Настройки → «Если объект с таким именем уже есть». |

## 13. Извлечение и внешний редактор

| Сообщение | Причина | Что делать |
|---|---|---|
| Предупреждение: `'<путь>' was not extracted: the file already exists` | В папке уже есть файл, а замена выключена. | Включите Настройки → «Заменять существующие файлы» или выберите пустую папку. |
| Предупреждение: `'<путь>' was not extracted: a file / folder with the same name is in the way` | Файл и папка с одним именем (часто — имена, различающиеся только регистром). | Извлеките в пустую папку. Имена, недопустимые или совпадающие в Windows, переименовываются автоматически, а настоящие имена записываются в `.fsforge-meta.json`. |
| Предупреждение: `Symbolic links could not be created on this system (<текст>); text placeholders were written instead.` | Windows без прав на создание ссылок. | Ничего делать не нужно: цели ссылок сохранены в заглушках и `.fsforge-meta.json`, и при сборке обратно ссылки восстановятся. Для настоящих ссылок включите «Режим разработчика» Windows (или запустите программу от администратора) и выберите Настройки → «Символические ссылки» → «настоящая ссылка Windows». |
| Предупреждение: `The metadata file could not be written: <текст>` | Не записан `.fsforge-meta.json` (нет места или прав). | Без него при сборке обратно потеряются права, владельцы, ссылки и устройства. Освободите место и извлеките заново. |
| «Слишком много данных для перетаскивания» | Перетаскивание в Проводник ограничено 256 МБ. | Используйте «Извлечь». |
| «Не удалось подготовить файлы» | Ошибка извлечения во временную папку при перетаскивании. | Проверьте место во временной папке (Настройки → «Папка для временных файлов»). |
| «Не удалось открыть файл» | Нет программы для этого типа файла или редактор из настроек не найден. | Укажите программу в Настройки → «Редактор для файлов без расширения и конфигураций» (по умолчанию `notepad.exe`) или назначьте программу для этого типа файлов в Windows. |
| «Не удалось прочитать изменённый файл» | Файл, открытый во внешнем редакторе, удалён или занят. | Сохраните файл в редакторе и закройте его, затем вернитесь в окно программы. |
| Вопрос «Файл изменён во внешней программе» | Файл, открытый из образа, сохранён в редакторе. | «Записать» — заменить файл в образе; изменения попадут в образ при его сохранении. |
| «Замена содержимого» — «Выберите один файл.» | Для замены содержимого выбрано несколько файлов. | Выберите один. |
| «Не удалось выполнить действие» | Не удалось открыть папку в Проводнике или скопировать путь в буфер обмена. | Повторите; причина указана в тексте. |

## 14. Настройки и параметры сохранения

| Сообщение | Что делать |
|---|---|
| «Права для файлов / папок / исполняемых файлов задаются восьмеричным числом, например 0644.» | Введите права вида `0644`, `0755`. |
| «Папка для временных файлов не существует.» | Создайте папку или очистите поле (будет системная временная папка). |
| «Резерв для root в ext — число от 0 до 50 процентов.», «Резерв для root — число от 0 до 50 процентов.» | Введите число от 0 до 50 (обычно 0 для образов, 5 для систем). |
| «Метка тома не длиннее 16 байт.» | Сократите метку. |
| «Размер образа — целое число мегабайт (МиБ), не меньше 1.» | Введите целое число. |
| «Запас свободного места — целое неотрицательное число мегабайт.» | Введите 0 или больше. |

## 15. Прочее

| Сообщение | Причина | Что делать |
|---|---|---|
| «Файл не найден» | Файл из списка недавних удалён или перемещён. | Он убирается из списка автоматически; откройте файл через «Открыть». |
| «Внутренняя ошибка (<тип>): <текст>» | Непредвиденная ошибка внутри операции. Окно продолжает работать, образ на диске не изменён. | Повторите операцию. Если повторяется — сообщите об ошибке, указав текст и шаги. |
| «Непредвиденная ошибка. Открытый образ не изменён на диске.» (системное окно) | Сбой вне операций (в интерфейсе). | Сохраните работу «Сохранить как» в новый файл и перезапустите программу; сообщите об ошибке. Если окно появляется при запуске, удалите или переименуйте файл настроек `%AppData%\FsForge\settings.json`. |
| `fsforge-cli`: `unknown command`, вывод справки | Неверная команда или параметры. | Запустите `fsforge-cli` без параметров, чтобы увидеть справку. |
