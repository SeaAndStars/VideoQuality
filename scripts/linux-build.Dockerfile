# Build environment for the Linux release's FFmpeg: an old glibc (2.35) so the bundled binaries run
# on Ubuntu 22.04+, Debian 12+ and other current distros. Used by scripts/build-ffmpeg-linux-docker.sh.
FROM ubuntu:22.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        build-essential git nasm pkg-config clang python3-pip ninja-build patchelf \
        wget ca-certificates zlib1g-dev xxd \
 && pip3 install --no-cache-dir meson==1.5.2 \
 && wget -q https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2204/x86_64/cuda-keyring_1.1-1_all.deb \
 && dpkg -i cuda-keyring_1.1-1_all.deb && rm cuda-keyring_1.1-1_all.deb \
 && apt-get update \
 && apt-get install -y --no-install-recommends cuda-nvcc-12-8 cuda-cudart-dev-12-8 \
 && rm -rf /var/lib/apt/lists/*
ENV CUDA_HOME=/usr/local/cuda-12.8 PATH=/usr/local/cuda-12.8/bin:$PATH
