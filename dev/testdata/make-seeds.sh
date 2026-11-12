#!/bin/bash
# Makes small, real media files (photos, videos, music) that the test data generator copies and varies.
# ffmpeg runs in a throwaway container from an image that is already on the server, with no network and
# only the output folder mounted.   Usage: dev/testdata/make-seeds.sh <out-dir>
set -euo pipefail
OUT=$(realpath -m "${1:?usage: $0 <out-dir>}")
IMG=${FFMPEG_IMAGE:-ghcr.io/immich-app/immich-server:v3.3.0}
mkdir -p "$OUT"
docker run --rm --network none --user "$(id -u):$(id -g)" -v "$OUT":/out -w /out --entrypoint sh "$IMG" -c '
set -e
f="ffmpeg -hide_banner -loglevel error -y"
src() { echo "testsrc2=size=$1:rate=$2:duration=$3"; }
tone="sine=frequency=440:sample_rate=44100:duration=8"
# photos: phone, camera, screenshot, scan, web
$f -f lavfi -i "$(src 4032x3024 1 1)" -frames:v 1 -q:v 3 photo-phone.jpg
$f -f lavfi -i "$(src 6000x4000 1 1)" -frames:v 1 -q:v 2 photo-camera.jpg
$f -f lavfi -i "$(src 3024x4032 1 1)" -frames:v 1 -q:v 4 photo-portrait.jpg
$f -f lavfi -i "$(src 1920x1080 1 1)" -frames:v 1 screenshot.png
$f -f lavfi -i "$(src 2480x3508 1 1)" -frames:v 1 -compression_algo lzw scan.tiff
$f -f lavfi -i "$(src 1200x800 1 1)" -frames:v 1 -quality 80 web.webp
$f -f lavfi -i "$(src 800x600 1 1)" -frames:v 1 old.bmp
$f -f lavfi -i "$(src 320x240 10 3)" -vf "split[a][b];[a]palettegen[p];[b][p]paletteuse" anim.gif
$f -f lavfi -i "$(src 1920x1080 1 1)" -frames:v 1 -c:v libx265 -tag:v hvc1 -f mp4 photo-iphone.heic.mp4
# videos: phone (h264/hevc), action cam, old camcorder, web, screen recording
$f -f lavfi -i "$(src 1920x1080 30 10)" -f lavfi -i "$tone" -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -shortest phone.mp4
$f -f lavfi -i "$(src 1920x1080 30 6)" -f lavfi -i "$tone" -c:v libx265 -preset veryfast -tag:v hvc1 -c:a aac -shortest iphone.mov
$f -f lavfi -i "$(src 1280x720 25 6)" -f lavfi -i "$tone" -c:v libvpx-vp9 -deadline realtime -c:a libopus -shortest web.webm
$f -f lavfi -i "$(src 1280x720 25 6)" -f lavfi -i "$tone" -c:v libx264 -preset veryfast -c:a libvorbis -shortest film.mkv
$f -f lavfi -i "$(src 640x480 25 6)" -f lavfi -i "$tone" -c:v mjpeg -q:v 5 -c:a pcm_s16le -shortest camcorder.avi
$f -f lavfi -i "$(src 720x576 25 6)" -c:v mpeg4 -q:v 5 old.3gp
# music
$f -f lavfi -i "$tone" -c:a libmp3lame -b:a 192k -metadata artist="Barış Manço" -metadata title="Dönence" song.mp3
$f -f lavfi -i "$tone" -c:a flac song.flac
$f -f lavfi -i "$tone" -c:a pcm_s16le song.wav
$f -f lavfi -i "$tone" -c:a libopus voice.ogg
$f -f lavfi -i "$tone" -c:a aac song.m4a
$f -f lavfi -i "$tone" -c:a alac apple.m4a
'
mv "$OUT/photo-iphone.heic.mp4" "$OUT/photo-iphone.heic"   # HEVC still in an ISO-BMFF box, like a phone photo
ls -la "$OUT"
