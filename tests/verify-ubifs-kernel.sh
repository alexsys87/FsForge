# Kernel checks of the UBIFS and UBI images FsForge writes. Sourced by verify-linux.sh, which provides
# $F (the CLI), the work directory with scan.py, tree1k, tree1k.ref, data, data.ref, img/ and out/, and ok/bad.
#
#   ubk_prepare   FsForge writes the images into ubk/ (needs no kernel support)
#   ubk_modules   loads nandsim, mtdram, ubi and ubifs; fails when the kernel has none of that
#   ubk_check     the kernel attaches every image to a simulated flash, mounts it with the UBIFS self-checks
#                 on, lists it, writes to it and mounts it again
#
# The simulated NAND has 128 KiB erase blocks, 2048-byte pages and 512-byte sub-pages; the NOR flash
# (mtdram) has 64 KiB erase blocks.

ubk_w() { # name source options... : one image written by FsForge
  local n=$1 src=$2; shift 2
  $F convert $src ubk/$n verify=content "$@" 2> out/ubk.$n.err || bad "write $n: $(head -c 300 out/ubk.$n.err)"
}

ubk_prepare() {
  rm -rf ubk; mkdir -p ubk
  local T=img/tree1k.sqfs
  ubk_w lzo.ubifs $T comp=lzo; ubk_w zlib.ubifs $T comp=zlib; ubk_w zstd.ubifs $T comp=zstd; ubk_w none.ubifs $T comp=none
  ubk_w fanout3.ubifs $T fanout=3 fixup=1 comp=zlib
  ubk_w biglpt.ubifs $T maxleb=400000                       # a table of LEB properties that needs the "big" layout
  ubk_w tight.ubifs $T maxleb=160 jrn=1                     # hardly more room than the content takes
  ubk_w nand.ubi $T                                         # a new UBI image, volume "rootfs" with autoresize
  ubk_w sub.ubi $T subpage=512 comp=zstd volname=root_fs volid=3
  ubk_w fixed.ubi $T autoresize=0 volsize=24                # a volume of a fixed size
  ubk_w sq.ubi $T format=squashfs autoresize=0              # SquashFS in a UBI volume (read through ubiblock)
  ubk_w nor.ubi $T peb=65536 minio=1 comp=zlib
  ubk_w nor.ubifs $T leb=65408 minio=1
  # A filesystem of one small file in a volume that is no larger than it must be: the journal has to fit the main area.
  $F pack data ubk/tiny.ubi autoresize=0 2> out/ubk.tiny.err || bad "write tiny.ubi: $(cat out/ubk.tiny.err)"
  $F pack data ubk/tiny.ubifs 2>> out/ubk.tiny.err || bad "write tiny.ubifs: $(cat out/ubk.tiny.err)"
  $F dump ubk/tiny.ubifs > ubk/tiny.ubifs.ref 2>/dev/null; $F dump ubk/tiny.ubi > ubk/tiny.ref 2>/dev/null                   # the root of a packed folder gets the time of packing
  # An image made by ubinize, edited: the volume "rootfs" is replaced, "kernel" and "rootfs_data" are carried over.
  $F edit img/nand.ubi ubk/edited.ubi rm:/bigtext add:/etc:img/kernel.bin verify=content 2> out/ubk.edited.err || bad "edit nand.ubi: $(cat out/ubk.edited.err)"
  $F dump ubk/edited.ubi > ubk/edited.ref 2>/dev/null
  $F edit img/nand.ubi ubk/edited_data.ubi vol=rootfs_data mkdir:/upper/new verify=content 2> out/ubk.edited2.err || bad "edit rootfs_data: $(cat out/ubk.edited2.err)"
  $F dump ubk/edited_data.ubi vol=rootfs_data > ubk/edited_data.ref 2>/dev/null
  $F convert img/sq.ubi ubk/sq2ubifs.ubi format=ubifs verify=content 2> out/ubk.sq2.err || bad "SquashFS volume -> UBIFS volume: $(cat out/ubk.sq2.err)"
}

ubk_modules() {
  grep -q "NAND simulator" /proc/mtd 2>/dev/null || modprobe nandsim first_id_byte=0x20 second_id_byte=0xaa third_id_byte=0x00 fourth_id_byte=0x15 2>/dev/null || return 1
  grep -q mtdram /proc/mtd 2>/dev/null || modprobe mtdram total_size=32768 erase_size=64 2>/dev/null || return 1
  [ -e /sys/class/ubi ] || modprobe ubi 2>/dev/null || return 1
  grep -q ubifs /proc/filesystems || modprobe ubifs 2>/dev/null || return 1
  UBK_NAND=$(sed -n 's/^mtd\([0-9]*\):.*NAND simulator.*/\1/p' /proc/mtd | head -1)
  UBK_NOR=$(sed -n 's/^mtd\([0-9]*\):.*mtdram.*/\1/p' /proc/mtd | head -1)
  [ -n "$UBK_NAND" ] && [ -n "$UBK_NOR" ] || return 1
  mount | grep -q " /sys/kernel/debug " || mount -t debugfs none /sys/kernel/debug 2>/dev/null
  # The self-checks of UBIFS: the index, the LEB properties and the accounting are verified on every commit.
  for f in /sys/kernel/debug/ubifs/chk_*; do [ -e $f ] && echo 1 > $f; done
  mkdir -p mnt
}

ubk_detach() { umount mnt 2>/dev/null; ubidetach -m $1 2>/dev/null; }

ubk_flash() { # mtd image [vid header offset] : writes a UBI image to the flash and attaches it
  local o=${3:+-O $3}
  ubiformat /dev/mtd$1 -y -q $o -f $2 > out/ubk.flash 2>&1 && ubiattach -m $1 $o > out/ubk.attach 2>&1
}

ubk_volume() { # mtd image [vid header offset] : an empty flash with one volume that holds a UBIFS image
  local o=${3:+-O $3}
  ubiformat /dev/mtd$1 -y -q $o > out/ubk.flash 2>&1 && ubiattach -m $1 $o > out/ubk.attach 2>&1 || return 1
  local d; d=$(sed -n 's/^UBI device number \([0-9]*\).*/\1/p' out/ubk.attach)
  ubimkvol /dev/ubi$d -N rootfs -m > /dev/null && ubiupdatevol /dev/ubi${d}_0 $2
}

ubk_dev() { sed -n 's/^UBI device number \([0-9]*\).*/\1/p' out/ubk.attach; }

ubk_read() { # label volume reference : the kernel mounts the volume and sees what was put in
  if mount -t ubifs $2 mnt 2> out/ubk.mount; then python3 scan.py mnt > out/ubk.scan.txt
    if cmp -s out/ubk.scan.txt $3; then ok "kernel mounts $1"; else bad "kernel reads $1 differently"; diff out/ubk.scan.txt $3 | head -4 | cut -c1-200; fi
  else bad "kernel cannot mount $1: $(cat out/ubk.mount; dmesg | grep -i ubifs | tail -2)"; return 1; fi
}

ubk_write() { # label volume : files are added and removed, and survive a remount
  head -c 200000 /dev/urandom > out/ubk.new.bin
  if cp out/ubk.new.bin mnt/vm-new.bin 2> out/ubk.cp && mkdir mnt/vm-dir && rm -f mnt/one-byte && sync && umount mnt && mount -t ubifs $2 mnt \
     && cmp -s out/ubk.new.bin mnt/vm-new.bin && [ -d mnt/vm-dir ] && [ ! -e mnt/one-byte ]; then ok "kernel writes to $1, the changes survive a remount"
  else bad "kernel cannot write to $1: $(cat out/ubk.cp)"; fi
}

ubk_check() {
  local n d before
  dmesg -c > /dev/null 2>&1
  for n in lzo zlib zstd none fanout3 biglpt tight; do
    if ubk_volume $UBK_NAND ubk/$n.ubifs 2048; then d=$(ubk_dev); ubk_read "$n.ubifs" ubi$d:rootfs tree1k.ref && ubk_write "$n.ubifs" ubi$d:rootfs
    else bad "flash $n.ubifs: $(tail -1 out/ubk.flash)"; fi; ubk_detach $UBK_NAND
  done
  if ubk_volume $UBK_NAND ubk/tiny.ubifs 2048; then d=$(ubk_dev); ubk_read "tiny.ubifs" ubi$d:rootfs ubk/tiny.ubifs.ref && ubk_write "tiny.ubifs" ubi$d:rootfs; else bad "flash tiny.ubifs"; fi; ubk_detach $UBK_NAND
  if ubk_volume $UBK_NOR ubk/nor.ubifs; then d=$(ubk_dev); ubk_read "nor.ubifs (NOR flash)" ubi$d:rootfs tree1k.ref && ubk_write "nor.ubifs" ubi$d:rootfs; else bad "flash nor.ubifs: $(tail -1 out/ubk.flash)"; fi; ubk_detach $UBK_NOR

  if ubk_flash $UBK_NAND ubk/nand.ubi 2048; then d=$(ubk_dev)
    ubk_read "nand.ubi (new UBI image)" ubi$d:rootfs tree1k.ref && ubk_write "nand.ubi" ubi$d:rootfs
    [ "$(df -k --output=size mnt | tail -1)" -gt 200000 ] && ok "the autoresize volume and its filesystem grew to the size of the flash" || bad "no autoresize: $(df -k mnt | tail -1)"
  else bad "flash nand.ubi: $(tail -1 out/ubk.attach)"; fi; ubk_detach $UBK_NAND
  if ubk_flash $UBK_NAND ubk/sub.ubi; then d=$(ubk_dev); ubk_read "sub.ubi (sub-pages, volume 3 'root_fs')" ubi$d:root_fs tree1k.ref && ubk_write "sub.ubi" ubi$d:root_fs
    [ -e /dev/ubi${d}_3 ] && ok "the volume number is the one asked for" || bad "volume 3 is missing"
  else bad "flash sub.ubi: $(tail -1 out/ubk.attach)"; fi; ubk_detach $UBK_NAND
  if ubk_flash $UBK_NAND ubk/fixed.ubi 2048; then d=$(ubk_dev); ubk_read "fixed.ubi (volume of a fixed size)" ubi$d:rootfs tree1k.ref && ubk_write "fixed.ubi" ubi$d:rootfs
  else bad "flash fixed.ubi: $(tail -1 out/ubk.attach)"; fi; ubk_detach $UBK_NAND
  if ubk_flash $UBK_NAND ubk/tiny.ubi 2048; then d=$(ubk_dev); ubk_read "tiny.ubi (smallest volume the kernel accepts)" ubi$d:rootfs ubk/tiny.ref && ubk_write "tiny.ubi" ubi$d:rootfs
  else bad "flash tiny.ubi: $(tail -1 out/ubk.attach)"; fi; ubk_detach $UBK_NAND
  if ubk_flash $UBK_NOR ubk/nor.ubi; then d=$(ubk_dev); ubk_read "nor.ubi (NOR flash)" ubi$d:rootfs tree1k.ref && ubk_write "nor.ubi" ubi$d:rootfs
  else bad "flash nor.ubi: $(tail -1 out/ubk.attach)"; fi; ubk_detach $UBK_NOR

  if ubk_flash $UBK_NAND ubk/edited.ubi 2048; then d=$(ubk_dev)
    ubk_read "edited.ubi: the replaced volume" ubi$d:rootfs ubk/edited.ref && [ ! -e mnt/bigtext ] && cmp -s mnt/etc/kernel.bin img/kernel.bin && ok "the edit is in the mounted volume" || bad "the edit is missing"
    umount mnt; ubk_read "edited.ubi: the volume 'rootfs_data' that was carried over" ubi$d:rootfs_data data.ref && ubk_write "the carried-over volume" ubi$d:rootfs_data
    umount mnt; head -c 300000 /dev/ubi${d}_0 | cmp -s - img/kernel.bin && ok "the static volume 'kernel' passes the checksum check of UBI and is unchanged" || bad "static volume 'kernel'"
  else bad "flash edited.ubi: $(tail -1 out/ubk.attach)"; fi; ubk_detach $UBK_NAND
  if ubk_flash $UBK_NAND ubk/edited_data.ubi 2048; then d=$(ubk_dev)
    ubk_read "edited_data.ubi: the small volume, replaced" ubi$d:rootfs_data ubk/edited_data.ref && ubk_write "the small replaced volume" ubi$d:rootfs_data
    umount mnt; ubk_read "edited_data.ubi: the volume 'rootfs' that was carried over" ubi$d:rootfs tree1k.ref
  else bad "flash edited_data.ubi: $(tail -1 out/ubk.attach)"; fi; ubk_detach $UBK_NAND
  if ubk_flash $UBK_NAND ubk/sq2ubifs.ubi 2048; then d=$(ubk_dev); ubk_read "a SquashFS volume converted to UBIFS in place" ubi$d:rootfs tree1k.ref
  else bad "flash sq2ubifs.ubi: $(tail -1 out/ubk.attach)"; fi; ubk_detach $UBK_NAND

  if ubk_flash $UBK_NAND ubk/sq.ubi 2048; then d=$(ubk_dev)
    if (modprobe ubiblock 2>/dev/null; modprobe squashfs 2>/dev/null; ubiblock --create /dev/ubi${d}_0) > out/ubk.blk 2>&1 && mount -t squashfs -o ro /dev/ubiblock${d}_0 mnt 2>> out/ubk.blk; then
      python3 scan.py mnt | cmp -s - tree1k.ref && ok "kernel mounts the SquashFS of sq.ubi through ubiblock" || bad "SquashFS in sq.ubi reads differently"
      umount mnt; ubiblock --remove /dev/ubi${d}_0 2>/dev/null
    else echo "  --    SquashFS in a UBI volume: not mounted ($(tail -1 out/ubk.blk))"
    fi
  else bad "flash sq.ubi: $(tail -1 out/ubk.attach)"; fi; ubk_detach $UBK_NAND

  before=$(dmesg | grep -ci "ubifs error\|ubifs assert\|ubi[0-9]* error")
  [ "$before" = 0 ] && ok "no UBI or UBIFS errors in the kernel log (self-checks were on)" || { bad "the kernel logged $before UBI/UBIFS error(s)"; dmesg | grep -i "ubifs error\|ubifs assert\|ubi[0-9]* error" | head -5 | cut -c1-220; }
}
