# JFFS2 checks. Sourced by verify-linux.sh, which provides $F (the CLI), scan.py, mnt/ and ok/bad;
# everything else lives in j2/.
#
#   j2_run       mkfs.jffs2 images (both byte orders, several erase block sizes, every compressor, without
#                clean markers, padded, with xattrs) are read by FsForge and compared with the source tree;
#                images written by FsForge are checked by jffs2dump, unpacked by jefferson when it is
#                installed, read by jffs2reader and read back by FsForge itself
#   j2_kernel    where mtdram and jffs2 can be loaded: every image FsForge wrote for 64 KiB blocks is put on a
#                simulated NOR flash, mounted by the kernel, listed, written to, and mounted again
#
# The tree has no hard links: jefferson and jffs2reader both keep only one name of a hard-linked file, also
# for images made by mkfs.jffs2. mkfs.jffs2 cuts symlink targets to 254 bytes and device numbers to 8+8 bits,
# so those two entries are left out when its images are compared.

j2_tree() {
  rm -rf j2; mkdir -p j2/tree j2/img j2/out
  python3 - j2/tree <<'PY'
import os, sys, random, struct
r = random.Random(7)
root = sys.argv[1]
def put(p, data, mode=0o644, t=1700000000):
    full = os.path.join(root, p); os.makedirs(os.path.dirname(full), exist_ok=True)
    open(full, 'wb').write(data); os.chmod(full, mode); os.utime(full, (t, t))
put('etc/config/network', b'config interface "lan"\n\toption proto static\n' * 300)
put('etc/passwd', b'root:x:0:0:root:/root:/bin/ash\n')
put('etc/empty', b'')
put('bin/busybox', bytes(r.getrandbits(8) for _ in range(150000)), 0o4755)
for n in (1, 4095, 4096, 4097, 8192, 12289):
    put(f'sizes/f{n}', bytes((i * 7) % 251 for i in range(n)))
put('sizes/zeros', bytes(70000))
put('deep/' + '/'.join(f'd{i}' for i in range(12)) + '/leaf', b'leaf\n')
put('names/' + 'n' * 254, b'long name\n')
put('names/пример файла', 'юникод\n'.encode())
put('www/index.html', b'<html>' + b'x' * 20000 + b'</html>', 0o600, 1600000000)
os.symlink('../bin/busybox', os.path.join(root, 'bin/sh'))
os.symlink('/tmp/' + 'y' * 300, os.path.join(root, 'longlink'))
os.mknod(os.path.join(root, 'null'), 0o20666, os.makedev(1, 3))
os.mknod(os.path.join(root, 'bigdev'), 0o60600, os.makedev(300, 70000))
os.mkfifo(os.path.join(root, 'fifo'))
os.chown(os.path.join(root, 'www/index.html'), 33, 33)
os.makedirs(os.path.join(root, 'emptydir'))
os.utime(os.path.join(root, 'emptydir'), (1500000000, 1500000000))
# Extended attributes and a POSIX ACL (getfacl form: version 2, then tag/perm/id entries).
p = os.path.join(root, 'etc/passwd')
os.setxattr(p, 'user.comment', b'hello')
os.setxattr(p, 'security.selinux', b'system_u:object_r:etc_t:s0\0')
acl = struct.pack('<I', 2) + b''.join(struct.pack('<HHI', t, perm, i) for t, perm, i in
      [(1, 6, 0xFFFFFFFF), (2, 4, 1000), (4, 4, 0xFFFFFFFF), (0x10, 4, 0xFFFFFFFF), (0x20, 4, 0xFFFFFFFF)])
os.setxattr(os.path.join(root, 'www/index.html'), 'system.posix_acl_access', acl)
PY
  python3 scan.py j2/tree | grep -v '^/	' > j2/tree.xattr.ref
  cut -f1-8 j2/tree.xattr.ref > j2/tree.ref                      # without the extended attributes column
  grep -v '^/bigdev	\|^/longlink	' j2/tree.ref > j2/tree.mkfs.ref
  # The source of the images FsForge writes: a tar archive of the tree keeps devices, owners, long links and xattrs.
  tar -C j2/tree --numeric-owner --xattrs --xattrs-include='*' -cf j2/tree.tar .
}

j2_mk() { # name mkfs.jffs2-options... : an image made by mkfs.jffs2, read by FsForge
  local n=$1; shift
  if mkfs.jffs2 -r j2/tree -o j2/img/$n.img "$@" > j2/out/mk.$n.log 2>&1; then
    $F dump j2/img/$n.img 2> j2/out/r.$n.err | grep -v '^/	\|^/bigdev	\|^/longlink	' | cut -f1-8 > j2/out/r.$n.txt
    if cmp -s j2/out/r.$n.txt j2/tree.mkfs.ref; then ok "read  mkfs.jffs2 $n ($(stat -c %s j2/img/$n.img) bytes)"
    else bad "read  mkfs.jffs2 $n: $(head -c 300 j2/out/r.$n.err)"; diff j2/out/r.$n.txt j2/tree.mkfs.ref | head -6 | cut -c1-180; fi
  else bad "mkfs.jffs2 $n: $(cat j2/out/mk.$n.log)"; fi
}

j2_wr() { # name fsforge-options... : an image written by FsForge, checked by the reference tools
  local n=$1; shift
  local img=j2/img/w_$n.jffs2 be=""
  case " $* " in *" endian=big "*) be=-b;; esac
  if ! $F convert j2/tree.tar $img verify=content "$@" > j2/out/w.$n.log 2>&1; then bad "write $n: $(cat j2/out/w.$n.log)"; return; fi
  jffs2dump -c $be $img > j2/out/d.$n.txt 2>&1
  if grep -qi "wrong\|error\|invalid\|mismatch" j2/out/d.$n.txt; then bad "jffs2dump $n: $(grep -i 'wrong\|error\|invalid\|mismatch' j2/out/d.$n.txt | head -3)"; return; fi
  # FsForge reads its own image back completely, owners and times included.
  $F dump $img 2>/dev/null | grep -v '^/	' | cut -f1-8 > j2/out/o.$n.txt
  if ! cmp -s j2/out/o.$n.txt j2/tree.ref; then bad "write $n: read back differs"; diff j2/out/o.$n.txt j2/tree.ref | head -4 | cut -c1-180; return; fi
  if command -v jefferson >/dev/null; then
    rm -rf j2/x; jefferson -d j2/x $img > j2/out/j.$n.log 2>&1
    # jefferson keeps neither owners nor times and skips FIFOs: compare type, mode, size/content and link targets.
    python3 scan.py j2/x | grep -v '^/	' | cut -f1,2,3,7,8 > j2/out/j.$n.txt
    grep -v '^/fifo	' j2/tree.ref | cut -f1,2,3,7,8 > j2/out/tree.cut
    if cmp -s j2/out/j.$n.txt j2/out/tree.cut; then ok "write $n ($(stat -c %s $img) bytes; jffs2dump, jefferson, read back)"
    else bad "write $n: jefferson sees a different tree"; diff j2/out/j.$n.txt j2/out/tree.cut | head -6 | cut -c1-180; fi
  else
    ok "write $n ($(stat -c %s $img) bytes; jffs2dump, read back; jefferson is not installed)"
  fi
}

j2_run() {
  j2_tree
  j2_mk le -e 64KiB
  j2_mk be -b -e 64KiB
  j2_mk nocm -n -e 128KiB
  j2_mk e8k -e 8KiB
  j2_mk e256k -e 256KiB -l
  j2_mk lzo -X lzo -x zlib -e 64KiB
  j2_mk rtime -x zlib -e 64KiB
  j2_mk nocomp -x zlib -x rtime -e 64KiB
  j2_mk pad -p -e 64KiB
  j2_mk page2k -s 2048 -e 64KiB
  if mkfs.jffs2 -r j2/tree -o j2/img/xattr.img -e 64KiB --with-xattr > j2/out/mk.xattr.log 2>&1; then
    $F dump j2/img/xattr.img 2> j2/out/r.xattr.err | grep -v '^/	\|^/bigdev	\|^/longlink	' > j2/out/r.xattr.txt
    if grep -v '^/bigdev	\|^/longlink	' j2/tree.xattr.ref | cmp -s - j2/out/r.xattr.txt; then ok "read  mkfs.jffs2 --with-xattr (attributes, SELinux label, POSIX ACL)"
    else bad "read  mkfs.jffs2 --with-xattr: $(head -c 200 j2/out/r.xattr.err)"; grep -v '^/bigdev	\|^/longlink	' j2/tree.xattr.ref | diff j2/out/r.xattr.txt - | head -6 | cut -c1-180; fi
  else bad "mkfs.jffs2 --with-xattr: $(cat j2/out/mk.xattr.log)"; fi
  [ "$($F info j2/img/e256k.img 2>&1 | grep -c 'erase block 262144')" = 1 ] && [ "$($F info j2/img/be.img 2>&1 | grep -c 'big-endian, erase block 65536,')" = 1 ] \
    && ok "byte order and erase block size of mkfs.jffs2 images are recognised" || bad "layout: $($F info j2/img/e256k.img 2>&1 | grep Layout)"

  j2_wr le
  j2_wr be endian=big
  j2_wr nocm cleanmarkers=0 erase=131072
  j2_wr lzo comp=lzo
  j2_wr rtime comp=rtime
  j2_wr none comp=none
  j2_wr e8k erase=8192 pagesize=2048
  j2_wr pad pad=1 erase=65536
  for e in little big; do
    if $F convert j2/tree.tar j2/img/w_x$e.jffs2 xattrs=1 endian=$e verify=content > j2/out/w.x$e.log 2>&1 &&
       $F dump j2/img/w_x$e.jffs2 2>/dev/null | grep -v '^/	' | cmp -s - j2/tree.xattr.ref; then ok "write xattrs=1 ($e-endian): attributes and ACL read back"
    else bad "write xattrs=1 ($e): $(head -3 j2/out/w.x$e.log)"; fi
  done
  $F convert j2/tree.tar j2/img/w_nox.jffs2 > j2/out/w.nox.log 2>&1
  grep -q "Extended attributes of" j2/out/w.nox.log && ! jffs2dump -c j2/img/w_nox.jffs2 | grep -qi xattr \
    && ok "without xattrs=1 the attributes are left out, with a warning" || bad "xattrs off: $(cat j2/out/w.nox.log)"
  # mkfs.jffs2's own reader handles only files of one node correctly: compare a small file and a listing.
  if jffs2reader j2/img/w_le.jffs2 -f /etc/passwd > j2/out/rd.txt 2>&1 && cmp -s j2/out/rd.txt j2/tree/etc/passwd &&
     [ "$(jffs2reader j2/img/w_le.jffs2 -d /etc | awk '{print $1, $NF}')" = "$(jffs2reader j2/img/le.img -d /etc | awk '{print $1, $NF}')" ]; then
    ok "jffs2reader reads an image written by FsForge like one made by mkfs.jffs2"
  else bad "jffs2reader: $(head -3 j2/out/rd.txt)"; fi
  # Converting a JFFS2 image to the other formats.
  $F convert j2/img/le.img j2/img/c.squashfs verify=content > j2/out/c.log 2>&1 && $F dump j2/img/c.squashfs 2>/dev/null | grep -v '^/	\|^/bigdev	\|^/longlink	' | cut -f1-8 | cmp -s - j2/tree.mkfs.ref \
    && ok "JFFS2 -> SquashFS" || bad "JFFS2 -> SquashFS: $(cat j2/out/c.log)"
}

j2_modules() {
  grep -q mtdram /proc/mtd 2>/dev/null || modprobe mtdram total_size=32768 erase_size=64 2>/dev/null || return 1
  grep -q jffs2 /proc/filesystems || modprobe jffs2 2>/dev/null || return 1
  J2_MTD=$(sed -n 's/^mtd\([0-9]*\):.*mtdram.*/\1/p' /proc/mtd | head -1)
  [ -n "$J2_MTD" ] && command -v flash_erase >/dev/null || return 1
  mkdir -p mnt
}

j2_mount() { # image : erases the simulated NOR flash, writes the image to it and mounts it
  umount mnt 2>/dev/null
  flash_erase -q /dev/mtd$J2_MTD 0 0 && dd if=$1 of=/dev/mtd$J2_MTD bs=64k 2>/dev/null && mount -t jffs2 mtd$J2_MTD mnt 2> j2/out/k.mount
}

j2_kernel() {
  for n in le rtime none pad; do              # lzo needs CONFIG_JFFS2_LZO, which many kernels leave out
    if j2_mount j2/img/w_$n.jffs2; then
      python3 scan.py mnt | grep -v '^/	' | cut -f1-8 > j2/out/k.$n.txt
      echo changed > mnt/etc/passwd && sync && umount mnt && mount -t jffs2 mtd$J2_MTD mnt 2>> j2/out/k.mount
      if cmp -s j2/out/k.$n.txt j2/tree.ref && [ "$(cat mnt/etc/passwd)" = changed ]; then ok "kernel mounts $n (64 KiB NOR), lists it, writes to it, mounts it again"
      else bad "kernel and $n: $(diff j2/out/k.$n.txt j2/tree.ref | head -3 | cut -c1-160) $(dmesg | grep -i jffs2 | tail -2)"; fi
      umount mnt 2>/dev/null
    else bad "kernel does not mount $n: $(cat j2/out/k.mount) $(dmesg | grep -i jffs2 | tail -2)"; fi
  done
}
