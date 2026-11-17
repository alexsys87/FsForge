# F2FS checks. Sourced by verify-linux.sh, which provides $F (the CLI), scan.py, tree/ with its listing tree.ref,
# img/xz.sqfs (the same tree), ok/bad; everything else lives in f2/.
#
#   f2_run      images made by mkfs.f2fs + sload.f2fs (f2fs-tools; plain, with extra attributes and inode checksums,
#               with a superblock checksum as well, a labelled one) are read by FsForge and
#               compared with what sload keeps of the tree: sload.f2fs -P stores neither devices, FIFOs, sockets
#               nor extended attributes, keeps only the low 16 bits of owners and sets the time of the root itself.
#               (sload.f2fs 1.16 also exits with 1 after mending an inode checksum it wrote wrong, and with
#               flexible_inline_xattr puts the data of large files where neither the kernel nor FsForge looks for
#               it: what counts is that fsck.f2fs finds the image clean, and that variant is left to the kernel.)
#               Images written by FsForge (size from the content, a fixed size with a label, without xattrs, an
#               edited sload image) must pass fsck.f2fs and read back as the tree. When the running kernel has
#               F2FS, they are also mounted and listed.
#   f2_qemu     with CPIO_KERNEL=<vmlinuz>, CPIO_BUSYBOX=<static busybox> and F2FS_MODULES=<folder with f2fs.ko,
#               lz4_compress.ko and lz4hc_compress.ko of that kernel, unless they are built in> (and
#               qemu-system-x86_64): a script started as /init mounts the images of sload and FsForge read-only
#               and lists what it sees (types, permissions, owners, times, sizes, devices, SHA-256, inode numbers
#               of hard links), which must match FsForge's listing of the same image; then it changes an FsForge
#               image (new folder, copied, removed and renamed files), which must stay clean for fsck.f2fs, and
#               writes LZ4-compressed files to an image made with -O extra_attr,compression, which FsForge must
#               read back with the right content.

f2_ref() { # what sload.f2fs -P keeps of tree.ref
  python3 - <<'PY'
for line in open('tree.ref', encoding='utf-8', errors='surrogateescape'):
    p, t, mode, uid, gid, mtime, extra, hl, xa = line.rstrip('\n').split('\t')
    if t in 'cbps': continue
    if p == '/': mtime = '-'
    print('\t'.join((p, t, mode, str(int(uid) & 0xFFFF), str(int(gid) & 0xFFFF), mtime, extra, hl, '-')))
PY
}

f2_noroot() { awk 'BEGIN{FS=OFS="\t"} $1=="/"{$6="-"} 1' "$1"; }

f2_fsck() { # image : fsck.f2fs finds nothing to fix
  fsck.f2fs --dry-run "$1" > "$1.fsck" 2>&1 || return 1
  ! grep "\[FSCK\]" "$1.fsck" | grep -v "Ok\.\.\]\|Max image size\|fixing" | grep -q .
}

f2_run() {
  rm -rf f2; mkdir -p f2/img f2/out
  echo "F2FS: images made by mkfs.f2fs and sload.f2fs"
  f2_ref > f2/sload.ref
  local n
  if command -v mkfs.f2fs >/dev/null && command -v sload.f2fs >/dev/null; then
    mk() { # name mkfs-options... : a 200 MiB image filled by sload, read by FsForge
      n=$1; shift
      truncate -s 200M f2/img/$n
      mkfs.f2fs -q -f "$@" f2/img/$n > f2/out/mk.$n.log 2>&1 && sload.f2fs -P -f tree f2/img/$n >> f2/out/mk.$n.log 2>&1
      if f2_fsck f2/img/$n; then
        if $F dump f2/img/$n > f2/out/r.$n.txt 2> f2/out/r.$n.err && f2_noroot f2/out/r.$n.txt | cmp -s - f2/sload.ref; then ok "read  $n"
        else bad "read  $n: $(head -c 200 f2/out/r.$n.err)"; f2_noroot f2/out/r.$n.txt | diff - f2/sload.ref | head -4 | cut -c1-200; fi
      else bad "mkfs.f2fs/sload.f2fs $n: $(tr '\r' '\n' < f2/out/mk.$n.log | grep -v 'Ok\.\.\]' | grep 'FSCK\|ASSERT\|rror' | head -2)"; fi
    }
    mk mk_plain.f2fs
    mk mk_extra.f2fs -O extra_attr,inode_checksum
    mk mk_csum.f2fs -O extra_attr,inode_checksum,sb_checksum
    mk mk_label.f2fs -l userdata
    truncate -s 200M f2/img/mk_flex.f2fs       # for the kernel only, see above
    mkfs.f2fs -q -f -O extra_attr,flexible_inline_xattr f2/img/mk_flex.f2fs > /dev/null 2>&1 && sload.f2fs -P -f tree f2/img/mk_flex.f2fs > /dev/null 2>&1
    $F dump f2/img/mk_label.f2fs 2>&1 >/dev/null | grep -q "label='userdata'" && ok "the label of a mkfs.f2fs -l image is read" || bad "label of mk_label.f2fs"
    $F dump f2/img/mk_csum.f2fs 2>&1 >/dev/null | grep -q "sb_checksum" && ok "the features of a mkfs.f2fs -O image are read" || bad "features of mk_csum.f2fs: $($F dump f2/img/mk_csum.f2fs 2>&1 >/dev/null)"
  else echo "  --    mkfs.f2fs or sload.f2fs is not installed: skipped"; fi

  echo "F2FS: images written by FsForge"
  local kernel=0
  { grep -qw f2fs /proc/filesystems || modprobe f2fs 2>/dev/null && grep -qw f2fs /proc/filesystems; } && kernel=1
  [ $kernel = 1 ] || echo "  --    the running kernel has no F2FS: images are checked by fsck.f2fs only"
  wr() { # name src ref options... : FsForge writes it, fsck.f2fs checks it, FsForge (and the kernel) read it
    n=$1; local src=$2 ref=$3; shift 3
    if ! $F convert $src f2/img/$n verify=content "$@" 2> f2/out/w.$n.err; then bad "write $n: $(grep -v '^warning' f2/out/w.$n.err | head -c 200)"; return 1; fi
    local r0=1 r1=1 r2=1
    if command -v fsck.f2fs >/dev/null; then f2_fsck f2/img/$n || r0=0; fi
    $F dump f2/img/$n 2>/dev/null | cmp -s - $ref || r1=0
    if [ $kernel = 1 ]; then
      if mount -t f2fs -o loop,ro f2/img/$n mnt 2> f2/out/m.$n.err; then python3 scan.py mnt | cmp -s - $ref || r2=0; umount mnt; else r2=0; fi
    fi
    [ $r0$r1$r2 = 111 ] && ok "write $n ($(stat -c %s f2/img/$n) bytes; fsck.f2fs$([ $kernel = 1 ] && echo " + kernel"))" \
      || { bad "write $n: fsck=$r0 read-back=$r1 kernel=$r2"; grep "\[FSCK\]" f2/img/$n.fsck 2>/dev/null | grep -v "Ok\.\.\]" | head -4; }
  }
  awk 'BEGIN{FS=OFS="\t"} {$9="-"} 1' tree.ref > f2/noxattr.ref
  wr w_auto.f2fs img/xz.sqfs tree.ref
  wr w_160m.f2fs img/xz.sqfs tree.ref size=160 label=rootfs
  wr w_noxattr.f2fs img/xz.sqfs f2/noxattr.ref xattrs=0 free=0
  $F dump f2/img/w_160m.f2fs 2>&1 >/dev/null | grep -q "blocks=40960 .*label='rootfs'" && ok "the size and the label are kept" || bad "size and label of w_160m.f2fs"
  # Saved again, an image keeps its size, label and UUID.
  local before; before=$($F info f2/img/w_160m.f2fs 2>/dev/null | grep "^Label/UUID" | cut -d, -f1)
  if $F edit f2/img/w_160m.f2fs f2/img/w_edit.f2fs rm:/one-byte mkdir:/newdir 2> f2/out/w.edit.err; then
    local after; after=$($F info f2/img/w_edit.f2fs 2>/dev/null | grep "^Label/UUID" | cut -d, -f1)
    { ! command -v fsck.f2fs >/dev/null || f2_fsck f2/img/w_edit.f2fs; } && [ "$before" = "$after" ] && [ "$(stat -c %s f2/img/w_edit.f2fs)" = $((160 << 20)) ] \
      && $F ls f2/img/w_edit.f2fs / | grep -q " newdir$" && ! $F ls f2/img/w_edit.f2fs / | grep -q " one-byte$" \
      && ok "an edited image keeps its size, label and UUID and passes fsck.f2fs" || bad "edited image: '$before' / '$after'"
  else bad "edit: $(head -c 200 f2/out/w.edit.err)"; fi
  if [ -f f2/img/mk_csum.f2fs ]; then
    grep -v "^/one-byte	" f2/sload.ref > f2/sload-edit.ref
    if $F edit f2/img/mk_csum.f2fs f2/img/w_from_sload.f2fs rm:/one-byte 2> f2/out/w.sl.err && { ! command -v fsck.f2fs >/dev/null || f2_fsck f2/img/w_from_sload.f2fs; } \
       && $F dump f2/img/w_from_sload.f2fs 2>/dev/null | f2_noroot /dev/stdin | cmp -s - f2/sload-edit.ref; then ok "an sload image edited and saved by FsForge passes fsck.f2fs"
    else bad "sload image saved by FsForge: $(head -c 200 f2/out/w.sl.err)"; fi
  fi
}

f2_qemu() {
  local kernel=${CPIO_KERNEL:-} busybox=${CPIO_BUSYBOX:-} mods=${F2FS_MODULES:-}
  if [ -z "$kernel" ] || [ -z "$busybox" ]; then echo "  --    kernel mount in QEMU skipped (set CPIO_KERNEL, CPIO_BUSYBOX and F2FS_MODULES)"; return; fi
  command -v qemu-system-x86_64 >/dev/null || { echo "  --    kernel mount skipped: qemu-system-x86_64 is not installed"; return; }
  command -v mkfs.f2fs >/dev/null || { echo "  --    kernel mount skipped: mkfs.f2fs is not installed"; return; }
  echo "F2FS: the kernel mounts, changes and compresses"
  rm -rf f2/g; mkdir -p f2/g/kbin f2/g/dev f2/g/mnt f2/g/img f2/g/kmod f2/g/proc f2/g/src
  cp "$busybox" f2/g/kbin/busybox; mknod f2/g/dev/console c 5 1
  local m; for m in lz4_compress lz4hc_compress f2fs; do [ -n "$mods" ] && [ -f "$mods/$m.ko" ] && cp "$mods/$m.ko" f2/g/kmod/; done
  local i
  for i in mk_plain.f2fs mk_csum.f2fs mk_flex.f2fs w_auto.f2fs w_160m.f2fs w_noxattr.f2fs; do [ -f f2/img/$i ] && cp f2/img/$i f2/g/img/; done
  cp tree/bigtext f2/g/src/notes.txt; head -c 300000 /dev/urandom > f2/g/src/random.bin; printf 'small file\n' > f2/g/src/small.txt
  # The images the kernel writes to are drives of their own, outside the initramfs.
  cp f2/img/w_auto.f2fs f2/rw.f2fs
  truncate -s 0 f2/comp.f2fs; truncate -s 128M f2/comp.f2fs; mkfs.f2fs -q -f -O extra_attr,compression f2/comp.f2fs > /dev/null 2>&1
  cat > f2/g/init <<'SH'
#!/kbin/busybox sh
B=/kbin/busybox
$B mount -t devtmpfs dev /dev; $B mount -t proc proc /proc
for m in lz4_compress lz4hc_compress f2fs; do [ -f /kmod/$m.ko ] && $B insmod /kmod/$m.ko; done
list() { $B find /mnt | while IFS= read -r p; do
  s=$($B stat -c '%f|%a|%u|%g|%Y|%s|%h|%i|%t:%T' "$p")
  if [ -L "$p" ]; then h="L:$($B readlink "$p")"; elif [ -f "$p" ]; then h=$($B sha256sum "$p" | $B cut -d' ' -f1); else h=-; fi
  echo "@@E ${p#/mnt}|$s|$h"; done; }
for i in /img/*; do n=${i##*/}; echo "@@IMG $n"; if $B mount -t f2fs -o loop,ro $i /mnt; then list; $B umount /mnt; fi; done
echo "@@IMG -"
if $B mount -t f2fs /dev/vda /mnt; then
  $B mkdir /mnt/new; $B cp /src/small.txt /src/random.bin /mnt/new/; echo x > /mnt/etc/added; $B rm /mnt/one-byte; $B mv /mnt/f4096 /mnt/new/moved
  $B umount /mnt && echo "@@RWOK"
fi
if $B mount -t f2fs -o compress_algorithm=lz4,compress_extension=txt /dev/vdb /mnt; then
  $B cp /src/notes.txt /src/small.txt /src/random.bin /mnt/; $B umount /mnt && echo "@@COMPOK"
fi
$B poweroff -f
SH
  chmod 755 f2/g/init
  cat > f2/kcmp.py <<'PY'
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
    e = k.pop(p, None)
    if e is None: bad.append(p + ': not seen by the kernel'); continue
    for key, v in dict(t=t, mode=mode, uid=uid, gid=gid, mtime=mtime, extra=extra).items():
        if e[key] != v: bad.append('%s: %s is %s in the kernel, %s in FsForge' % (p, key, e[key], v))
    if hl != '-' and first.setdefault(hl, e['ino']) != e['ino']: bad.append(p + ': not the same inode as ' + hl + ' in the kernel')
bad += [p + ': only the kernel shows it' for p in k]
print('; '.join(bad[:4]))
sys.exit(1 if bad else 0)
PY
  mksquashfs f2/g f2/g.sqfs -noappend -quiet -no-progress >/dev/null 2>&1
  $F convert f2/g.sqfs f2/g.cpio.gz verify=none > /dev/null 2>&1
  timeout 1800 qemu-system-x86_64 -m 3072 -nographic -no-reboot -kernel "$kernel" -initrd f2/g.cpio.gz \
    -drive file=f2/rw.f2fs,format=raw,if=virtio -drive file=f2/comp.f2fs,format=raw,if=virtio \
    -append "console=ttyS0 panic=-1 loglevel=3" > f2/out/boot.log 2>&1
  for i in f2/g/img/*; do
    n=${i##*/}
    $F dump $i > f2/out/k.$n.dump 2>/dev/null
    if r=$(python3 f2/kcmp.py f2/out/boot.log $n f2/out/k.$n.dump); then ok "the kernel mounts $n and sees what FsForge reads ($(grep -c . f2/out/k.$n.dump) objects)"
    else bad "kernel mount of $n: $r"; fi
  done
  # Written by the kernel: still clean, and FsForge reads the changes.
  h() { sha256sum "$1" | cut -d' ' -f1; }
  if grep -aq "@@RWOK" f2/out/boot.log; then
    $F dump f2/rw.f2fs > f2/out/rw.txt 2>/dev/null
    { ! command -v fsck.f2fs >/dev/null || f2_fsck f2/rw.f2fs; } && grep -q "^/new/random.bin	f	.*	300000:$(h f2/g/src/random.bin)	" f2/out/rw.txt \
      && grep -q "^/new/moved	f" f2/out/rw.txt && grep -q "^/etc/added	f" f2/out/rw.txt && ! grep -q "^/one-byte	" f2/out/rw.txt \
      && ok "an FsForge image changed by the kernel passes fsck.f2fs, and FsForge reads the changes" || bad "image changed by the kernel: $(grep '\[FSCK\]' f2/rw.f2fs.fsck 2>/dev/null | grep -v 'Ok\.\.\]' | head -2)"
  else bad "the kernel did not change the FsForge image read-write: $(grep -a -i 'f2fs' f2/out/boot.log | tail -2)"; fi
  if grep -aq "@@COMPOK" f2/out/boot.log; then
    $F dump f2/comp.f2fs > f2/out/comp.txt 2> f2/out/comp.err
    local c; c=$($F info f2/comp.f2fs 2>/dev/null | grep -o "[0-9]* compressed files" | cut -d' ' -f1)
    grep -q "^/notes.txt	f	.*:$(h f2/g/src/notes.txt)	" f2/out/comp.txt && grep -q "^/small.txt	f	.*:$(h f2/g/src/small.txt)	" f2/out/comp.txt \
      && grep -q "^/random.bin	f	.*:$(h f2/g/src/random.bin)	" f2/out/comp.txt && [ "${c:-0}" -ge 1 ] \
      && ok "files compressed by the kernel with LZ4 are read ($c compressed)" || bad "compressed files: ${c:-0} compressed; $(head -c 200 f2/out/comp.err)"
  else echo "  --    the kernel has no F2FS compression (CONFIG_F2FS_FS_COMPRESSION or lz4 modules missing): skipped"; fi
}
