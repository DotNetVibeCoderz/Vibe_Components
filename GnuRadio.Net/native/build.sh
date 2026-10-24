#!/usr/bin/env sh
# Builds the optional Rust kernels and stages them in runtimes/<rid>/native.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
crate="$here/gnuradio-native"
os="$(uname -s)"
arch="$(uname -m)"
case "$arch" in x86_64|amd64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; esac
case "$os" in
  Darwin) rid="osx-$arch"; lib="libgnuradio_native.dylib" ;;
  *) rid="linux-$arch"; lib="libgnuradio_native.so" ;;
esac
(cd "$crate" && cargo build --release)
mkdir -p "$here/../runtimes/$rid/native"
cp "$crate/target/release/$lib" "$here/../runtimes/$rid/native/"
echo "Staged $lib in runtimes/$rid/native"
