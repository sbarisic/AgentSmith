"""Run with the managed Python: python -I tests/local_audio_test.py. No model downloads."""
import importlib.util
import pathlib
import tempfile
import unittest
import wave
import socket
import numpy as np
import av

ROOT = pathlib.Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("audio_helper", ROOT / "src/AgentSmith.Tools/LocalAudio/audio_helper.py")
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)

class AudioDecodingTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.directory.name)

    def tearDown(self):
        self.directory.cleanup()

    def wav(self, name="tone.wav", rate=48000, seconds=3, channels=2):
        path = self.root / name
        samples = (np.sin(np.arange(rate*seconds)*440*2*np.pi/rate)*12000).astype("<i2")
        with wave.open(str(path), "wb") as out:
            out.setnchannels(channels); out.setsampwidth(2); out.setframerate(rate)
            out.writeframes(np.repeat(samples[:,None], channels, axis=1).tobytes())
        return path

    def mp3(self):
        source = self.wav()
        target = self.root / "tone.mp3"
        with av.open(str(source)) as inp, av.open(str(target), "w") as out:
            stream = out.add_stream("libmp3lame", rate=48000)
            stream.layout = "stereo"
            for frame in inp.decode(audio=0):
                for packet in stream.encode(frame): out.mux(packet)
            for packet in stream.encode(None): out.mux(packet)
        return target

    def test_wav_downmix_resample_range_and_hash(self):
        data, info = helper.decode(str(self.wav()), 1, 1)
        self.assertEqual(data.shape, (16000,))
        self.assertEqual(info["startSeconds"], 1)
        self.assertAlmostEqual(info["durationSeconds"], 1)
        self.assertTrue(info["partial"])
        self.assertEqual(len(info["clipHash"]), 64)
        self.assertGreater(float(np.max(data)), .3)
        self.assertLessEqual(float(np.max(data)), 1)

    def test_mp3_has_same_range_semantics(self):
        path = self.mp3()
        whole, _ = helper.decode(str(path), 0, 0)
        selected, info = helper.decode(str(path), 1, 1)
        self.assertEqual(len(selected), 16000)
        self.assertAlmostEqual(info["durationSeconds"], 1)
        np.testing.assert_allclose(whole[16000:32000], selected, atol=.002)

    def test_zero_duration_caps_both_formats(self):
        data, info = helper.decode(str(self.wav(seconds=121, rate=8000, channels=1)), 0, 0)
        self.assertEqual(len(data), 120*16000)
        self.assertTrue(info["partial"])

    def test_invalid_empty_and_out_of_range(self):
        bad = self.root / "bad.wav"; bad.write_bytes(b"not an audio file")
        with self.assertRaises(Exception): helper.decode(str(bad), 0, 0)
        path = str(self.wav())
        for start,duration in [(-1,1),(0,121),(3,1),(999999,0)]:
            with self.assertRaises(ValueError): helper.decode(path,start,duration)

    def test_network_is_blocked_in_helper(self):
        with self.assertRaisesRegex(RuntimeError,"Network access is disabled"):
            socket.create_connection(("example.com",443))

    def test_missing_models_produce_explicit_component_failures(self):
        result = helper.run(dict(protocol=1,operation="selftest",models=str(self.root)))
        self.assertEqual(result["transcription"]["status"], "failed")
        self.assertEqual(result["classification"]["status"], "failed")

if __name__ == "__main__":
    unittest.main()
