"""Managed Python regression tests for whole-source preparation; no model loading."""
import importlib.util
import pathlib
import tempfile
import unittest
import wave
import numpy as np
import av

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("recording_decode", ROOT / "src/AgentSmith.Tools/LocalAudio/recording_decode.py")
decoder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(decoder)

class PreparationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name)
    def tearDown(self):
        self.temp.cleanup()
    def wav(self, seconds=241, rate=48000, channels=2):
        path = self.root / "input.wav"
        with wave.open(str(path), "wb") as output:
            output.setparams((channels, 2, rate, 0, "NONE", "not compressed"))
            block = np.zeros((rate, channels), dtype="<i2")
            block[100:200] = 8000
            for _ in range(seconds): output.writeframes(block.tobytes())
        return path
    def prepare(self, path, **kwargs):
        return decoder.prepare(dict(inputs=[dict(path=str(path))], destination=str(self.root / "normalized.f32"), maxBytes=100000000, **kwargs))
    def test_full_source_not_first_120_seconds(self):
        path = self.wav()
        self.assertGreater(path.stat().st_size, 25*1024*1024)
        result = self.prepare(path)
        self.assertEqual(result["frames"], 241*16000)
        samples = np.fromfile(self.root / "normalized.f32", dtype="<f4")
        self.assertGreater(np.max(samples[-16000:]), .1)
    def test_mp3_delay_preserves_decoded_length(self):
        wav = self.wav(seconds=4)
        mp3 = self.root / "input.mp3"
        with av.open(str(wav)) as source, av.open(str(mp3), "w") as output:
            stream = output.add_stream("libmp3lame", rate=48000); stream.layout = "stereo"
            stream.codec_context.flags |= av.codec.context.Flags.qscale
            stream.codec_context.global_quality = 4 * 118
            for frame in source.decode(audio=0):
                for packet in stream.encode(frame): output.mux(packet)
            for packet in stream.encode(None): output.mux(packet)
        with av.open(str(mp3)) as encoded:
            self.assertGreater(len({packet.size for packet in encoded.demux(audio=0) if packet.size}), 1)
        result = self.prepare(mp3)
        self.assertEqual(result["frames"], 4*16000)
    def test_capture_gap_and_mapping(self):
        path = self.wav(seconds=1, rate=16000, channels=1)
        result = decoder.prepare(dict(inputs=[dict(path=str(path), start=0, frames=16000), dict(path=str(path), start=32000, frames=16000)], destination=str(self.root / "normalized.f32"), maxBytes=1000000))
        self.assertEqual(result["frames"], 48000)
        samples = np.fromfile(self.root / "normalized.f32", dtype="<f4")
        self.assertTrue(np.all(samples[16000:32000] == 0))
        self.assertEqual(result["mappings"][1]["start"], 32000)
    def test_budget_never_promotes_partial_output(self):
        path = self.wav(seconds=4)
        with self.assertRaisesRegex(ValueError, "budget"):
            decoder.prepare(dict(inputs=[dict(path=str(path))], destination=str(self.root / "normalized.f32"), maxBytes=1000))
        self.assertFalse((self.root / "normalized.f32").exists())
    def test_false_manifest_and_disguised_playlist_rejected(self):
        path = self.wav(seconds=1)
        with self.assertRaisesRegex(ValueError, "encoding"):
            decoder.prepare(dict(inputs=[dict(path=str(path), bitsPerSample=32, floatingPoint=True)], destination=str(self.root / "normalized.f32"), maxBytes=1000000))
        with self.assertRaisesRegex(ValueError, "manifest"):
            decoder.prepare(dict(inputs=[dict(path=str(path), frames=32000)], destination=str(self.root / "normalized.f32"), maxBytes=1000000))
        path.write_text("#EXTM3U\nhttps://example.com/audio.wav\n", encoding="utf-8")
        with self.assertRaises(Exception): self.prepare(path)

if __name__ == "__main__": unittest.main()
