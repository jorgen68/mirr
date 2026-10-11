import struct
import sys

import av
import numpy as np


def read_exact(stream, count):
    buffer = b""
    while len(buffer) < count:
        chunk = stream.read(count - len(buffer))
        if not chunk:
            return None
        buffer += chunk
    return buffer


def resize_nearest(source, out_height, out_width):
    ys = np.arange(out_height) * source.shape[0] // out_height
    xs = np.arange(out_width) * source.shape[1] // out_width
    return source[ys][:, xs]


class Bridge:
    def __init__(self, family):
        self.codec = av.CodecContext.create(family, "r")
        self.crop_width = 0
        self.crop_height = 0
        self.detection_countdown = 0

    def feed(self, annexb, stdout):
        try:
            packets = self.codec.parse(annexb)
        except Exception as error:
            print(f"parse error: {error}", file=sys.stderr, flush=True)
            return
        for packet in packets:
            try:
                frames = self.codec.decode(packet)
            except Exception as error:
                print(f"decode error: {error}", file=sys.stderr, flush=True)
                continue
            for frame in frames:
                self.emit(frame, stdout)

    def emit(self, frame, stdout):
        converted = frame.reformat(format="bgra")
        width, height = converted.width, converted.height
        payload = bytes(converted.planes[0])

        self.detection_countdown -= 1
        if self.detection_countdown <= 0:
            self.detection_countdown = 10
            self.detect(payload, width, height)

        if self.crop_width and self.crop_height:
            pixels = np.frombuffer(payload, dtype=np.uint8).reshape(height, width, 4)
            content = pixels[: self.crop_height, : self.crop_width]
            payload = np.ascontiguousarray(resize_nearest(content, height, width)).tobytes()

        stdout.write(struct.pack(">IIII", 12 + len(payload), width, height, width * 4))
        stdout.write(payload)
        stdout.flush()

    def detect(self, payload, width, height):
        if height < 64 or width < 64:
            self.crop_width = 0
            self.crop_height = 0
            return
        pixels = np.frombuffer(payload, dtype=np.uint8).reshape(height, width, 4)
        sample = pixels[::4, ::4, :3].astype(np.int16)
        gray = np.abs(sample - 128).max(axis=2) <= 12
        row_gray = gray.all(axis=1)
        column_gray = gray.all(axis=0)

        bottom = height - 1
        while bottom > height // 4:
            row = bottom // 4
            if row >= row_gray.shape[0] or not row_gray[row]:
                break
            bottom -= 1
        if bottom == height - 1:
            self.crop_width = 0
            self.crop_height = 0
            return

        right = width - 1
        while right > width // 4:
            column = right // 4
            if column >= column_gray.shape[0] or not column_gray[column]:
                break
            right -= 1
        if right == width - 1 or bottom < 64 or right < 64:
            self.crop_width = 0
            self.crop_height = 0
            return

        self.crop_width = right + 1
        self.crop_height = bottom + 1


def main():
    family = "hevc"
    arguments = sys.argv[1:]
    if "--codec" in arguments:
        family = arguments[arguments.index("--codec") + 1]

    stdin = sys.stdin.buffer
    stdout = sys.stdout.buffer
    bridge = Bridge(family)
    print(f"bridge ready (codec={family})", file=sys.stderr, flush=True)

    while True:
        header = read_exact(stdin, 4)
        if header is None:
            break
        length = struct.unpack(">I", header)[0]
        payload = read_exact(stdin, length)
        if payload is None:
            break
        bridge.feed(payload[1:], stdout)

    print("bridge input closed", file=sys.stderr, flush=True)


main()
