# CramFS and romfs checks. Sourced by verify-linux.sh, which provides $F (the CLI), scan.py, tree/ with its
# listing tree.ref, img/xz.sqfs (the same tree), ok/bad and cmpdump; everything else lives in crr/.
#
#   crr_run     images made by mkfs.cramfs (little- and big-endian, 512-byte boot pad, holes, 16 KiB blocks, a
#               name and edition) and by genromfs (data aligned to 16, 512 and 4096 bytes) are read by FsForge and
#               compared with what each format keeps of the tree. CramFS images written by FsForge are checked
#               and unpacked by fsck.cramfs and compared the same way (fsck.cramfs knows no holes: that variant is
#               left to the kernel).
#   crr_qemu    with CPIO_KERNEL=<vmlinuz>, CPIO_BUSYBOX=<static busybox> and CRR_MODULES=<folder with mtd.ko,
#               cramfs.ko and romfs.ko of that kernel> (and qemu-system-x86_64): the kernel mounts the images of
#               mkfs.cramfs, genromfs and FsForge, and a script started as /init lists what it sees (types,
#               permissions, owners, times, sizes, devices, SHA-256, inode numbers of hard links); the listing
#               must match FsForge's listing of the same image. The kernel reads only images of its own byte
#               order and page size: big-endian and 16 KiB images are not mounted.
#
# Neither format can hold a name of 255 bytes (CramFS: 252, romfs: 127): the tree is used without it.

crr_ref() { # cramfs|romfs : what the format keeps of tree.ref, without the long name
  python3 - "$1" <<'PY'
import sys
kind = sys.argv[1]
for line in open('tree.ref', encoding='utf-8', errors='surrogateescape'):
    p, t, mode, uid, gid, mtime, extra, hl, xa = line.rstrip('\n').split('\t')
    if p.endswith('/' + 'n' * 255): continue
    mtime, xa = '0', '-'
    if t in 'cb':
        mask = 0xFF if kind == 'cramfs' else 0xFFFF
        a, b = (int(x) for x in extra.split(':'))
        extra = '%d:%d' % (a & mask, b & mask)
    if kind == 'cramfs':
        hl, uid, gid = '-', str(int(uid) & 0xFFFF), str(int(gid) & 0xFF)
    else:
        uid = gid = '0'
        exe = t in 'df' and int(mode, 8) & 0o111
        mode = '0777' if t == 'l' else '0600' if t in 'cb' else '0755' if exe else '0644'
    print('\t'.join((p, t, mode, uid, gid, mtime, extra, hl, xa)))
PY
}

crr_rd() { # name ref : FsForge reads crr/img/<name>
  if $F dump crr/img/$1 > crr/out/r.$1.txt 2> crr/out/r.$1.err && cmp -s crr/out/r.$1.txt $2; then ok "read  $1"
  else bad "read  $1: $(head -c 200 crr/out/r.$1.err)"; diff crr/out/r.$1.txt $2 | head -4 | cut -c1-200; fi
}

crr_run() {
  rm -rf crr; mkdir -p crr/img crr/out
  echo "CramFS and romfs: images made by mkfs.cramfs and genromfs"
  # The tree without the name of 255 bytes, as a folder and as a SquashFS image.
  rm -rf crr/tree; cp -a tree crr/tree; rm -f "crr/tree/weird names/$(printf 'n%.0s' $(seq 255))"
  crr_ref cramfs > crr/cramfs.ref; crr_ref romfs > crr/romfs.ref
  $F edit img/xz.sqfs crr/img/src.sqfs "rm:/weird names/$(printf 'n%.0s' $(seq 255))" > /dev/null 2>&1
  local n
  if command -v mkfs.cramfs >/dev/null; then
    mk() { n=$1; shift; mkfs.cramfs "$@" crr/tree crr/img/$n > crr/out/mk.$n.log 2>&1 && crr_rd $n crr/cramfs.ref || bad "mkfs.cramfs $n: $(tail -1 crr/out/mk.$n.log)"; }
    mk mk_le.cramfs; mk mk_be.cramfs -N big; mk mk_pad_holes.cramfs -p -z; mk mk_16k.cramfs -b 16384; mk mk_named.cramfs -n rootfs -e 5
    $F dump crr/img/mk_named.cramfs 2>&1 >/dev/null | grep -q "name='rootfs' edition=5" && ok "name and edition of a mkfs.cramfs image" || bad "name and edition: $($F dump crr/img/mk_named.cramfs 2>&1 >/dev/null)"
    $F dump crr/img/mk_16k.cramfs 2>&1 >/dev/null | grep -q "block=16384" && ok "the block size of a 16 KiB image is recognized" || bad "block size of mk_16k.cramfs"
  else echo "  --    mkfs.cramfs is not installed: skipped"; fi
  if command -v genromfs >/dev/null; then
    gr() { n=$1; shift; genromfs -f crr/img/$n -d crr/tree "$@" > crr/out/gr.$n.log 2>&1 && crr_rd $n crr/romfs.ref || bad "genromfs $n: $(tail -1 crr/out/gr.$n.log)"; }
    gr gen.romfs -V rootfs; gr gen_a512.romfs -a 512; gr gen_a4096.romfs -a 4096
    $F dump crr/img/gen_a4096.romfs 2>&1 >/dev/null | grep -q "align=4096" && ok "the data alignment of a genromfs -a 4096 image is recognized" || bad "alignment of gen_a4096.romfs"
  else echo "  --    genromfs is not installed: skipped"; fi

  echo "CramFS and romfs: images written by FsForge"
  wr() { # name ref options... : FsForge writes it from the SquashFS image, reads it back and compares
    n=$1; local ref=$2; shift 2
    if ! $F convert crr/img/src.sqfs crr/img/$n verify=content "$@" 2> crr/out/w.$n.err; then bad "write $n: $(grep -v '^warning' crr/out/w.$n.err | head -c 200)"; return 1; fi
    crr_rd $n $ref
  }
  local x
  for x in "w_le.cramfs" "w_be.cramfs endian=big" "w_pad_holes.cramfs pad=1 holes=1" "w_16k.cramfs block=16384" "w_named.cramfs label=rootfs edition=5 level=1"; do
    set -- $x; n=$1; shift
    wr $n crr/cramfs.ref "$@" || continue
    command -v fsck.cramfs >/dev/null || continue
    case $n in *holes*) continue;; esac
    rm -rf crr/x
    local bs=; case $n in *16k*) bs="-b 16384";; esac
    if fsck.cramfs $bs --extract=crr/x crr/img/$n > crr/out/f.$n.txt 2>&1; then
      # fsck.cramfs sets the times of neither folders nor symbolic links.
      python3 scan.py crr/x | awk 'BEGIN{FS=OFS="\t"} $2=="d"||$2=="l"{$6="-"} 1' > crr/out/f.$n.ls
      awk 'BEGIN{FS=OFS="\t"} $2=="d"||$2=="l"{$6="-"} 1' crr/cramfs.ref | cmp -s - crr/out/f.$n.ls \
        && ok "fsck.cramfs checks and unpacks $n identically" || { bad "fsck.cramfs extraction of $n differs"; diff crr/out/f.$n.ls <(awk 'BEGIN{FS=OFS="\t"} $2=="d"||$2=="l"{$6="-"} 1' crr/cramfs.ref) | head -4 | cut -c1-200; }
    else bad "fsck.cramfs rejects $n: $(tail -2 crr/out/f.$n.txt)"; fi
  done
  rm -rf crr/x
  for x in "w.romfs label=rootfs" "w_a512.romfs align=512" "w_a4096.romfs align=4096"; do set -- $x; n=$1; shift; wr $n crr/romfs.ref "$@"; done
  # Saved again in their own format, the images keep their settings.
  $F convert crr/img/mk_be.cramfs crr/img/w_again.cramfs verify=content 2>/dev/null && $F dump crr/img/w_again.cramfs 2>&1 >/dev/null | grep -q "endian=big" \
    && ok "a big-endian CramFS image saved again stays big-endian" || bad "big-endian CramFS saved again"
  $F convert crr/img/gen_a512.romfs crr/img/w_again.romfs verify=content 2>/dev/null && $F dump crr/img/w_again.romfs 2>&1 >/dev/null | grep -q "align=512" \
    && ok "a romfs image saved again keeps its data alignment" || bad "romfs saved again"
}

crr_qemu() {
  local kernel=${CPIO_KERNEL:-} busybox=${CPIO_BUSYBOX:-} mods=${CRR_MODULES:-}
  if [ -z "$kernel" ] || [ -z "$busybox" ] || [ -z "$mods" ]; then echo "  --    kernel mount skipped (set CPIO_KERNEL, CPIO_BUSYBOX and CRR_MODULES)"; return; fi
  command -v qemu-system-x86_64 >/dev/null || { echo "  --    kernel mount skipped: qemu-system-x86_64 is not installed"; return; }
  echo "CramFS and romfs: the kernel mounts the images"
  rm -rf crr/g; mkdir -p crr/g/kbin crr/g/dev crr/g/mnt crr/g/img crr/g/kmod crr/g/proc
  cp "$busybox" crr/g/kbin/busybox; cp "$mods"/mtd.ko "$mods"/cramfs.ko "$mods"/romfs.ko crr/g/kmod/; mknod crr/g/dev/console c 5 1
  local i
  for i in mk_le.cramfs mk_pad_holes.cramfs gen.romfs gen_a512.romfs w_le.cramfs w_pad_holes.cramfs w_named.cramfs w.romfs w_a4096.romfs; do
    [ -f crr/img/$i ] && cp crr/img/$i crr/g/img/
  done
  cat > crr/g/init <<'SH'
#!/kbin/busybox sh
B=/kbin/busybox
$B mount -t devtmpfs dev /dev; $B mount -t proc proc /proc
$B insmod /kmod/mtd.ko; $B insmod /kmod/cramfs.ko; $B insmod /kmod/romfs.ko
for i in /img/*; do
  n=${i##*/}; t=cramfs; case $n in *.romfs) t=romfs;; esac
  echo "@@IMG $n"
  if $B mount -t $t -o loop,ro $i /mnt; then
    $B find /mnt | while IFS= read -r p; do
      s=$($B stat -c '%f|%a|%u|%g|%Y|%s|%h|%i|%t:%T' "$p")
      if [ -L "$p" ]; then h="L:$($B readlink "$p")"; elif [ -f "$p" ]; then h=$($B sha256sum "$p" | $B cut -d' ' -f1); else h=-; fi
      echo "@@E ${p#/mnt}|$s|$h"
    done
    $B umount /mnt
  fi
done
$B poweroff -f
SH
  chmod 755 crr/g/init
  cat > crr/kcmp.py <<'PY'
# kcmp.py boot.log image fsforge-dump cramfs|romfs : what the kernel shows of an image against FsForge's listing of it
import sys
log, name, dump, kind = sys.argv[1], sys.argv[2].encode(), sys.argv[3], sys.argv[4]
def esc(b): return ''.join(chr(c) if 0x20 < c < 0x7f and c != 0x5c else '\\x%02x' % c for c in b)
k, cur = {}, None
for raw in open(log, 'rb').read().split(b'\n'):
    raw = raw.rstrip(b'\r')
    i = raw.find(b'@@IMG ')
    if i >= 0: cur = raw[i + 6:]; continue
    i = raw.find(b'@@E ')
    if i < 0 or cur != name: continue
    f = raw[i + 4:].rsplit(b'|', 10)                     # a name may hold '|'
    mode = int(f[1], 16)
    t = {0o040000: 'd', 0o100000: 'f', 0o120000: 'l', 0o020000: 'c', 0o060000: 'b', 0o010000: 'p', 0o140000: 's'}[mode & 0o170000]
    maj, mnr = (int(x, 16) for x in f[9].split(b':'))
    extra = '%s:%s' % (f[6].decode(), f[10].decode()) if t == 'f' else esc(f[10][2:]) if t == 'l' else '%d:%d' % (maj, mnr) if t in 'cb' else '-'
    k[esc(f[0]) or '/'] = dict(t=t, mode='%04o' % int(f[2], 8), uid=f[3].decode(), gid=f[4].decode(), mtime=f[5].decode(), extra=extra, ino=f[8].decode())
if not k: print('the kernel did not mount it'); sys.exit(1)
bad, first = [], {}
for line in open(dump, encoding='utf-8', errors='surrogateescape'):
    p, t, mode, uid, gid, mtime, extra, hl, xa = line.rstrip('\n').split('\t')
    if p == '/' and kind == 'cramfs': mode = '%04o' % (int(mode, 8) | 0o555)     # the kernel adds r-x to the root
    e = k.pop(p, None)
    if e is None: bad.append(p + ': not seen by the kernel'); continue
    for key, v in dict(t=t, mode=mode, uid=uid, gid=gid, mtime=mtime, extra=extra).items():
        if e[key] != v: bad.append('%s: %s is %s in the kernel, %s in FsForge' % (p, key, e[key], v))
    if hl != '-' and first.setdefault(hl, e['ino']) != e['ino']: bad.append(p + ': not the same inode as ' + hl + ' in the kernel')
bad += [p + ': only the kernel shows it' for p in k]
print('; '.join(bad[:4]))
sys.exit(1 if bad else 0)
PY
  mksquashfs crr/g crr/g.sqfs -noappend -quiet -no-progress >/dev/null 2>&1
  $F convert crr/g.sqfs crr/g.cpio.gz verify=none > /dev/null 2>&1
  timeout 1200 qemu-system-x86_64 -m 3072 -nographic -no-reboot -kernel "$kernel" -initrd crr/g.cpio.gz \
    -append "console=ttyS0 panic=-1 loglevel=3" > crr/out/boot.log 2>&1
  for i in crr/g/img/*; do
    n=${i##*/}; local kind=cramfs; case $n in *.romfs) kind=romfs;; esac
    $F dump $i > crr/out/k.$n.dump 2>/dev/null
    if r=$(python3 crr/kcmp.py crr/out/boot.log $n crr/out/k.$n.dump $kind); then ok "the kernel mounts $n and sees what FsForge reads ($(grep -c . crr/out/k.$n.dump) objects)"
    else bad "kernel mount of $n: $r"; fi
  done
}
