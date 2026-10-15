"""Run with the managed Python: python -I tests/local_audio_test.py. No model downloads."""
import importlib.util
import pathlib
import tempfile
import unittest
from unittest import mock
from types import SimpleNamespace
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

    def test_whisper_models_are_reused_and_word_times_are_source_relative(self):
        helper.MODELS.clear()
        word = SimpleNamespace(start=.2, end=.8, word=" Hello")
        segment = SimpleNamespace(start=.2, end=.8, text="Hello", words=[word])
        model = mock.Mock()
        model.transcribe.side_effect = lambda *a, **kw: (iter([segment]), SimpleNamespace(language="en", language_probability=.99))
        with mock.patch("faster_whisper.WhisperModel", return_value=model) as factory:
            first = helper.transcribe(np.ones(16000, dtype=np.float32)*.1, self.root, 10, "auto")
            helper.transcribe(np.ones(16000, dtype=np.float32)*.1, self.root, 0, "auto")
            self.assertEqual(factory.call_count, 1)
            self.assertAlmostEqual(first["segments"][0]["words"][0]["startSeconds"], 10.2)
            self.assertTrue(model.transcribe.call_args.kwargs["word_timestamps"])
        helper.MODELS.clear()

    def test_speaker_labels_follow_first_occurrence_and_models_are_reused(self):
        helper.MODELS.clear()
        model = mock.Mock()
        model.process.return_value.sort_by_start_time.return_value = [
            SimpleNamespace(start=0, end=.3, speaker=17), SimpleNamespace(start=.3, end=.6, speaker=2),
            SimpleNamespace(start=.6, end=1, speaker=17)]
        with mock.patch("sherpa_onnx.OfflineSpeakerDiarizationConfig") as config, mock.patch("sherpa_onnx.OfflineSpeakerDiarization", return_value=model) as factory:
            config.return_value.validate.return_value = True
            first = helper.diarize(np.ones(16000, dtype=np.float32)*.1, self.root, 10)
            helper.diarize(np.ones(16000, dtype=np.float32)*.1, self.root, 0)
            self.assertEqual(factory.call_count, 1)
            self.assertEqual(first["speakerCount"], 2)
            self.assertEqual([t["speaker"] for t in first["turns"]], ["Speaker 1", "Speaker 2", "Speaker 1"])
            self.assertAlmostEqual(first["turns"][1]["startSeconds"], 10.3)
        helper.MODELS.clear()

if __name__ == "__main__":
    unittest.main()
