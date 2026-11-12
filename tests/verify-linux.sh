#!/bin/bash
# Cross-checks the FsForge core against the reference Linux tools.
# Needs root (loop mounts, device nodes) and: squashfs-tools, e2fsprogs, tar, attr, python3;
# optional: libarchive-tools (bsdtar), busybox (also makes the FAT partition of the disk image tests), acl,
# util-linux partx.
#
#   sudo tests/verify-linux.sh [work-dir]
#
# Every image FsForge writes is mounted by the kernel and listed, unpacked with unsquashfs or tar,
# checked with e2fsck, and compared entry by entry (names, types, modes, owners, times, content
# hashes, hard links, extended attributes) with the source tree. Images made by mksquashfs,
# mke2fs and tar are read by FsForge and compared the same way. Disk images (MBR/GPT, FAT boot
# partition + root filesystem, gzip/xz) are edited and then checked partition by partition.
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
F="$HERE/../tools/FsForge.Cli/bin/Release/net10.0/fsforge-cli"
W=${1:-/tmp/fsforge-verify}
[ -x "$F" ] || { echo "build the CLI first: dotnet build tools/FsForge.Cli -c Release"; exit 2; }
[ "$(id -u)" = 0 ] || { echo "run as root"; exit 2; }
rm -rf "$W"; mkdir -p "$W/mnt" "$W/img" "$W/out"; cd "$W"
PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  ok    $*"; }
bad()  { FAIL=$((FAIL+1)); echo "  FAIL  $*"; }

# ---------------------------------------------------------------- listing
cat > scan.py <<'PY'
import os, sys, hashlib, stat
root = os.fsencode(sys.argv[1]).rstrip(b'/')
def esc(b):
    return ''.join(chr(c) if 0x20 < c < 0x7f and c != 0x5c else '\\x%02x' % c for c in b)
items = []
def walk(d, rel):
    st = os.lstat(d)
    items.append((rel if rel else b'/', d, st))
    if stat.S_ISDIR(st.st_mode):
        for n in os.listdir(d):
            walk(d + b'/' + n, rel + b'/' + n)
walk(root, b'')
items.sort(key=lambda x: x[0])
first, hashes = {}, {}
for rel, p, st in items:
    m = st.st_mode
    t = 'd' if stat.S_ISDIR(m) else 'f' if stat.S_ISREG(m) else 'l' if stat.S_ISLNK(m) else 'c' if stat.S_ISCHR(m) else 'b' if stat.S_ISBLK(m) else 'p' if stat.S_ISFIFO(m) else 's'
    hl, key = '-', (st.st_dev, st.st_ino)
    if t != 'd' and st.st_nlink > 1:
        hl = first.setdefault(key, esc(rel))
    if t == 'f':
        if key not in hashes:
            h = hashlib.sha256()
            with open(p, 'rb') as f:
                for b in iter(lambda: f.read(1 << 20), b''): h.update(b)
            hashes[key] = h.hexdigest()
        extra = '%d:%s' % (st.st_size, hashes[key])
    elif t == 'l': extra = esc(os.readlink(p))
    elif t in 'cb': extra = '%d:%d' % (os.major(st.st_rdev), os.minor(st.st_rdev))
    else: extra = '-'
    mode = '0777' if t == 'l' else '%04o' % (m & 0o7777)
    xa = ''
    try: names = sorted(os.listxattr(p, follow_symlinks=False))
    except OSError: names = []
    for n in names:
        try: xa += n + '=' + os.getxattr(p, n, follow_symlinks=False).hex() + ';'
        except OSError: pass
    sys.stdout.write('%s\t%s\t%s\t%d\t%d\t%d\t%s\t%s\t%s\n' % (esc(rel), t, mode, st.st_uid, st.st_gid, int(st.st_mtime), extra, hl, xa or '-'))
PY

# -------------------------------------------------------------- test tree
mktree() {
  T=$1; small=$2; rm -rf $T; mkdir -p $T; pushd $T >/dev/null
  mkdir -p etc/config bin lib/modules/5.15/kernel usr/share/doc dev var/empty "weird names/sub dir" deep/a/b/c/d/e/f/g/h
  cp -a /etc/services /etc/passwd /etc/group /etc/hostname etc/ 2>/dev/null
  cp /bin/ls /bin/cat /bin/bash bin/
  ln -s bash bin/sh; ln -s /proc/mounts etc/mtab; ln -s ../../bin/ls "weird names/link to ls"
  ln bin/cat bin/cat-hardlink; ln bin/cat usr/share/cat-third
  ln -s $(python3 -c "print('a/'*100+'target')") etc/longlink
  mknod dev/null c 1 3; mknod dev/sda b 8 0; mknod dev/ttyS300 c 4 300; mknod dev/bigminor b 259 70000; mkfifo dev/fifo
  python3 -c "import socket; s=socket.socket(socket.AF_UNIX); s.bind('dev/sock')"
  : > var/empty/zero-length
  head -c 1 /dev/urandom > one-byte
  for n in 4095 4096 4097 131072 262144 1048576 1048577; do head -c $n /dev/urandom > f$n; done
  head -c 262145 /dev/zero > z256k1
  (cat /bin/bash; cat /bin/bash; head -c 777 /dev/urandom) > bigtext
  cp bigtext bigtext-dup; cp etc/services services-dup
  dd if=/dev/urandom of=sparse1 bs=4096 count=3 seek=300 conv=notrunc 2>/dev/null; truncate -s 3M sparse1
  truncate -s 5M sparse-all-zero
  dd if=/dev/urandom of=sparse2 bs=1000 count=1 2>/dev/null; dd if=/dev/urandom of=sparse2 bs=1000 count=1 seek=2500 conv=notrunc 2>/dev/null
  python3 - <<'PY'
import os
with open('fragmented','wb') as f:
    for i in range(700):
        f.seek(i*2*4096); f.write(os.urandom(4096))
for i in range(1, 601):
    open('usr/share/doc/file-%d.txt' % i, 'w').write('file number %d with some text\n' % i)
for i in range(1, 41):
    os.makedirs('lib/modules/5.15/kernel/d%d' % i)
    open('lib/modules/5.15/kernel/d%d/mod%d.ko' % (i, i), 'wb').write(os.urandom(i*511))
open(b'weird names/invalid-utf8-\xff\xfe.bin','wb').write(b'x')
open(b'weird names/'+b'n'*255,'wb').write(b'longname')
open(b'weird names/tab\there','wb').write(b't')
PY
  echo hi > "weird names/sub dir/файл на русском.txt"; echo hi > "weird names/日本語"; echo hi > "weird names/emoji-😀.txt"
  echo hi > "weird names/a:b*c?d<e>f|g\"h"; echo hi > "weird names/trailing dot."; echo hi > "weird names/CON"
  echo up > "weird names/Makefile"; echo low > "weird names/makefile"
  echo deep > deep/a/b/c/d/e/f/g/h/file
  chmod 4755 bin/ls; chmod 2750 etc/config; chmod 1777 var; chmod 000 one-byte; chmod 600 f4095; chmod 0444 etc/passwd
  chown 1000:1000 f4096; chown 65534:65534 f4097; chown 123456:654321 f131072; chown -h 1000:100 bin/sh; chown 33:33 usr/share/doc
  setfattr -n user.comment -v "hello world" etc/services
  setfattr -n user.bin -v 0x000102030405 f4096
  setfattr -n security.capability -v 0x0100000200200000000000000000000000000000 bin/cat
  setfattr -n security.selinux -v "u:object_r:system_file:s0" bin/ls
  setfattr -n security.selinux -v "u:object_r:system_file:s0" etc
  setfattr -n trusted.overlay.opaque -v y var
  [ $small = 0 ] && setfattr -n user.big -v "$(head -c 3000 /dev/zero | tr '\0' 'v')" bigtext
  touch -d "2001-02-03 04:05:06 UTC" etc/passwd; touch -h -d "2010-10-10 10:10:10 UTC" bin/sh; touch -d "2030-01-01 00:00:00 UTC" f4096
  touch -d "1999-12-31 23:59:59 UTC" var/empty deep "weird names"; touch -d "2020-05-05 05:05:05 UTC" .
  popd >/dev/null
}
mktree tree 0; python3 scan.py tree > tree.ref
mktree tree1k 1; python3 scan.py tree1k > tree1k.ref              # without the 3000-byte attribute (does not fit a 1K block)
grep -v "^/dev/sock" tree.ref > tree.tar.ref; cut -f1-8 tree.tar.ref > tree.tar.nox.ref

cmpdump() { # name image ref  : FsForge reads an image made by a reference tool
  local id=${1//[ \/]/_}
  if $F dump "$2" > "out/r.$id.txt" 2> "out/r.$id.err" && cmp -s "out/r.$id.txt" "$3"; then ok "read  $1"; else bad "read  $1: $(head -c 200 "out/r.$id.err")"; diff "out/r.$id.txt" "$3" | head -4 | cut -c1-200; fi
}
kmount() { # name image ref [filter] : the kernel reads an image written by FsForge
  if mount -o loop,ro "$2" mnt 2> out/m.$1.err; then python3 scan.py mnt | grep -v "${4:-^$}" > out/k.$1.txt; umount mnt
    cmp -s out/k.$1.txt "$3" && return 0; diff out/k.$1.txt "$3" | head -4 | cut -c1-200; return 1
  else cat out/m.$1.err; dmesg | tail -2; return 1; fi
}

# --------------------------------------------------------------- SquashFS
echo "SquashFS: images made by mksquashfs"
mk() { n=$1; shift; mksquashfs tree img/$n.sqfs -noappend -quiet -no-progress "$@" >/dev/null 2>&1 && cmpdump "squashfs $n" img/$n.sqfs tree.ref; }
mk gzip -comp gzip; mk gzip4k -comp gzip -b 4096; mk xz -comp xz; mk xzbcj -comp xz -Xbcj x86; mk lz4 -comp lz4; mk lz4hc -comp lz4 -Xhc
mk zstd -comp zstd; mk lzo -comp lzo; mk lzma -comp lzma 2>/dev/null; mk nofrag -comp gzip -no-fragments; mk nocomp -comp gzip -noI -noD -noF -noX
mk noexp -comp xz -no-exports; mk b1m -comp zstd -b 1048576

echo "SquashFS: images written by FsForge"
sq() { n=$1; src=$2; shift 2
  if ! $F convert $src img/w_$n.sqfs format=squashfs verify=content "$@" 2> out/w.$n.err; then bad "write squashfs $n: $(cat out/w.$n.err)"; return; fi
  kmount "sq_$n" img/w_$n.sqfs tree.ref && r1=1 || r1=0
  rm -rf uq; if unsquashfs -q -n -d uq img/w_$n.sqfs >/dev/null 2>out/u.$n.err; then python3 scan.py uq | cut -f1-5,7- | grep -v "^/dev/sock\|^/	" > out/u.$n.txt
    cut -f1-5,7- tree.ref | grep -v "^/dev/sock\|^/	" | cmp -s - out/u.$n.txt && r2=1 || r2=0; else r2=0; fi; rm -rf uq
  [ $r1 = 1 ] && [ $r2 = 1 ] && ok "write squashfs $n ($(stat -c %s img/w_$n.sqfs) bytes; kernel + unsquashfs)" || bad "write squashfs $n: kernel=$r1 unsquashfs=$r2"
}
sq gzip img/xz.sqfs comp=gzip; sq gzip4k img/xz.sqfs comp=gzip block=4096; sq xz img/gzip.sqfs comp=xz; sq xz1m img/gzip.sqfs comp=xz block=1048576
sq xzbcj img/gzip.sqfs comp=xz bcj=x86; sq xzfast img/gzip.sqfs comp=xz effort=0; sq lz4 img/xz.sqfs comp=lz4; sq zstd img/xz.sqfs comp=zstd
sq nofrag img/xz.sqfs comp=gzip nofrag=1; sq nodedup img/xz.sqfs comp=gzip dedup=0; sq fromlzo img/lzo.sqfs; [ -f img/lzma.sqfs ] && sq fromlzma img/lzma.sqfs

# -------------------------------------------------------------------- ext
echo "ext: images made by mke2fs"
me() { n=$1; t=$2; ref=$3; size=$4; shift 4; rm -f img/$n.img
  if mke2fs -q -F "$@" -d $t img/$n.img $size >/dev/null 2>out/mke.$n.err; then
    mount -o loop,ro img/$n.img mnt && { python3 scan.py mnt > out/eref.$n.txt; umount mnt; cmpdump "$n" img/$n.img out/eref.$n.txt; }
  else echo "  skip  $n (mke2fs: $(head -c 80 out/mke.$n.err))"; fi; }
me ext2 tree tree.ref 64M -t ext2; me ext2_1k tree1k tree1k.ref 40M -t ext2 -b 1024 -I 128; me ext2_rev0 tree1k tree1k.ref 40M -t ext2 -r 0 -b 1024
me ext3 tree1k tree1k.ref 64M -t ext3 -b 2048; me ext4 tree tree.ref 64M -t ext4; me ext4_1k tree1k tree1k.ref 48M -t ext4 -b 1024
me ext4_128 tree tree.ref 64M -t ext4 -I 128 -O ^has_journal; me ext4_nocsum tree tree.ref 64M -t ext4 -O ^metadata_csum,^64bit
me ext4_inline tree tree.ref 64M -t ext4 -O inline_data; me ext4_bigalloc tree tree.ref 64M -t ext4 -O bigalloc -C 16384
me ext4_eainode tree tree.ref 64M -t ext4 -O ea_inode; me ext4_metabg tree1k tree1k.ref 96M -t ext4 -b 1024 -O meta_bg,^resize_inode

echo "ext: images written by FsForge"
ex() { n=$1; src=$2; ref=$3; shift 3
  if ! $F convert $src img/w_$n.img verify=content "$@" 2> out/w.$n.err; then bad "write $n: $(cat out/w.$n.err)"; return; fi
  e2fsck -f -n img/w_$n.img > out/fsck.$n.txt 2>&1 && r0=1 || r0=0
  kmount "ext_$n" img/w_$n.img $ref "^/lost+found" && r1=1 || r1=0
  [ $r0 = 1 ] && [ $r1 = 1 ] && ok "write $n ($(stat -c %s img/w_$n.img) bytes; e2fsck + kernel)" || { bad "write $n: e2fsck=$r0 kernel=$r1"; grep -v "^Pass\|^e2fsck" out/fsck.$n.txt | head -6; }
}
mksquashfs tree1k img/tree1k.sqfs -noappend -quiet -no-progress -comp lz4 >/dev/null
for k in ext4 ext3 ext2; do ex $k img/xz.sqfs tree.ref format=$k; done
for k in ext4 ext3 ext2; do for bs in 1024 2048; do ex ${k}_$bs img/tree1k.sqfs tree1k.ref format=$k bs=$bs; done; done
ex ext4_i128 img/xz.sqfs tree.ref format=ext4 isize=128; ex ext2_1k_i128 img/tree1k.sqfs tree1k.ref format=ext2 bs=1024 isize=128
ex ext4_nojournal img/xz.sqfs tree.ref format=ext4 journal=0; ex ext4_64m img/xz.sqfs tree.ref format=ext4 size=64
ex ext4_200m_1k img/tree1k.sqfs tree1k.ref format=ext4 bs=1024 size=200; ex ext2_300m img/xz.sqfs tree.ref format=ext2 size=300
ex ext4_free img/xz.sqfs tree.ref format=ext4 free=20 label=rootfs reserved=5
for v in ext2 ext3 ext4 ext4_inline ext4_bigalloc ext4_metabg; do [ -f img/$v.img ] && { grep -v "^/lost+found" out/eref.$v.txt > out/eref.nolf.$v.txt; ex "same_$v" img/$v.img out/eref.nolf.$v.txt; }; done
if $F convert img/xz.sqfs img/w_small.img format=ext4 size=4 2>/dev/null; then bad "an image that is too small was accepted"; else ok "content that does not fit is rejected"; fi

# -------------------------------------------------------------------- tar
echo "tar: archives made by GNU tar${BSD:+ and bsdtar}"
mt() { n=$1; ref=$2; shift 2; (cd tree && tar -c "$@" -f ../img/$n . 2>/dev/null)
  if [ "$ref" = nox ]; then $F dump img/$n 2>out/r.$n.err | cut -f1-8 | cmp -s - tree.tar.nox.ref && ok "read  tar $n" || bad "read  tar $n: $(head -c 200 out/r.$n.err)"; else cmpdump "tar $n" img/$n tree.tar.ref; fi; }
X="--xattrs --xattrs-include=*"
mt gnu.tar nox --format=gnu; mt gnu_sparse.tar nox --format=gnu --sparse; mt oldgnu.tar nox --format=oldgnu
mt pax.tar x --format=pax $X; mt pax_sparse10.tar x --format=pax --sparse $X; mt pax_sparse01.tar x --format=pax --sparse --sparse-version=0.1 $X
mt pax_sparse00.tar x --format=pax --sparse --sparse-version=0.0 $X; mt pax.tar.gz x --format=pax $X -z; mt pax.tar.xz x --format=pax $X -J
command -v zstd >/dev/null && mt pax.tar.zst x --format=pax $X --zstd
if command -v bsdtar >/dev/null; then
  (cd tree && bsdtar -c --format=pax --xattrs -f ../img/bsd_pax.tar . 2>/dev/null); cmpdump "tar bsd_pax.tar" img/bsd_pax.tar tree.tar.ref
  (cd tree && bsdtar -c --format=gnutar -f ../img/bsd_gnu.tar . 2>/dev/null); $F dump img/bsd_gnu.tar 2>/dev/null | cut -f1-8 | cmp -s - tree.tar.nox.ref && ok "read  tar bsd_gnu.tar" || bad "read  tar bsd_gnu.tar"
fi

echo "tar: archives written by FsForge"
tw() { n=$1; shift; nox=0; case "$*" in *gnu*) nox=1;; esac
  if ! $F convert img/xz.sqfs img/w_$n format=tar verify=content "$@" 2> out/w.$n.err; then bad "write tar $n: $(cat out/w.$n.err)"; return; fi
  rm -rf tx; mkdir tx; r1=0
  if tar -xp --xattrs --xattrs-include='*' --numeric-owner -C tx -f img/w_$n 2>out/x.$n.err; then
    if [ $nox = 1 ]; then python3 scan.py tx | cut -f1-8 | cmp -s - tree.tar.nox.ref && r1=1; else python3 scan.py tx | cmp -s - tree.tar.ref && r1=1; fi; fi
  rm -rf tx; [ $r1 = 1 ] && ok "write tar $n ($(stat -c %s img/w_$n) bytes; GNU tar)" || bad "write tar $n: GNU tar extraction differs: $(head -c 200 out/x.$n.err)"
}
tw pax.tar comp=none; tw pax.tar.gz comp=gzip; tw pax.tar.xz comp=xz; tw pax.tar.zst comp=zstd; tw gnu.tar comp=none dialect=gnu; tw gnu.tar.gz comp=gzip dialect=gnu
tw noprefix.tar comp=none prefix=
xz -t img/w_pax.tar.xz 2>/dev/null && ok "xz accepts the .tar.xz" || bad "xz rejects the .tar.xz"
gzip -t img/w_pax.tar.gz 2>/dev/null && ok "gzip accepts the .tar.gz" || bad "gzip rejects the .tar.gz"
if command -v bsdtar >/dev/null; then rm -rf tx; mkdir tx; bsdtar -xp --xattrs --numeric-owner -C tx -f img/w_pax.tar.gz 2>/dev/null
  python3 scan.py tx | tail -n +2 | cmp -s - <(tail -n +2 tree.tar.ref) && ok "bsdtar extracts the .tar.gz identically" || bad "bsdtar extraction differs"; rm -rf tx; fi
if command -v busybox >/dev/null; then rm -rf tx; mkdir tx; busybox tar -xzp -C tx -f img/w_gnu.tar.gz 2>/dev/null
  # busybox restores neither the times of directories and links nor the owners of links
  python3 scan.py tx | cut -f1-5,7-8 | grep -v "	l	0777	" | cmp -s - <(cut -f1-5,7-8 tree.tar.nox.ref | grep -v "	l	0777	") && ok "busybox extracts the .tar.gz identically" || bad "busybox extraction differs"; rm -rf tx; fi

# ------------------------------------------------------ folders and ACLs
echo "folders"
rm -rf hx; $F extract img/xz.sqfs hx 2>out/hx.err && $F pack hx img/w_repacked.sqfs comp=lz4 2>>out/hx.err && $F dump img/w_repacked.sqfs 2>/dev/null | cmp -s - tree.ref \
  && ok "extract to a folder + pack gives the same tree" || bad "extract + pack differs: $(tail -2 out/hx.err)"
if command -v setfacl >/dev/null; then
  rm -rf acl; mkdir -p acl/d; echo x > acl/f; setfacl -m u:1000:rw,g:50:r,m::rw acl/f 2>/dev/null; setfacl -d -m u:33:rwx acl/d 2>/dev/null
  touch -d "1960-01-01 UTC" acl/f; touch -d "2100-01-01 UTC" acl/d; touch -d "2020-01-01 UTC" acl
  python3 scan.py acl > acl.ref
  (cd acl && tar -c --format=pax --acls --xattrs --xattrs-include='*' -f ../img/acl.tar .)
  mke2fs -q -F -t ext4 -d acl img/acl.img 16M >/dev/null 2>&1; mount -o loop,ro img/acl.img mnt; python3 scan.py mnt | grep -v "^/lost+found" > acl.ext.ref; umount mnt
  mount -o loop,ro img/acl.img mnt; python3 scan.py mnt > acl.full.ref; umount mnt
  cmpdump "ext4 with POSIX ACLs and times before 1970 and after 2038" img/acl.img acl.full.ref
  cmpdump "tar with POSIX ACLs and times before 1970 and after 2038" img/acl.tar acl.ref
  ex acl_ext4 img/acl.img acl.ext.ref format=ext4
fi

# ------------------------------------------------------------ disk images
# SD card images as OpenWrt and Raspberry Pi OS ship them: a partition table, a FAT boot partition
# and the root filesystem in the second partition. FsForge must replace the root filesystem and
# leave every other byte of the disk alone.
echo "disk images"
cat > mkdisk.py <<'PY'
import struct, sys, zlib, uuid
# mkdisk.py mbr|gpt <out> <file length or 0> <type:offset:size:file|-> ...
kind, out, flen = sys.argv[1], sys.argv[2], int(sys.argv[3])
parts = []
for spec in sys.argv[4:]:
    t, off, size, f = spec.split(':')
    parts.append((t, int(off), int(size), None if f == '-' else open(f, 'rb').read()))
end = max(o + (len(d) if d is not None else 0) for _, o, _, d in parts)
total = flen or max(o + s for _, o, s, _ in parts) + (33 * 512 + 512 if kind == 'gpt' else 0)
disk = bytearray(max(total, end) if flen == 0 else flen)
def entry(i, t, off, size, boot=False):
    struct.pack_into('<B3sB3sII', disk, 446 + 16 * i, 0x80 if boot else 0, b'\xfe\xff\xff', t, b'\xfe\xff\xff', off // 512, min(size // 512, 0xffffffff))
disk[0:440] = bytes((i * 37 + 11) & 0xff for i in range(440))            # "boot code"
struct.pack_into('<I', disk, 440, 0x5452574f)                           # disk signature (PARTUUID)
disk[510:512] = b'\x55\xaa'
if kind == 'mbr':
    for i, (t, off, size, d) in enumerate(parts):
        entry(i, int(t, 16), off, size, i == 0)
else:
    sectors = len(disk) // 512
    entry(0, 0xee, 512, len(disk) - 512)
    names = {'efi': 'c12a7328-f81f-11d2-ba4b-00a0c93ec93b', 'linux': '0fc63daf-8483-4772-8e79-3d69d8477de4'}
    table = bytearray(128 * 128)
    for i, (t, off, size, d) in enumerate(parts):
        struct.pack_into('<16s16sQQQ', table, i * 128, uuid.UUID(names[t]).bytes_le, uuid.UUID(int=i + 1).bytes_le, off // 512, (off + size) // 512 - 1, 0)
        table[i * 128 + 56:i * 128 + 56 + 2 * len(t)] = t.encode('utf-16-le')
    def header(lba, other, tlba):
        h = bytearray(struct.pack('<8sIIIIQQQQ16sQIII', b'EFI PART', 0x10000, 92, 0, 0, lba, other, 34, sectors - 34,
                                  uuid.UUID(int=0x1234).bytes_le, tlba, 128, 128, zlib.crc32(table)))
        struct.pack_into('<I', h, 16, zlib.crc32(h))
        disk[lba * 512:lba * 512 + 92] = h
    disk[1024:1024 + len(table)] = table
    disk[(sectors - 33) * 512:(sectors - 33) * 512 + len(table)] = table
    header(1, sectors - 1, 2); header(sectors - 1, 1, sectors - 33)
for t, off, size, d in parts:
    if d is not None:
        d = d[:max(0, len(disk) - off)]
        disk[off:off + len(d)] = d
open(out, 'wb').write(disk)
PY
M=1048576; BOOT=$((2*M)); BOOTSZ=$((16*M)); ROOT=$((18*M))          # the layout of the OpenWrt zynq sdcard image
rm -rf disk; mkdir -p disk/bootfiles disk/x
# A real FAT boot partition with files in it.
head -c 300000 /dev/urandom > disk/bootfiles/boot.bin; head -c 3000000 /dev/urandom > disk/bootfiles/uImage; echo "console=ttyPS0,115200" > disk/bootfiles/uEnv.txt
truncate -s $BOOTSZ disk/boot.fat; busybox mkdosfs -n BOOT disk/boot.fat >/dev/null 2>&1
FATOK=0; if mount -o loop disk/boot.fat mnt 2>/dev/null; then cp -r disk/bootfiles/. mnt/; mkdir mnt/overlays; echo dtb > mnt/overlays/a.dtbo; umount mnt; FATOK=1
  else cat disk/bootfiles/* | dd of=disk/boot.fat bs=1M seek=1 conv=notrunc 2>/dev/null; fi      # no vfat in this kernel: raw data in the volume
fatsum() { # image offset : checksums of all files of the FAT partition (of its raw bytes when the kernel has no vfat)
  if [ $FATOK = 1 ] && mount -o loop,ro,offset=$2 "$1" mnt 2>/dev/null; then (cd mnt && find . -type f | sort | xargs sha256sum); umount mnt
  else dd if="$1" bs=1M skip=$(($2 / M)) count=$((BOOTSZ / M)) 2>/dev/null | sha256sum; fi; }
# The root filesystem the way OpenWrt builds it: xz, 256 KiB blocks, padded to 64 KiB; the file ends there.
mksquashfs tree disk/root.sqfs -noappend -quiet -no-progress -comp xz -b 256k >/dev/null 2>&1
SQ=$(stat -c %s disk/root.sqfs); SQPAD=$(( (SQ + 65535) / 65536 * 65536 )); ROOTSZ=$((SQPAD + 8*M))
python3 mkdisk.py mbr disk/sdcard.img $((ROOT + SQPAD)) c:$BOOT:$BOOTSZ:disk/boot.fat 83:$ROOT:$ROOTSZ:disk/root.sqfs
gzip -9nc disk/sdcard.img > disk/sdcard.img.gz
fatsum disk/sdcard.img $BOOT > disk/fat.ref
[ $FATOK = 1 ] && echo "  (the FAT partition is compared file by file)" || echo "  (no vfat in this kernel: the FAT partition is compared byte by byte)"
ptable() { busybox fdisk -l "$1" 2>/dev/null | grep "^$1" | sed "s|^$1||"; }

$F info disk/sdcard.img.gz > out/d.info 2>&1
grep -q "MBR partition table" out/d.info && grep -q "^  \* 2: SquashFS" out/d.info && grep -q "1: FAT32 \"BOOT\"" out/d.info \
  && ok "info shows the partitions of sdcard.img.gz" || { bad "info on sdcard.img.gz"; cat out/d.info; }
cmpdump "root filesystem of sdcard.img.gz (partition 2 at 0x1200000)" disk/sdcard.img.gz tree.ref

# The same edit done by FsForge inside the image and by hand on a copy of the tree.
head -c 70000 /dev/urandom > disk/new.bin; touch -d "2024-03-03 03:03:03 UTC" disk/new.bin
rm -rf tree2; cp -a tree tree2; cp -a disk/new.bin tree2/etc/; rm tree2/bin/cat; chmod 600 tree2/etc/hostname
python3 scan.py tree2 | grep -v "^/etc	\|^/bin	" > disk/tree2.ref
EDIT="add:/etc:disk/new.bin rm:/bin/cat chmod:600:/etc/hostname"
dcheck() { # id name image root-offset reference-disk [filter] [reference tree] : what a card written from the image must satisfy
  local id=$1 n=$2 img=$3 off=$4 refdisk=$5 flt=${6:-^$} ref=${7:-disk/tree2.ref} r=1 raw=disk/x/$1.img
  case "$img" in *.gz) gzip -t "$img" 2>/dev/null || { bad "$n: gzip rejects the file"; return; }; gzip -dc "$img" > $raw;;
                 *.xz) xz -t "$img" 2>/dev/null || { bad "$n: xz rejects the file"; return; }; xz -dc "$img" > $raw;;
                 *) cp "$img" $raw;; esac
  # (an enlarged partition has a new size in the table: compare from the second sector on)
  cmp -s -n $((off - 512)) -i 512 $raw $refdisk || { bad "$n: the bytes in front of the root partition changed"; r=0; }
  [ -n "${GROWN:-}" ] || cmp -s -n 512 $raw $refdisk || { bad "$n: the first sector (partition table) changed"; r=0; }
  [ "$(ptable $raw)" = "$(ptable $refdisk)" ] || [ -n "${GROWN:-}" ] || { bad "$n: fdisk shows another partition table"; r=0; }
  [ "$(fatsum $raw $BOOT)" = "$(cat disk/fat.ref)" ] || { bad "$n: the FAT boot partition changed"; r=0; }
  if mount -o loop,ro,offset=$off $raw mnt 2> out/d.$id.err; then python3 scan.py mnt | grep -v "^/etc	\|^/bin	\|$flt" > out/d.$id.txt; umount mnt
    cmp -s out/d.$id.txt $ref || { bad "$n: the kernel sees another tree in the root partition"; diff out/d.$id.txt $ref | head -4 | cut -c1-200; r=0; }
  else bad "$n: the kernel cannot mount the root partition: $(cat out/d.$id.err)"; r=0; fi
  [ $r = 1 ] && ok "$n"
}
if $F edit disk/sdcard.img.gz disk/w_sdcard.img.gz $EDIT verify=content 2> out/d.edit.err; then
  dcheck sd "edited sdcard.img.gz: boot partition intact, kernel mounts the new root filesystem" disk/w_sdcard.img.gz $ROOT disk/sdcard.img
  raw=disk/x/sd.img
  unsquashfs -o $ROOT -s $raw > out/d.unsq 2>&1 && grep -q "Compression xz" out/d.unsq && grep -q "Block size 262144" out/d.unsq \
    && ok "unsquashfs reads the partition: xz, 256 KiB blocks as before" || { bad "unsquashfs on the partition"; head -5 out/d.unsq; }
  # libfstools looks for the overlay at the next 64 KiB boundary: it must find zeros there (= format on first boot).
  used=$(unsquashfs -o $ROOT -s $raw | sed -n 's/^Filesystem size \([0-9]*\) bytes.*/\1/p'); ov=$(( ROOT + (used + 65535) / 65536 * 65536 ))
  len=$(stat -c %s $raw)
  if [ $len -ge $((ov + 65536)) ] && [ "$(dd if=$raw bs=65536 skip=$((ov / 65536)) count=1 2>/dev/null | tr -d '\0' | wc -c)" = 0 ] && [ $len -le $((ROOT + ROOTSZ)) ]
  then ok "64 KiB of zeros at the overlay offset 0x$(printf %X $ov), image ends inside the partition"; else bad "overlay area: image length $len, overlay at $ov"; fi
else bad "edit sdcard.img.gz: $(cat out/d.edit.err)"; fi

# Saving over the opened file.
cp disk/sdcard.img.gz disk/inplace.img.gz
$F edit disk/inplace.img.gz disk/inplace.img.gz $EDIT 2> out/d.inplace.err \
  && dcheck inplace "sdcard.img.gz edited in place" disk/inplace.img.gz $ROOT disk/sdcard.img || bad "edit in place: $(cat out/d.inplace.err)"
ls disk | grep -q "fsforge-tmp" && bad "temporary files left next to the image" || ok "no temporary files left next to the image"

# A filesystem that outgrows the last partition: the partition grows, nothing else moves.
head -c $((9*M)) /dev/urandom > disk/big.bin
if $F edit disk/sdcard.img.gz disk/w_grown.img $EDIT add:/var:disk/big.bin effort=0 2> out/d.grow.err && grep -q "enlarged" out/d.grow.err; then
  cp -a disk/big.bin tree2/var/; python3 scan.py tree2 | grep -v "^/etc	\|^/bin	\|^/var	" > disk/tree3.ref; rm tree2/var/big.bin
  newsz=$(( $(busybox fdisk -l disk/w_grown.img 2>/dev/null | awk '$1 ~ /img2$/ {print $6}') * 512 ))
  GROWN=1 dcheck grown "partition 2 enlarged for a larger filesystem ($((ROOTSZ / M)) -> $((newsz / M)) MiB)" disk/w_grown.img $ROOT disk/sdcard.img "^/var	" disk/tree3.ref
  cmp -s -n 458 disk/w_grown.img disk/sdcard.img && [ $newsz -gt $ROOTSZ ] && [ $(stat -c %s disk/w_grown.img) -le $((ROOT + newsz)) ] \
    && ok "only the size field of the partition table changed" || bad "partition table after growing: size $newsz"
else bad "growing the last partition: $(tail -2 out/d.grow.err)"; fi

# The filesystem alone, when asked for.
$F convert disk/sdcard.img.gz disk/w_root.squashfs 2> out/d.alone.err && [ "$(head -c 4 disk/w_root.squashfs)" = hsqs ] && kmount alone disk/w_root.squashfs tree.ref \
  && ok "convert to .squashfs writes the filesystem alone" || bad "filesystem alone: $(cat out/d.alone.err)"

# Raspberry Pi style: FAT + ext4 made by mke2fs with all the modern features, xz compressed.
EXTSZ=$((64*M)); rm -f disk/root.ext4
mke2fs -q -F -t ext4 -L rootfs -d tree disk/root.ext4 $((EXTSZ / 1024)) >/dev/null 2>&1
python3 mkdisk.py mbr disk/raspi.img 0 c:$BOOT:$BOOTSZ:disk/boot.fat 83:$ROOT:$EXTSZ:disk/root.ext4
xz -T0 -1 -c disk/raspi.img > disk/raspi.img.xz
mount -o loop,ro disk/root.ext4 mnt; python3 scan.py mnt > disk/ext.ref; umount mnt
cmpdump "ext4 root partition of raspi.img.xz" disk/raspi.img.xz disk/ext.ref
if $F edit disk/raspi.img.xz disk/w_raspi.img.xz $EDIT verify=content effort=0 2> out/d.raspi.err; then
  mount -o loop disk/root.ext4 mnt; cp -a disk/new.bin mnt/etc/; rm mnt/bin/cat; chmod 600 mnt/etc/hostname; python3 scan.py mnt | grep -v "^/etc	\|^/bin	" > disk/ext2.ref; umount mnt
  dcheck raspi "edited raspi.img.xz: boot partition intact, kernel mounts the ext4 root" disk/w_raspi.img.xz $ROOT disk/raspi.img "^$" disk/ext2.ref
  raw=disk/x/raspi.img
  [ $(stat -c %s $raw) = $(stat -c %s disk/raspi.img) ] && dd if=$raw of=disk/x/p2.ext4 bs=1M skip=18 2>/dev/null && e2fsck -fn disk/x/p2.ext4 > out/d.fsck 2>&1 \
    && [ "$(dumpe2fs -h disk/x/p2.ext4 2>/dev/null | sed -n 's/^Block count: *//p')" = $((EXTSZ / 4096)) ] \
    && [ "$(blkid -o value -s UUID disk/x/p2.ext4)" = "$(blkid -o value -s UUID disk/root.ext4)" ] \
    && ok "e2fsck: the ext4 partition is clean, same size and UUID as before" || { bad "ext4 partition after the edit"; tail -3 out/d.fsck; }
else bad "edit raspi.img.xz: $(cat out/d.raspi.err)"; fi

# GPT (OpenWrt x86 EFI images): primary and backup tables with valid checksums must survive untouched.
GSZ=$((SQPAD + 4*M)); python3 mkdisk.py gpt disk/gpt.img 0 efi:$BOOT:$BOOTSZ:disk/boot.fat linux:$ROOT:$GSZ:disk/root.sqfs
$F info disk/gpt.img > out/d.gpt 2>&1
if grep -q "GPT partition table" out/d.gpt && $F edit disk/gpt.img disk/w_gpt.img $EDIT 2> out/d.gpt.err; then
  dcheck gpt "GPT disk: partitions in front intact, kernel mounts the new root filesystem" disk/w_gpt.img $ROOT disk/gpt.img
  cmp -s <(tail -c $((34*512)) disk/gpt.img) <(tail -c $((34*512)) disk/w_gpt.img) && [ $(stat -c %s disk/w_gpt.img) = $(stat -c %s disk/gpt.img) ] \
    && ok "backup GPT at the end of the disk is unchanged" || bad "backup GPT changed"
  if command -v partx >/dev/null; then [ "$(partx -g -o NR,START,END,NAME disk/w_gpt.img 2>&1)" = "$(partx -g -o NR,START,END,NAME disk/gpt.img 2>&1)" ] && [ -n "$(partx -g disk/w_gpt.img 2>/dev/null)" ] \
    && ok "partx (libblkid) reads the same GPT from the saved disk" || bad "partx output differs: $(partx -g disk/w_gpt.img 2>&1 | head -2)"; fi
else bad "GPT disk: $(cat out/d.gpt out/d.gpt.err 2>/dev/null | head -3)"; fi
rm -rf disk/x tree2

echo; echo "$PASS passed, $FAIL failed"; [ $FAIL = 0 ]
