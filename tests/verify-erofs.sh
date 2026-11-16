# EROFS checks. Sourced by verify-linux.sh, which provides $F (the CLI), scan.py, mnt/, tree/ with its
# listing tree.ref, ok/bad, cmpdump and kmount; everything else lives in erofs/.
#
#   erofs_run    images made by mkfs.erofs are read by FsForge: uncompressed, lz4, lz4hc, lzma, deflate and
#                zstd; clusters of 4 KiB to 1 MiB; compact and full cluster indexes; tails next to the inode
#                (ztailpacking), fragments in the packed inode, deduplicated clusters; chunk-based files;
#                blocks of 1 to 4 KiB; short and long inodes; shared attributes and long attribute name
#                prefixes. Images written by FsForge are checked by fsck.erofs (which unpacks every file),
#                mounted by the kernel and compared entry by entry with the source tree; a disk image with
#                an EROFS root partition is edited in place.
#
# erofs-utils 1.7 is enough; with a newer mkfs.erofs more variants are made (zstd, -Eall-fragments).
# A second set of tools can be given in EROFS_UTILS (a folder with mkfs.erofs and fsck.erofs): its images
# are read as well, and its fsck.erofs checks what FsForge writes.

erofs_tools() { # the folders that hold mkfs.erofs: the system one, and $EROFS_UTILS
  command -v mkfs.erofs >/dev/null && dirname "$(command -v mkfs.erofs)"
  [ -n "${EROFS_UTILS:-}" ] && [ -x "$EROFS_UTILS/mkfs.erofs" ] && echo "$EROFS_UTILS"
}

erofs_mk() { # tools-dir tree|changed name mkfs-options... : an image made by mkfs.erofs, read by FsForge
  local dir=$1 kind=$2 n=$3; shift 3
  local v; v=$("$dir/fsck.erofs" -V 2>&1 | sed -n 's/^fsck.erofs[^0-9]*\([0-9][0-9.]*\).*/\1/p' | head -1)
  local id=m${v}_$n
  local img=erofs/img/$id.erofs ref=tree.ref
  rm -f $img
  # mkfs.erofs 1.7 fails on a name of 255 bytes: it is left out there.
  local skip=()
  case $v in 1.[0-7]|1.[0-7].*) skip=(--exclude-regex='^weird names/n{255}$'); grep -v '/n\{255\}	' tree.ref > erofs/out/tree.short.ref; ref=erofs/out/tree.short.ref;; esac
  if ! "$dir/mkfs.erofs" "$@" "${skip[@]}" $img tree > erofs/out/mk.$id.log 2>&1; then
    rm -f $img
    echo "  skip  mkfs.erofs $v $n ($(grep -i "invalid\|unknown\|unsupported\|unrecognized\|fail\|error" erofs/out/mk.$id.log | head -1 | cut -c1-90))"; return
  fi
  # What the kernel sees in the image is the reference. (mkfs.erofs 1.7 loses the holes of chunk-based files
  # and makes unreadable images with compressed blocks below 4 KiB; some options change owners and times.)
  if mount -o loop,ro $img mnt 2> erofs/out/km.$id.err; then
    if python3 scan.py mnt > erofs/out/ref.$id.txt 2> erofs/out/ks.$id.err; then umount mnt; ref=erofs/out/ref.$id.txt
    else umount mnt; rm -f $img erofs/out/ref.$id.txt; echo "  skip  mkfs.erofs $v $n (the kernel cannot read what this mkfs.erofs made)"; return; fi
  elif [ $kind = changed ]; then rm -f $img; echo "  skip  mkfs.erofs $v $n (the kernel does not mount it: $(head -c 80 erofs/out/km.$id.err))"; return
  else cp $ref erofs/out/ref.$id.txt; fi
  cmpdump "mkfs.erofs $v $n" $img $ref
}

EROFS_WRITTEN=""
erofs_wr() { # name source ref fsforge-options... : an image written by FsForge, checked by fsck.erofs and the kernel
  local n=$1 src=$2 ref=$3; shift 3
  local img=erofs/img/w_$n.erofs
  if ! $F convert $src $img format=erofs verify=content "$@" 2> erofs/out/w.$n.err; then bad "write erofs $n: $(cat erofs/out/w.$n.err)"; return; fi
  local r0=1 r1 dir
  for dir in $(erofs_tools); do
    # --extract makes fsck.erofs unpack (and so decompress) every file.
    rm -rf erofs/x; "$dir/fsck.erofs" --extract=erofs/x $img > erofs/out/fsck.$n.txt 2>&1 || r0=0
    [ $r0 = 1 ] && { python3 scan.py erofs/x | cut -f1,2,7 | grep -v "^/dev/sock\|^/	" > erofs/out/x.$n.txt
      cut -f1,2,7 $ref | grep -v "^/dev/sock\|^/	" | cmp -s - erofs/out/x.$n.txt || r0=0; }
    rm -rf erofs/x
  done
  local by="fsck.erofs"
  if [ "$EROFS_KERNEL" = 1 ]; then by="fsck.erofs + kernel"; kmount "erofs_$n" $img $ref && r1=1 || r1=0; else r1=1; fi
  EROFS_WRITTEN="$EROFS_WRITTEN $img"
  [ $r0 = 1 ] && [ $r1 = 1 ] && ok "write erofs $n ($(stat -c %s $img) bytes; $by)" || { bad "write erofs $n: fsck.erofs=$r0 kernel=$r1"; tail -3 erofs/out/fsck.$n.txt | cut -c1-200; }
}

erofs_run() {
  rm -rf erofs; mkdir -p erofs/img erofs/out
  EROFS_KERNEL=0; { grep -qw erofs /proc/filesystems || modprobe erofs 2>/dev/null; } && EROFS_KERNEL=1
  [ $EROFS_KERNEL = 1 ] || echo "  --    the kernel has no EROFS: images are compared with the source tree and checked by fsck.erofs only"
  local dir
  for dir in $(erofs_tools); do
    echo "EROFS: images made by $dir/mkfs.erofs"
    erofs_mk $dir tree plain
    erofs_mk $dir tree lz4 -zlz4
    erofs_mk $dir tree lz4hc -zlz4hc,12
    erofs_mk $dir tree lzma -zlzma
    erofs_mk $dir tree deflate -zdeflate
    erofs_mk $dir tree zstd -zzstd
    erofs_mk $dir tree lz4hc_64k -zlz4hc -C65536
    erofs_mk $dir tree lz4hc_1m -zlz4hc -C1048576
    erofs_mk $dir tree lzma_256k -zlzma -C262144
    erofs_mk $dir tree zstd_64k -zzstd -C65536
    erofs_mk $dir tree legacy -zlz4hc -Elegacy-compress
    erofs_mk $dir tree legacy_64k -zlz4hc -Elegacy-compress -C65536
    erofs_mk $dir tree ztail -zlz4hc -Eztailpacking
    erofs_mk $dir tree ztail_lzma_16k -zlzma -Eztailpacking -C16384
    erofs_mk $dir tree fragments -zlz4hc -Efragments
    erofs_mk $dir tree fragments_64k -zlz4hc -Efragments -C65536
    erofs_mk $dir tree allfragments -zlz4hc -Eall-fragments
    erofs_mk $dir tree dedupe -zlz4hc -Ededupe
    erofs_mk $dir tree dedupe_64k -zlz4hc -Ededupe -C65536
    erofs_mk $dir tree everything -zlzma -Eztailpacking,fragments,dedupe -C16384
    erofs_mk $dir tree fragdedupe -zlz4hc -Efragments,dedupe,ztailpacking -C65536
    erofs_mk $dir tree chunks_4k --chunksize=4096
    erofs_mk $dir tree chunks_1m --chunksize=1048576 -Eforce-chunk-indexes
    erofs_mk $dir tree bs1k -b1024 -zlz4hc
    erofs_mk $dir tree bs2k -b2048 -zlzma
    erofs_mk $dir tree bs512 -b512
    erofs_mk $dir changed compact -zlz4 -Eforce-inode-compact --force-uid=1000 --force-gid=1000 -T1700000000
    erofs_mk $dir tree extended -zlz4 -Eforce-inode-extended
    erofs_mk $dir tree noinline -zlz4hc -Enoinline_data
    erofs_mk $dir tree sharedxattr -zlz4 -x1
    erofs_mk $dir tree xattrprefix -zlz4 --xattr-prefix=security.selinux --xattr-prefix=user.
    erofs_mk $dir changed onetime -zlz4hc -T1700000000
    erofs_mk $dir changed allroot -zlz4hc --all-root
    erofs_mk $dir changed noxattr -zlz4hc -x-1
    printf '%s\n' '4096 1 bin/.*' '65536 0 f[0-9]*' > erofs/hints
    erofs_mk $dir tree twoalgs -zlz4hc:lzma -C65536 --compress-hints=erofs/hints
  done

  echo "EROFS: images written by FsForge"
  local first; first=$(ls erofs/img/m*_plain.erofs 2>/dev/null | tail -1)
  [ -n "$first" ] || { bad "no mkfs.erofs image to start from"; return; }
  local ref=erofs/out/ref.$(basename $first .erofs).txt
  erofs_wr none $first $ref comp=none
  erofs_wr lz4 $first $ref comp=lz4
  erofs_wr lz4hc $first $ref comp=lz4hc
  erofs_wr lz4hc_l12 $first $ref comp=lz4hc level=12
  erofs_wr lzma $first $ref comp=lzma
  erofs_wr lzma_fast $first $ref comp=lzma effort=0
  erofs_wr lz4hc_16k $first $ref comp=lz4hc pcluster=16384
  erofs_wr lz4hc_1m $first $ref comp=lz4hc pcluster=1048576
  erofs_wr lzma_64k $first $ref comp=lzma pcluster=65536
  erofs_wr lzma_1m $first $ref comp=lzma pcluster=1048576 effort=0
  erofs_wr noinline $first $ref comp=lz4hc inline=0
  erofs_wr nodedup $first $ref comp=lz4hc dedup=0
  erofs_wr noshare $first $ref comp=lz4 sharexattrs=0
  erofs_wr label $first $ref comp=lz4 label=rootfs uuid=01234567-89ab-cdef-0123-456789abcdef time=1700000000
  [ "$(blkid -o value -s LABEL erofs/img/w_label.erofs 2>/dev/null)" = rootfs ] && [ "$(blkid -o value -s UUID erofs/img/w_label.erofs 2>/dev/null)" = 01234567-89ab-cdef-0123-456789abcdef ] \
    && ok "blkid reads the label and the UUID of the written image" || bad "blkid: $(blkid erofs/img/w_label.erofs 2>&1)"
  # From other formats, and images saved again with the settings they have.
  erofs_wr from_squashfs img/xz.sqfs tree.ref comp=lzma pcluster=131072
  erofs_wr same_lzma erofs/img/w_lzma_64k.erofs $ref
  $F dump erofs/img/w_same_lzma.erofs 2>&1 >/dev/null | grep -q "comp=Lzma pcluster=65536" && ok "an EROFS image is saved again with its compression and cluster size" \
    || bad "settings were not kept: $($F dump erofs/img/w_same_lzma.erofs 2>&1 >/dev/null | tail -1)"
  local m; for m in erofs/img/m*_zstd.erofs erofs/img/m*_everything.erofs erofs/img/m*_bs1k.erofs erofs/img/m*_chunks_4k.erofs; do
    [ -f $m ] && erofs_wr "same_$(basename $m .erofs)" $m erofs/out/ref.$(basename $m .erofs).txt; done

  # The other way round: what FsForge wrote, converted to the other formats, still is the same tree.
  if $F convert erofs/img/w_lzma.erofs erofs/img/back.sqfs comp=xz verify=content 2> erofs/out/back.err && kmount erofs_back erofs/img/back.sqfs $ref; then ok "EROFS written by FsForge converts to SquashFS"; else bad "EROFS to SquashFS: $(cat erofs/out/back.err)"; fi

  # A disk image whose root partition is EROFS: the partition is replaced, everything else stays.
  [ $EROFS_KERNEL = 1 ] || return 0
  python3 - erofs/img/w_lz4hc.erofs erofs/img/disk.img <<'PY'
import struct, sys
fs = open(sys.argv[1], 'rb').read()
start, slot = 2048 * 512, (len(fs) + (8 << 20) + 511) // 512 * 512
mbr = bytearray(512)
mbr[446:462] = struct.pack('<B3xB3xII', 0, 0x83, start // 512, slot // 512)
mbr[510:512] = b'\x55\xaa'
with open(sys.argv[2], 'wb') as f:
    f.write(mbr); f.write(b'\xA5' * (start - 512)); f.write(fs); f.truncate(start + slot)
PY
  if $F edit erofs/img/disk.img erofs/img/w_disk.img add:/etc:scan.py rm:/bin/cat verify=content 2> erofs/out/disk.err; then
    rm -rf erofs/t; cp -a tree erofs/t; cp scan.py erofs/t/etc/; rm erofs/t/bin/cat
    touch -d "@$(stat -c %Y scan.py)" erofs/t/etc/scan.py
    cmp -s <(head -c $((2048*512)) erofs/img/disk.img) <(head -c $((2048*512)) erofs/img/w_disk.img) && r0=1 || r0=0
    mount -o loop,ro,offset=$((2048*512)) erofs/img/w_disk.img mnt 2> erofs/out/dm.err && { python3 scan.py mnt | grep -v "^/etc	\|^/bin	\|^/etc/scan.py	" > erofs/out/disk.txt; umount mnt; }
    python3 scan.py erofs/t | grep -v "^/etc	\|^/bin	\|^/etc/scan.py	" > erofs/out/disk.ref
    cmp -s erofs/out/disk.txt erofs/out/disk.ref && r1=1 || r1=0
    [ $r0 = 1 ] && [ $r1 = 1 ] && ok "disk image: the EROFS root partition is edited in place, the kernel mounts it" || { bad "disk image with EROFS: surroundings=$r0 content=$r1"; diff erofs/out/disk.txt erofs/out/disk.ref | head -4 | cut -c1-160; }
    rm -rf erofs/t
  else bad "edit a disk image with an EROFS partition: $(cat erofs/out/disk.err)"; fi
}
