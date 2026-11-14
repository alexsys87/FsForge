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

| Сообщение | Причина | Что делать |
|---|---|---|
| `The file cannot be opened: <текст системы>` | Файла нет, нет прав на чтение, файл занят другой программой без общего доступа. | Проверьте путь и права; закройте программу, которая держит файл (эмулятор, программа записи карт); скопируйте файл в другую папку. |
| `The file is not a SquashFS, ext2/3/4 image, disk image with such a partition, or tar archive.` | Формат не распознан. | Убедитесь, что это образ файловой системы, а не, например, образ прошивки в собственном формате производителя. Включите Настройки → «Искать SquashFS внутри прошивок, если файл не образ». |
| …`It looks like bzip2 / LZ4 / LZMA / compress (.Z) compressed data.` | Файл сжат методом, который программа не распаковывает. | Распакуйте его сторонней программой (7-Zip, `bunzip2`, `lz4 -d`, `unlzma`) и откройте результат. |
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
| `None of the partitions of this disk image holds SquashFS or ext2/3/4. Its partitions: <список>.` | На диске нет раздела, который программа умеет редактировать (только FAT, F2FS, swap и т. п.). | Редактируются только разделы SquashFS и ext2/3/4. Раздел FAT можно править в Windows, подключив образ (например, в «Управлении дисками» для `.vhd` или сторонней программой). |
| `The disk image has no partition <N>. Its partitions: <список>.` | В `fsforge-cli` указан несуществующий `part=<N>`. | Посмотрите номера разделов: `fsforge-cli info образ.img`. |
| `Partition <N> holds <формат>, not SquashFS or ext2/3/4, and cannot be edited.` | Выбран раздел с неподдерживаемой файловой системой. | Выберите другой раздел. |
| `The selected partition does not hold SquashFS or ext2/3/4.`; в окне выбора — «В этом разделе нет SquashFS или ext2/3/4 — его нельзя открыть для редактирования.» | То же в окне выбора раздела. | Выберите раздел с пометкой SquashFS или ext. |
| «Выберите раздел.» | В окне выбора раздела ничего не выбрано. | Выберите раздел в списке. |
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

## 4. Чтение архивов tar

| Сообщение | Причина | Что делать |
|---|---|---|
| `The file is not a tar archive.` | Не tar. | Проверьте файл. |
| `The archive is compressed with <метод>, which is not supported. Use gzip, xz or zstd.` | bzip2, lz4, lzma, compress. | Перепакуйте: `bunzip2 -c a.tar.bz2 \| gzip > a.tar.gz` или через 7-Zip. |
| `The archive is corrupt: <подробности>` | Ошибка в сжатых данных. | Скачайте заново; `gzip -t` / `xz -t` / `zstd -t`. |
| `XZ files with BCJ filters are not supported for streaming.` | Архив `.tar.xz` сжат с BCJ-фильтром. | Перепакуйте без фильтра: `xz -dc a.tar.xz \| xz -c > b.tar.xz`. |
| `The XZ dictionary is too large.` | Словарь xz больше 1 ГБ. | Перепакуйте с обычными параметрами `xz -6`. |
| `The tar archive is truncated.`, `Corrupt tar archive: …` (bad header checksum after <N> entries, oversized extended header / long name / long link name, negative member size, bad sparse map, sparse member data is truncated), `Corrupt tar header: invalid <поле> field.` | Архив обрезан или повреждён. | Скачайте заново. `tar -tvf` покажет, до какой записи архив цел. |
| Предупреждение: `A malformed pax extended header was ignored.` | Повреждён расширенный заголовок pax; запись прочитана без него. | У записи могут пропасть длинное имя, точное время или атрибуты. |
| Предупреждение: `'<имя>': the path contains '..' and was skipped.` | Путь выходит за пределы архива (защита от «path traversal»). | Запись намеренно пропущена — так и должно быть. |
| Предупреждение: `'<путь>' is replaced by a directory required by '<имя>'.` | В архиве есть файл и папка с одним путём. | Оставлена папка. Проверьте архив, если это неожиданно. |
| Предупреждение: `'<имя>': the hard link target '<цель>' is not in the archive; the link was skipped.` | Жёсткая ссылка на файл, которого нет в архиве (архив собран не целиком). | Добавьте недостающий файл вручную. |
| Предупреждение: `'<имя>' was skipped: <причина>` | Запись с недопустимым именем или конфликтом. | Запись потеряна; при необходимости добавьте её вручную. |

## 5. Ошибки сжатых данных

Появляются при открытии, извлечении или сохранении, когда читается конкретный блок данных:

`Corrupt gzip block…`, `Corrupt XZ block…` / `Corrupt XZ data…` (bad stream header, checksum mismatch, truncated…),
`Corrupt LZMA block` / `Corrupt LZMA data…`, `Corrupt LZ4 block.`, `Corrupt LZO block.`,
`Corrupt Zstandard block…`, `Not an XZ stream.`, `Unsupported XZ stream flags.`,
`Unsupported XZ block header flags.`, `Unsupported XZ filter 0x<…>.`,
`XZ BCJ filters with a start offset are not supported.`, `The XZ filter '<имя>' is not supported.`

Причина — повреждённые данные или редкие параметры сжатия. Если ошибка возникает при извлечении
одного файла — повреждён именно он: извлеките остальное. Если при открытии — см. общие способы.

## 6. Сохранение

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

### Запись tar

| Сообщение | Причина | Что делать |
|---|---|---|
| Предупреждение: `'<путь>': sockets cannot be stored in a tar archive; the entry was skipped.` | Формат tar не хранит сокеты. | Обычно безопасно: сокеты создаются программами при запуске. |
| Предупреждение: `Extended attributes of <N> entries were not stored: the GNU tar format cannot hold them (use pax).` | Выбран диалект GNU. | Выберите «pax (POSIX)». |
| `Only a directory can be exported as an archive.` | «Экспорт в архив» вызван для файла. | Выберите папку. |
| `The tar path prefix contains invalid characters.`, `…must be empty or end with '/'.`, `…must be relative and must not contain '..'.` | Неверный префикс путей. | Используйте `./`, пустой или вида `rootfs/`. |
| `'<путь>': unexpected end of the file data.` | Данные файла оборвались при чтении (исходный образ повреждён или добавленный файл изменился). | См. «Исходные файлы изменились» ниже. |

### Исходные файлы изменились

| Сообщение | Причина | Что делать |
|---|---|---|
| `The added file '<путь>' can no longer be read: <текст>` | Файл, добавленный с диска, удалён, переименован или заблокирован до сохранения. Образ ссылается на него и читает только при записи. | Верните файл на место или добавьте его заново. Чтобы такого не было, включите Настройки → «Сразу копировать добавляемые файлы во временную папку». |
| `The file '<путь>' has changed since it was added to the image. Add it again.` | Размер добавленного файла изменился. | Добавьте файл заново (или включите ту же настройку). |

## 7. Правка образа

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

## 8. Добавление файлов и сборка из папки

| Сообщение | Причина | Что делать |
|---|---|---|
| `The folder '<путь>' does not exist.` | Папку удалили или переименовали. | Выберите существующую папку. |
| Предупреждение: `'<путь>' could not be read: <текст>` | Нет прав на папку или она занята. | Запустите программу с нужными правами или скопируйте папку в доступное место. |
| Предупреждение: `'<путь>' does not exist.` | Перетащенный или выбранный объект исчез. | Добавьте заново. |
| Предупреждение: `'<путь>' is a link on the host and was skipped.` | Символическая ссылка или точка соединения (junction) Windows, которую нельзя превратить в объект образа. | Добавьте сам файл, на который она указывает. Ссылки Linux, записанные в `.fsforge-meta.json` при извлечении, восстанавливаются автоматически. |
| Предупреждение: `'<путь>': the symbolic link has no target and was skipped.` | У ссылки (или заглушки ссылки) нет цели. | Создайте ссылку в программе вручную («Создать → Символическая ссылка»). |
| Предупреждение: `'<путь>' was skipped: <текст>` | Файл не читается (занят, нет прав) или имя недопустимо. | Закройте программу, держащую файл, и добавьте его снова. |
| Вопрос «Объекты с такими именами уже существуют» | Совпадение имён при добавлении. | Выберите действие; поведение по умолчанию — Настройки → «Если объект с таким именем уже есть». |

## 9. Извлечение и внешний редактор

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

## 10. Настройки и параметры сохранения

| Сообщение | Что делать |
|---|---|
| «Права для файлов / папок / исполняемых файлов задаются восьмеричным числом, например 0644.» | Введите права вида `0644`, `0755`. |
| «Папка для временных файлов не существует.» | Создайте папку или очистите поле (будет системная временная папка). |
| «Резерв для root в ext — число от 0 до 50 процентов.», «Резерв для root — число от 0 до 50 процентов.» | Введите число от 0 до 50 (обычно 0 для образов, 5 для систем). |
| «Метка тома не длиннее 16 байт.» | Сократите метку. |
| «Размер образа — целое число мегабайт (МиБ), не меньше 1.» | Введите целое число. |
| «Запас свободного места — целое неотрицательное число мегабайт.» | Введите 0 или больше. |

## 11. Прочее

| Сообщение | Причина | Что делать |
|---|---|---|
| «Файл не найден» | Файл из списка недавних удалён или перемещён. | Он убирается из списка автоматически; откройте файл через «Открыть». |
| «Внутренняя ошибка (<тип>): <текст>» | Непредвиденная ошибка внутри операции. Окно продолжает работать, образ на диске не изменён. | Повторите операцию. Если повторяется — сообщите об ошибке, указав текст и шаги. |
| «Непредвиденная ошибка. Открытый образ не изменён на диске.» (системное окно) | Сбой вне операций (в интерфейсе). | Сохраните работу «Сохранить как» в новый файл и перезапустите программу; сообщите об ошибке. Если окно появляется при запуске, удалите или переименуйте файл настроек `%AppData%\FsForge\settings.json`. |
| `fsforge-cli`: `unknown command`, вывод справки | Неверная команда или параметры. | Запустите `fsforge-cli` без параметров, чтобы увидеть справку. |
