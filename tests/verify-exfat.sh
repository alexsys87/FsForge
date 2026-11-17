# exFAT checks. Sourced by verify-linux.sh, which provides $F (the CLI), scan.py, ok/bad; everything else lives
# in exf/.
#
#   exf_run     images written by FsForge (size from the content, a fixed size, clusters of 512 bytes to 128 KiB,
#               4 KiB sectors, a label and serial number) must pass fsck.exfat (exfatprogs), carry the label and
#               serial that exfatlabel and tune.exfat read, and read back as the tree; an SD card image (MBR,
#               exFAT data partition) gets its partition edited in place.
#   exf_qemu    with CPIO_KERNEL=<vmlinuz>, CPIO_BUSYBOX=<static busybox> and EXFAT_MODULES=<folder with exfat.ko of
#               that kernel, unless it is built in> (and qemu-system-x86_64): a script started as /init
#               - mounts the FsForge images read-only and lists what it sees (types, permissions with
#                 fmask=0133,dmask=0022, sizes, times, SHA-256), which must match FsForge's listing;
#               - fills empty images made by mkfs.exfat (clusters of 512 bytes, 4 KiB, 32 KiB, 128 KiB) with the tree,
#                 which FsForge must read as the tree (the kernel keeps files contiguous, without FAT chains);
#               - changes an FsForge image (new folder and files, a file appended to, removed and renamed ones), which
#                 must stay clean for fsck.exfat and read back with the changes.
#
# exFAT keeps no owners, permissions (only "read-only"), links or special files: the listings compare path,
# type, permissions as FsForge shows them (0644, 0444 read-only, 0755 directories), time and content.

exf_list() { awk -F'\t' 'BEGIN{OFS="\t"} $1 != "/" {print $1,$2,$3,($2=="d"?"-":$6),$7}'; }

exf_tree() {
  rm -rf exf; mkdir -p exf/tree exf/img exf/out
  python3 - exf/tree <<'PY'
import os, sys, random
r = random.Random(4)
root = sys.argv[1]
def put(p, data, t=1700000001, mode=0o644):
    full = os.path.join(root, p); os.makedirs(os.path.dirname(full), exist_ok=True)
    open(full, 'wb').write(data); os.chmod(full, mode); os.utime(full, (t, t))
put('DCIM/100CANON/IMG_0001.JPG', bytes(r.getrandbits(8) for _ in range(400000)), 1600000003)
put('DCIM/100CANON/IMG_0002.JPG', bytes(r.getrandbits(8) for _ in range(250001)), 1600000004)
put('video/clip.mp4', bytes(r.getrandbits(8) for _ in range(3000000)), 1650000005)
put('Long File Name with spaces.txt', b'spaces\n'); put('.hidden', b'dot\n'); put('multi.dot.name.tar.gz', b'x' * 5000)
put('UPPER.TXT', b'upper\n'); put('lower.txt', b'lower\n'); put('MiXeD.Txt', b'mixed\n')
put('пример/файл на русском.txt', 'юникод\n'.encode()); put('日本語.txt', b'jp\n'); put('emoji-😀.txt', b'smile\n')
put('read-only.txt', b'do not change\n', mode=0o444)
put('empty.bin', b''); put('n' * 200 + '.txt', b'long\n')
for n in (511, 512, 513, 4095, 4096, 4097, 32768, 131072, 131073): put(f'sizes/s{n}', bytes((i * 13) % 251 for i in range(n)))
put('deep/' + '/'.join(f'level{i}' for i in range(12)) + '/leaf.txt', b'leaf\n')
for i in range(300): put(f'many/file-{i:03}.dat', str(i).encode(), 1700000000 + i)
os.makedirs(os.path.join(root, 'emptydir'))
for dirpath, dirs, files in os.walk(root, topdown=False):
    for d in dirs: os.utime(os.path.join(dirpath, d), (1690000000, 1690000000))
PY
  # What exFAT keeps of the tree, in the form FsForge lists it.
  python3 scan.py exf/tree | python3 -c '
import sys
for line in sys.stdin:
    f = line.rstrip("\n").split("\t")
    f[2] = "0755" if f[1] == "d" else "0644" if int(f[2], 8) & 0o222 else "0444"
    print("\t".join(f))' | exf_list > exf/tree.ref
  tar -C exf/tree -cf exf/tree.tar .
}

exf_dump() { $F dump "$1" 2> "$2" | exf_list; }   # image err-file

EXF_WRITTEN=""
exf_wr() { # name fsforge-options... : an image written by FsForge, checked by fsck.exfat and read back
  local n=$1; shift
  if ! $F convert exf/tree.tar exf/img/w_$n.exfat verify=content "$@" > exf/out/w.$n.log 2>&1; then bad "write $n: $(tail -2 exf/out/w.$n.log)"; return; fi
  if ! fsck.exfat -n exf/img/w_$n.exfat > exf/out/fsck.$n.txt 2>&1 || ! grep -q "clean" exf/out/fsck.$n.txt; then
    bad "fsck.exfat $n: $(grep -v '^exfatprogs' exf/out/fsck.$n.txt | head -4)"; return
  fi
  exf_dump exf/img/w_$n.exfat exf/out/o.$n.err > exf/out/o.$n.txt
  if cmp -s exf/out/o.$n.txt exf/tree.ref; then ok "write $n ($($F info exf/img/w_$n.exfat 2>/dev/null | sed -n 's/^exFAT: *revision [^,]*, \([^(]*\)(.*/\1/p'); fsck.exfat, read back)"; EXF_WRITTEN="$EXF_WRITTEN $n"
  else bad "write $n: read back differs"; diff exf/out/o.$n.txt exf/tree.ref | head -4 | cut -c1-180; fi
}

exf_run() {
  command -v fsck.exfat >/dev/null || { echo "  --    exFAT checks skipped: exfatprogs is not installed"; return; }
  echo "exFAT: images written by FsForge"
  exf_tree
  exf_wr auto
  exf_wr 64m size=64 label=CAMERA serial=1234ABCD
  exf_wr c512 cluster=512 free=4
  exf_wr c32k cluster=32768 size=96
  exf_wr c128k cluster=131072 size=128
  exf_wr s4k sector=4096 size=64
  [ "$(exfatlabel exf/img/w_64m.exfat 2>/dev/null | tail -1 | sed 's/.*: *//')" = CAMERA ] && tune.exfat -i exf/img/w_64m.exfat 2>/dev/null | grep -qi "0x1234abcd" \
    && ok "exfatlabel and tune.exfat read the label and the serial number" || bad "label/serial: $(exfatlabel exf/img/w_64m.exfat 2>&1 | tail -1) $(tune.exfat -i exf/img/w_64m.exfat 2>&1 | tail -1)"
  # Saved again, an image keeps its size, cluster size, label and serial number.
  if $F edit exf/img/w_64m.exfat exf/img/w_edit.exfat rm:/empty.bin mkdir:/added > exf/out/e.log 2>&1 && fsck.exfat -n exf/img/w_edit.exfat > exf/out/fsck.e.txt 2>&1 \
     && [ "$(stat -c %s exf/img/w_edit.exfat)" = $((64 << 20)) ] && tune.exfat -i exf/img/w_edit.exfat 2>/dev/null | grep -qi "0x1234abcd" \
     && $F ls exf/img/w_edit.exfat / | grep -q " added$"; then ok "an edited image keeps its size, label and serial and passes fsck.exfat"
  else bad "edited image: $(tail -2 exf/out/e.log) $(tail -2 exf/out/fsck.e.txt)"; fi

  # An SD card with an exFAT data partition (MBR type 7) made by mkfs.exfat, edited in place.
  if command -v sfdisk >/dev/null; then
    truncate -s 80M exf/sd.img
    printf 'start=2048, size=8192, type=c\nstart=10240, size=153600, type=7\n' | sfdisk -q exf/sd.img > /dev/null 2>&1
    truncate -s 75M exf/part.img; mkfs.exfat -q -L SDCARD exf/part.img > /dev/null 2>&1
    dd if=exf/part.img of=exf/sd.img bs=512 seek=10240 conv=notrunc status=none
    cp exf/sd.img exf/sd.orig
    if $F edit exf/sd.img exf/sd.img add:/:exf/tree/UPPER.TXT > exf/out/sd.log 2>&1; then
      dd if=exf/sd.img of=exf/part2.img bs=512 skip=10240 count=153600 status=none
      fsck.exfat -n exf/part2.img > exf/out/fsck.sd.txt 2>&1 && $F ls exf/part2.img / | grep -q " UPPER.TXT$" \
        && cmp -s -n $((10240 * 512)) exf/sd.img exf/sd.orig && [ "$(stat -c %s exf/sd.img)" = "$(stat -c %s exf/sd.orig)" ] \
        && [ "$(exfatlabel exf/part2.img 2>/dev/null | tail -1 | sed 's/.*: *//')" = SDCARD ] \
        && ok "the exFAT partition of an SD card image is edited in place (fsck.exfat, label and the rest of the disk kept)" || bad "SD card: $(tail -3 exf/out/fsck.sd.txt)"
    else bad "SD card edit: $(tail -2 exf/out/sd.log)"; fi
  fi
}

exf_qemu() {
  local kernel=${CPIO_KERNEL:-} busybox=${CPIO_BUSYBOX:-} mods=${EXFAT_MODULES:-}
  if [ -z "$kernel" ] || [ -z "$busybox" ]; then echo "  --    kernel checks skipped (set CPIO_KERNEL, CPIO_BUSYBOX and EXFAT_MODULES)"; return; fi
  command -v qemu-system-x86_64 >/dev/null || { echo "  --    kernel checks skipped: qemu-system-x86_64 is not installed"; return; }
  command -v mkfs.exfat >/dev/null || { echo "  --    kernel checks skipped: exfatprogs is not installed"; return; }
  echo "exFAT: the kernel mounts, fills and changes images"
  rm -rf exf/g; mkdir -p exf/g/kbin exf/g/dev exf/g/mnt exf/g/img exf/g/kmod exf/g/proc exf/g/src
  cp "$busybox" exf/g/kbin/busybox; mknod exf/g/dev/console c 5 1
  local m; for m in nls_utf8 exfat; do [ -n "$mods" ] && [ -f "$mods/$m.ko" ] && cp "$mods/$m.ko" exf/g/kmod/; done
  local n
  for n in $EXF_WRITTEN; do cp exf/img/w_$n.exfat exf/g/img/; done
  cp exf/tree.tar exf/g/src/
  printf 'appended\n' > exf/g/src/more.txt
  # Empty images of mkfs.exfat for the kernel to fill: drives vdb.., the FsForge image to change: vda.
  cp exf/img/w_auto.exfat exf/rw.exfat
  local drives="-drive file=exf/rw.exfat,format=raw,if=virtio" c i=0
  for c in 512 4K 32K 128K; do
    truncate -s 0 exf/k$c.exfat; truncate -s 96M exf/k$c.exfat
    mkfs.exfat -q -c $c -L K$c exf/k$c.exfat > /dev/null 2>&1
    drives="$drives -drive file=exf/k$c.exfat,format=raw,if=virtio"
  done
  cat > exf/g/init <<'SH'
#!/kbin/busybox sh
B=/kbin/busybox
$B mount -t devtmpfs dev /dev; $B mount -t proc proc /proc
for m in nls_utf8 exfat; do [ -f /kmod/$m.ko ] && $B insmod /kmod/$m.ko; done
O="fmask=0133,dmask=0022"
list() { $B find /mnt | while IFS= read -r p; do
  s=$($B stat -c '%f|%a|%u|%g|%Y|%s|%h|%i|%t:%T' "$p")
  if [ -f "$p" ]; then h=$($B sha256sum "$p" | $B cut -d' ' -f1); else h=-; fi
  echo "@@E ${p#/mnt}|$s|$h"; done; }
for i in /img/*; do n=${i##*/}; echo "@@IMG $n"; if $B mount -t exfat -o loop,ro,$O $i /mnt; then list; $B umount /mnt; fi; done
echo "@@IMG -"
for d in vdb vdc vdd vde; do
  if $B mount -t exfat -o $O /dev/$d /mnt; then $B tar -xf /src/tree.tar -C /mnt 2>/dev/null; $B chmod 444 /mnt/read-only.txt; $B umount /mnt && echo "@@FILLED $d"; fi
done
if $B mount -t exfat -o $O /dev/vda /mnt; then
  $B mkdir /mnt/new; $B cp /src/more.txt /mnt/new/; $B cat /src/more.txt >> /mnt/lower.txt; $B rm /mnt/UPPER.TXT; $B mv /mnt/video /mnt/movies
  $B umount /mnt && echo "@@RWOK"
fi
$B poweroff -f
SH
  chmod 755 exf/g/init
  cat > exf/kcmp.py <<'PY'
# kcmp.py boot.log image fsforge-dump : what the kernel shows of an image against FsForge's listing of it
import sys
log, name, dump = sys.argv[1], sys.argv[2].encode(), sys.argv[3]
def esc(b): return ''.join(chr(c) if 0x20 < c < 0x7f and c != 0x5c else '\\x%02x' % c for c in b)
k, cur = {}, None
for raw in open(log, 'rb').read().split(b'\n'):
    raw = raw.rstrip(b'\r')
    i = raw.find(b'@@IMG ')
    if i >= 0: cur = raw[i + 6:]; continue
    i = raw.find(b'@@E ')
    if i < 0 or cur != name: continue
    f = raw[i + 4:].rsplit(b'|', 10)
    p = esc(f[0])
    if not p: continue
    t = 'd' if int(f[1], 16) & 0o170000 == 0o040000 else 'f'
    k[p] = (t, '%04o' % int(f[2], 8), '-' if t == 'd' else f[5].decode(), '%s:%s' % (f[6].decode(), f[10].decode()) if t == 'f' else '-')
if not k: print('the kernel did not mount it'); sys.exit(1)
bad = []
for line in open(dump, encoding='utf-8', errors='surrogateescape'):
    p, t, mode, mtime, extra = line.rstrip('\n').split('\t')
    e = k.pop(p, None)
    if e is None: bad.append(p + ': not seen by the kernel'); continue
    if e != (t, mode, mtime, extra): bad.append('%s: kernel %s, FsForge %s' % (p, e, (t, mode, mtime, extra)))
bad += [p + ': only the kernel shows it' for p in k]
print('; '.join(bad[:4]))
sys.exit(1 if bad else 0)
PY
  mksquashfs exf/g exf/g.sqfs -noappend -quiet -no-progress >/dev/null 2>&1
  $F convert exf/g.sqfs exf/g.cpio.gz verify=none > /dev/null 2>&1
  timeout 1800 qemu-system-x86_64 -m 2048 -nographic -no-reboot -kernel "$kernel" -initrd exf/g.cpio.gz $drives \
    -append "console=ttyS0 panic=-1 loglevel=3" > exf/out/boot.log 2>&1
  for i in exf/g/img/*; do
    n=${i##*/}
    exf_dump $i /dev/null > exf/out/k.$n.txt
    if r=$(python3 exf/kcmp.py exf/out/boot.log $n exf/out/k.$n.txt); then ok "the kernel mounts $n and sees what FsForge reads ($(grep -c . exf/out/k.$n.txt) objects)"
    else bad "kernel mount of $n: $r"; fi
  done
  local d=b
  for c in 512 4K 32K 128K; do
    if grep -aq "@@FILLED vd$d" exf/out/boot.log; then
      exf_dump exf/k$c.exfat exf/out/kf.$c.err > exf/out/kf.$c.txt
      if cmp -s exf/out/kf.$c.txt exf/tree.ref && fsck.exfat -n exf/k$c.exfat > /dev/null 2>&1; then ok "read  an image of mkfs.exfat -c $c filled by the kernel"
      else bad "read  mkfs.exfat -c $c filled by the kernel: $(head -c 200 exf/out/kf.$c.err)"; diff exf/out/kf.$c.txt exf/tree.ref | head -4 | cut -c1-180; fi
    else bad "the kernel did not fill the mkfs.exfat -c $c image: $(grep -a -i exfat exf/out/boot.log | tail -2)"; fi
    d=$(echo $d | tr 'bcd' 'cde')
  done
  if grep -aq "@@RWOK" exf/out/boot.log; then
    $F dump exf/rw.exfat > exf/out/rw.txt 2>/dev/null
    fsck.exfat -n exf/rw.exfat > exf/out/fsck.rw.txt 2>&1 && grep -q "^/new/more.txt	f" exf/out/rw.txt && grep -q "^/movies/clip.mp4	f" exf/out/rw.txt \
      && ! grep -q "^/UPPER.TXT	" exf/out/rw.txt && grep -q "^/lower.txt	f	.*	15:" exf/out/rw.txt \
      && ok "an FsForge image changed by the kernel passes fsck.exfat, and FsForge reads the changes" || bad "image changed by the kernel: $(grep -v '^exfatprogs' exf/out/fsck.rw.txt | head -2)"
  else bad "the kernel did not change the FsForge image: $(grep -a -i exfat exf/out/boot.log | tail -2)"; fi
}
