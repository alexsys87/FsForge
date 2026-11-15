# FAT checks. Sourced by verify-linux.sh, which provides $F (the CLI), scan.py, mnt/ and ok/bad;
# everything else lives in fat/.
#
#   fat_run      images made by mkfs.fat (FAT12, FAT16, FAT32, 512 B clusters, 4 KiB sectors) and filled by mcopy
#                are read by FsForge; images written by FsForge are checked by fsck.fat, unpacked by mcopy and read
#                back by FsForge; an SD card image (MBR, FAT32 boot partition with hidden sectors, ext4 root) gets
#                its boot partition edited in place
#   fat_kernel   every image FsForge wrote is mounted by the kernel (vfat), listed, written to, checked by
#                fsck.fat and read by FsForge again
#
# FAT keeps no owners, permissions, links or special files, and times in steps of 2 seconds: the listings
# compare path, type, content and the time of files rounded down to 2 s. mtools cannot handle characters
# beyond the BMP, so the tree has none (the self-tests cover them).

fat_list() { awk -F'\t' 'BEGIN{OFS="\t"} $1 != "/" {t=$6; if ($2=="f") t=t-t%2; else t="-"; print $1,$2,t,$7}'; }

fat_tree() {
  rm -rf fat; mkdir -p fat/tree fat/img fat/out
  python3 - fat/tree <<'PY'
import os, sys, random
r = random.Random(3)
root = sys.argv[1]
def put(p, data, t=1700000000):
    full = os.path.join(root, p); os.makedirs(os.path.dirname(full), exist_ok=True)
    open(full, 'wb').write(data); os.utime(full, (t, t))
put('config.txt', b'dtparam=audio=on\ncamera_auto_detect=1\n' * 20)
put('cmdline.txt', b'console=serial0,115200 root=PARTUUID=1234-02 rootfstype=ext4 fsck.repair=yes rootwait\n')
put('start4.elf', bytes(r.getrandbits(8) for _ in range(300000)))
put('overlays/README', b'overlays\n' * 500)
for i in range(30): put(f'overlays/overlay-number-{i:02}.dtbo', bytes(r.getrandbits(8) for _ in range(1000 + i * 97)))
put('Long File Name 1.txt', b'one\n'); put('Long File Name 2.txt', b'two\n'); put('Long File Name 3.txt', b'three\n')
put('.hidden-config', b'dot\n'); put('multi.dot.name.tar.gz', b'x' * 5000); put('name with spaces.txt', b'spaces\n')
put('UPPER.TXT', b'upper\n'); put('lower.txt', b'lower\n'); put('MiXeD.Txt', b'mixed\n')
put('пример/файл на русском.txt', 'юникод\n'.encode()); put('日本語.txt', b'jp\n')
put('empty.bin', b''); put('n' * 200 + '.txt', b'long\n')
for n in (511, 512, 513, 2047, 2048, 2049, 4096, 65536, 65537): put(f'sizes/s{n}', bytes((i * 13) % 251 for i in range(n)))
put('deep/' + '/'.join(f'level{i}' for i in range(10)) + '/leaf.txt', b'leaf\n')
for i in range(150): put(f'many/file-{i:03}.dat', str(i).encode())
os.makedirs(os.path.join(root, 'emptydir'))
PY
  python3 scan.py fat/tree | fat_list > fat/tree.ref
  tar -C fat/tree -cf fat/tree.tar .
}

fat_dump() { $F dump "$1" 2> "$2" | fat_list; }   # image err-file

fat_mk() { # name size-in-KiB mkfs.fat-options... : an image made by mkfs.fat and filled by mcopy, read by FsForge
  local n=$1 size=$2; shift 2
  rm -f fat/img/$n.img
  if mkfs.fat -C fat/img/$n.img $size "$@" > fat/out/mk.$n.log 2>&1 && mcopy -s -m -i fat/img/$n.img fat/tree/* fat/tree/.hidden-config ::/ > fat/out/mc.$n.log 2>&1; then
    if fat_dump fat/img/$n.img fat/out/r.$n.err > fat/out/r.$n.txt && cmp -s fat/out/r.$n.txt fat/tree.ref; then ok "read  mkfs.fat $n"
    else bad "read  mkfs.fat $n: $(head -c 300 fat/out/r.$n.err)"; diff fat/out/r.$n.txt fat/tree.ref | head -6 | cut -c1-180; fi
  else bad "mkfs.fat/mcopy $n: $(cat fat/out/mk.$n.log fat/out/mc.$n.log | tail -3)"; fi
}

FAT_WRITTEN=""
fat_wr() { # name fsforge-options... : an image written by FsForge, checked by fsck.fat, mcopy and FsForge
  local n=$1; shift
  if ! $F convert fat/tree.tar fat/img/w_$n.vfat verify=content "$@" > fat/out/w.$n.log 2>&1; then bad "write $n: $(cat fat/out/w.$n.log)"; return; fi
  if ! fsck.fat -n -v fat/img/w_$n.vfat > fat/out/fsck.$n.txt 2>&1; then bad "fsck.fat $n: $(grep -iv '^ \|^fsck\|^Checking\|^Boot\|^Media\|^[0-9]' fat/out/fsck.$n.txt | head -4)"; return; fi
  fat_dump fat/img/w_$n.vfat fat/out/o.$n.err > fat/out/o.$n.txt
  if ! cmp -s fat/out/o.$n.txt fat/tree.ref; then bad "write $n: read back differs"; diff fat/out/o.$n.txt fat/tree.ref | head -4 | cut -c1-180; return; fi
  rm -rf fat/x; mkdir fat/x; mcopy -s -m -i fat/img/w_$n.vfat ::/ fat/x/ > fat/out/x.$n.log 2>&1
  python3 scan.py fat/x | fat_list > fat/out/x.$n.txt
  if cmp -s fat/out/x.$n.txt fat/tree.ref; then ok "write $n ($($F info fat/img/w_$n.vfat 2>&1 | sed -n 's/^FAT: *\([^(]*\) (.*/\1/p'); fsck.fat, mcopy, read back)"; FAT_WRITTEN="$FAT_WRITTEN $n"
  else bad "write $n: mtools sees a different tree"; diff fat/out/x.$n.txt fat/tree.ref | head -6 | cut -c1-180; fi
}

fat_run() {
  # mtools needs a UTF-8 locale for long names beyond ASCII; the caller's locale comes back at the end.
  local old_lang=${LANG-} old_lc=${LC_ALL-}
  export TZ=UTC MTOOLS_SKIP_CHECK=1 LANG=C.UTF-8 LC_ALL=C.UTF-8
  fat_tree
  fat_mk fat12 8192 -F 12 -n SMALL
  fat_mk fat16 65536 -F 16 -n BOOT
  fat_mk fat16c512 32768 -F 16 -s 1
  fat_mk fat32 262144 -F 32 -n bootfs
  fat_mk fat32big 1048576 -F 32 -s 8
  fat_mk fat32s4k 524288 -F 32 -S 4096

  fat_wr auto
  fat_wr fat12 fattype=12 size=16
  fat_wr fat16 fattype=16 size=64 label=BOOT
  fat_wr fat16c512 fattype=16 size=32 cluster=512
  fat_wr fat32 fattype=32 size=256 label=bootfs
  fat_wr fat32c4k fattype=32 size=300 cluster=4096 serial=1234-ABCD
  fat_wr free free=20

  # The root of FAT12/16 holds a fixed number of entries: more than 512 names there must still work.
  rm -rf fat/wide; mkdir fat/wide
  for i in $(seq -w 0 699); do echo $i > fat/wide/root-entry-with-a-long-name-$i.txt; done
  tar -C fat/wide -cf fat/wide.tar .
  if $F convert fat/wide.tar fat/img/w_wide.vfat fattype=16 size=64 verify=content > fat/out/w.wide.log 2>&1 && fsck.fat -n fat/img/w_wide.vfat > fat/out/fsck.wide.txt 2>&1 \
     && [ "$(mdir -i fat/img/w_wide.vfat -b ::/ | wc -l)" = 700 ]; then ok "a FAT16 root with 700 long names"
  else bad "wide root: $(cat fat/out/w.wide.log fat/out/fsck.wide.txt | tail -3)"; fi

  # An SD card: MBR, a FAT32 boot partition made by mkfs.fat with hidden sectors, an ext4 root; partition 1 is edited.
  rm -f fat/img/card.img fat/img/boot.img fat/img/root.img
  if mkfs.fat -C fat/img/boot.img 65536 -F 32 -s 1 -n BOOTFS -h 8192 > /dev/null && mcopy -s -m -i fat/img/boot.img fat/tree/config.txt fat/tree/cmdline.txt fat/tree/overlays ::/ \
     && mke2fs -q -F -t ext4 -d fat/tree/sizes fat/img/root.img 16M > /dev/null && python3 - <<'PY'
import struct
boot = open('fat/img/boot.img','rb').read(); root = open('fat/img/root.img','rb').read()
p1, p2 = 8192 * 512, 8192 * 512 + len(boot)
disk = bytearray(p2 + len(root))
disk[446:462] = struct.pack('<B3sB3sII', 0x80, b'\xfe\xff\xff', 0x0C, b'\xfe\xff\xff', p1 // 512, len(boot) // 512)
disk[462:478] = struct.pack('<B3sB3sII', 0, b'\xfe\xff\xff', 0x83, b'\xfe\xff\xff', p2 // 512, len(root) // 512)
disk[510:512] = b'\x55\xaa'
disk[p1:p1 + len(boot)] = boot; disk[p2:] = root
open('fat/img/card.img','wb').write(disk)
PY
  then
    printf 'dtparam=audio=on\nenable_uart=1\n' > fat/out/config.txt
    if $F edit fat/img/card.img fat/img/card2.img add:/:fat/out/config.txt rm:/cmdline.txt part=1 verify=content > fat/out/card.log 2>&1; then
      local B=$(( $(stat -c %s fat/img/boot.img) / 512 )) r1=0 r2=0 r3=0 r4=0
      dd if=fat/img/card2.img of=fat/img/card-p1.img bs=512 skip=8192 count=$B status=none
      fsck.fat -n fat/img/card-p1.img > fat/out/fsck.card.txt 2>&1 && r1=1
      [ "$(mtype -i fat/img/card-p1.img ::/config.txt)" = "$(cat fat/out/config.txt)" ] && ! mdir -i fat/img/card-p1.img ::/cmdline.txt > /dev/null 2>&1 && r2=1
      cmp -s <(head -c $((8192*512)) fat/img/card.img) <(head -c $((8192*512)) fat/img/card2.img) \
        && cmp -s <(tail -c +$(((8192+B)*512+1)) fat/img/card.img) <(tail -c +$(((8192+B)*512+1)) fat/img/card2.img) && r3=1
      [ "$($F info fat/img/card2.img part=1 2>&1 | grep -c "hidden sectors 8192")" = 1 ] && [ "$(fatlabel fat/img/card-p1.img 2>&1)" = BOOTFS ] && r4=1
      [ $r1$r2$r3$r4 = 1111 ] && ok "SD card: partition 1 (FAT32) edited in place; fsck.fat, mtools; table and root partition unchanged" \
        || bad "SD card: fsck=$r1 content=$r2 rest=$r3 label/hidden=$r4 $(tail -3 fat/out/fsck.card.txt)"
      [ "$($F info fat/img/card2.img 2>&1 | grep -c '^Format: *ext4')" = 1 ] && ok "SD card: the root partition still opens by default" \
        || bad "default partition: $($F info fat/img/card2.img 2>&1 | head -3)"
    else bad "edit card: $(cat fat/out/card.log)"; fi
  else bad "SD card: mkfs.fat, mcopy or mke2fs failed"; fi
  export LANG=$old_lang LC_ALL=$old_lc
}

fat_kernel() {
  local n
  for n in $FAT_WRITTEN; do
    umount mnt 2>/dev/null
    if ! mount -t vfat -o loop,ro,utf8,tz=UTC fat/img/w_$n.vfat mnt 2> fat/out/k.$n.err; then bad "kernel cannot mount w_$n.vfat: $(cat fat/out/k.$n.err)"; continue; fi
    python3 scan.py mnt | fat_list > fat/out/k.$n.txt; umount mnt
    if ! cmp -s fat/out/k.$n.txt fat/tree.ref; then bad "kernel sees a different tree in w_$n.vfat"; diff fat/out/k.$n.txt fat/tree.ref | head -4 | cut -c1-180; continue; fi
    # Written to by the kernel, the image must stay consistent and readable by FsForge.
    cp fat/img/w_$n.vfat fat/img/k_$n.vfat
    if mount -t vfat -o loop,utf8,tz=UTC fat/img/k_$n.vfat mnt 2>> fat/out/k.$n.err; then
      echo "written by the kernel" > "mnt/kernel note.txt"; rm -f mnt/cmdline.txt; mkdir -p mnt/new/dir; sync; umount mnt
      rm -rf fat/kx; $F extract fat/img/k_$n.vfat fat/kx > fat/out/kc.$n.err 2>&1
      if fsck.fat -n fat/img/k_$n.vfat > fat/out/kfsck.$n.txt 2>&1 && [ "$(cat "fat/kx/kernel note.txt")" = "written by the kernel" ] \
         && [ -d fat/kx/new/dir ] && [ ! -e fat/kx/cmdline.txt ] && cmp -s fat/kx/start4.elf fat/tree/start4.elf; then ok "kernel mounts w_$n.vfat, sees the tree, writes to it; fsck.fat and FsForge agree"
      else bad "after the kernel wrote to w_$n.vfat: $(tail -3 fat/out/kfsck.$n.txt fat/out/kc.$n.err)"; fi
    else bad "kernel cannot mount w_$n.vfat for writing: $(cat fat/out/k.$n.err)"; fi
  done
  # The boot partition edited inside the SD card image, mounted from its offset.
  if [ -f fat/img/card2.img ] && mount -t vfat -o loop,ro,offset=$((8192*512)) fat/img/card2.img mnt 2> fat/out/k.card.err; then
    if [ "$(cat mnt/config.txt)" = "$(cat fat/out/config.txt)" ] && [ ! -e mnt/cmdline.txt ]; then ok "kernel mounts the edited boot partition of the SD card"
    else bad "kernel sees a different boot partition"; fi
    umount mnt
  else bad "kernel cannot mount the boot partition of the SD card: $(cat fat/out/k.card.err 2>&1)"; fi
}
