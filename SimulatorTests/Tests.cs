// Simulated-camera tests for SonyCamera/SonyImage (and, via DriverTests, the ASCOM Camera class).
// Run with run.sh - see README.md.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ASCOM.DeviceInterface;

namespace ASCOM.SonyMirrorless
{
    class Tests : SonyCommon
    {
        [DllImport("SonyMTPCamera.dll")] static extern void Fake_Configure(int w, int h, int flags);
        [DllImport("SonyMTPCamera.dll")] static extern void Fake_Stats(out int closes, out UInt32 lastClosed, out UInt32 iso);

        internal static int pass = 0, fail = 0;
        internal static void Check(string name, bool ok, string detail = "") { Console.WriteLine("{0} {1} {2}", ok ? "PASS" : "FAIL", name, detail); if (ok) pass++; else fail++; }

        static SonyCamera NewCamera(int w, int h, int flags)
        {
            Fake_Configure(w, h, 0);
            SonyCamera cam = (SonyCamera)new SonyCameraEnumerator().Cameras[0];
            Fake_Configure(w, h, flags); // failure modes only apply after enumeration
            return cam;
        }

        static string Run(Action a) { try { a(); return "ok"; } catch (Exception e) { return e.GetType().Name + ": " + e.Message; } }

        // Polls like a client would; gives up after 50 polls
        static SonyImage.ImageStatus WaitForImage(SonyCamera cam, out string err)
        {
            err = null;
            for (int i = 0; i < 50; i++)
            {
                try
                {
                    SonyImage.ImageStatus s = cam.LastImage.Status;
                    if (s != SonyImage.ImageStatus.Capturing && s != SonyImage.ImageStatus.Reading) return s;
                }
                catch (Exception e) { err = e.GetType().Name + ": " + e.Message; return cam.LastImage.m_status; }
            }
            return cam.LastImage.m_status;
        }

        static long Hash(Array a) { long h = 17; foreach (int v in a) h = h * 31 + v; return h; }

        static long Rss() { foreach (string l in File.ReadAllLines("/proc/self/status")) if (l.StartsWith("VmRSS")) return long.Parse(Regex.Match(l, @"\d+").Value) / 1024; return -1; }

        static void Main(string[] args)
        {
            string err;

            // --- Functional: image conversion (hashes compared between builds by the caller) ---
            // Sizes chosen to cover a height that isn't a multiple of the conversion band, and a 1-row image
            foreach (int[] dims in new[] { new[] { 600, 400 }, new[] { 601, 403 }, new[] { 7, 1 } })
            foreach (int personality in new[] { PERSONALITY_APT, PERSONALITY_NINA, PERSONALITY_SHARPCAP })
            foreach (short readout in new[] { OUTPUTFORMAT_RGGB, OUTPUTFORMAT_RGB, OUTPUTFORMAT_BGR })
            {
                SonyCamera cam = NewCamera(dims[0], dims[1], 0);
                cam.Connected = true;
                cam.OutputMode = readout == OUTPUTFORMAT_RGGB ? SonyCamera.ImageMode.RGGB : SonyCamera.ImageMode.RGB;
                cam.StartCapture(1.0, personality, readout);
                SonyImage.ImageStatus s = WaitForImage(cam, out err);
                bool mono = readout == OUTPUTFORMAT_RGGB || personality == PERSONALITY_NINA;
                Array a = mono ? (Array)SonyImage.BAYER : SonyImage.RGB;
                int min = int.MaxValue; foreach (int v in a) min = Math.Min(min, v);
                Console.WriteLine("HASH {5}x{6} p{0} r{1}: status={2} hash={3} min={4}", personality, readout, s, Hash(a), min, dims[0], dims[1]);
                cam.Connected = false;
            }

            // NINA mono conversion must never produce negative values (was cast to short)
            {
                SonyCamera cam = NewCamera(600, 400, 0); cam.Connected = true; cam.OutputMode = SonyCamera.ImageMode.RGB;
                cam.StartCapture(1.0, PERSONALITY_NINA, OUTPUTFORMAT_RGB); WaitForImage(cam, out err);
                int min = int.MaxValue; foreach (int v in SonyImage.BAYER) min = Math.Min(min, v);
                Check("NINA mono has no negative pixels", min >= 0, "min=" + min);
            }

            // NINA mono must equal the luminance formula computed independently from the fake's pixel pattern
            {
                int w = 601, h = 403;
                SonyCamera cam = NewCamera(w, h, 0); cam.Connected = true; cam.OutputMode = SonyCamera.ImageMode.RGB;
                cam.StartCapture(1.0, PERSONALITY_NINA, OUTPUTFORMAT_RGB); WaitForImage(cam, out err);
                int mismatches = 0;
                for (int row = 0; row < h; row++)
                for (int x = 0; x < w; x++)
                {
                    long word = ((long)row * w + x) * 3;
                    Func<long, int> f = k => (int)(((uint)(k * 2654435761u)) >> 13) & 0xffff;
                    int expected = Math.Min(65535, (int)((0.2125 * f(word)) + (0.7154 * f(word + 1)) + (0.0721 * f(word + 2))));
                    if (SonyImage.BAYER[x, row] != expected) mismatches++;
                }
                Check("NINA mono matches reference luminance (orientation unchanged)", mismatches == 0, mismatches + " mismatching pixels");
            }

            // --- Functional: connect / disconnect ---
            {
                SonyCamera cam = NewCamera(600, 400, F_FAIL_OPEN);
                string r = Run(() => cam.Connected = true);
                Check("OpenDevice failure throws on connect", r != "ok", r);
            }
            {
                SonyCamera cam = NewCamera(600, 400, F_FAIL_INFO);
                string r = Run(() => cam.Connected = true);
                int closes; UInt32 last, iso; Fake_Stats(out closes, out last, out iso);
                Check("GetCameraInfo failure throws and closes handle", r != "ok" && !cam.Connected && closes == 1, r + " closes=" + closes);
            }
            {
                SonyCamera cam = NewCamera(600, 400, 0);
                cam.Connected = false; // never connected
                int closes; UInt32 last, iso; Fake_Stats(out closes, out last, out iso);
                Check("Disconnect without connect doesn't call CloseDevice", closes == 0, "closes=" + closes + " lastClosed=0x" + last.ToString("x"));
            }
            {
                SonyCamera cam = NewCamera(600, 400, 0);
                cam.Connected = true; cam.Connected = false;
                int closes; UInt32 last, iso; Fake_Stats(out closes, out last, out iso);
                Check("Connect then disconnect closes the real handle", closes == 1 && last == 5, "closes=" + closes + " lastClosed=" + last);
            }

            // --- Stability: failure paths must not hang ---
            {
                SonyCamera cam = NewCamera(600, 400, F_FAIL_START); cam.Connected = true;
                string r = Run(() => cam.StartCapture(1.0, PERSONALITY_APT, OUTPUTFORMAT_RGGB));
                SonyImage.ImageStatus s = r == "ok" ? WaitForImage(cam, out err) : SonyImage.ImageStatus.Failed;
                Check("StartCapture failure is reported (not polled forever)", r != "ok" || s == SonyImage.ImageStatus.Failed, r + " status=" + s + " state=" + Run(() => { var x = cam.State; }) );
            }
            {
                SonyCamera cam = NewCamera(600, 400, F_TRUNCATE); cam.Connected = true;
                cam.StartCapture(1.0, PERSONALITY_APT, OUTPUTFORMAT_RGGB);
                SonyImage.ImageStatus s = WaitForImage(cam, out err);
                CameraStates st = CameraStates.cameraIdle; string r2 = Run(() => st = cam.State);
                Check("Truncated image data ends in Failed/cameraError, not stuck", s == SonyImage.ImageStatus.Failed && st == CameraStates.cameraError, "status=" + s + " state=" + st + " pollError=" + err);
            }
            {
                SonyCamera cam = NewCamera(600, 400, F_FAIL_PREVIEW); cam.Connected = true; cam.PreviewMode = true;
                string r = Run(() => cam.StartCapture(0, PERSONALITY_APT, OUTPUTFORMAT_RGB));
                SonyImage.ImageStatus s = r == "ok" ? WaitForImage(cam, out err) : SonyImage.ImageStatus.Failed;
                Check("Liveview failure is reported (not polled forever)", r != "ok" || s == SonyImage.ImageStatus.Failed, r + " status=" + s);
            }
            {
                SonyCamera cam = NewCamera(600, 400, 0); cam.Connected = true;
                cam.StartCapture(1.0, PERSONALITY_APT, OUTPUTFORMAT_RGGB); cam.StopCapture();
                SonyImage.ImageStatus s = WaitForImage(cam, out err);
                Check("Abort mid-exposure doesn't hang", s != SonyImage.ImageStatus.Capturing, "status=" + s);
            }

            // --- Functional: gain ---
            {
                SonyCamera cam = NewCamera(600, 400, F_NO_ISO); cam.Connected = true;
                string r = Run(() => { var g = cam.Gains; });
                Check("Camera without ISO list: Gains doesn't crash", r == "ok", r);
            }
            {
                SonyCamera cam = NewCamera(600, 400, 0); cam.Connected = true;
                cam.GainIndex = 1;
                short g = cam.GainIndex;
                Check("Gain get after set returns the set value", g == 1, "got " + g);
                cam.StartCapture(1.0, PERSONALITY_APT, OUTPUTFORMAT_RGGB); WaitForImage(cam, out err);
                int closes; UInt32 last, iso; Fake_Stats(out closes, out last, out iso);
                Check("Gain is applied to camera at exposure", iso == 200, "iso=" + iso);
                string r = Run(() => cam.GainIndex = -3);
                Check("Invalid gain is rejected", r != "ok", r);
            }

            // --- Performance: conversion time ---
            foreach (int[] dims in new[] { new[] { 6000, 4000 }, new[] { 9504, 6336 } })
            foreach (short readout in new[] { OUTPUTFORMAT_RGGB, OUTPUTFORMAT_RGB })
            {
                SonyCamera cam = NewCamera(dims[0], dims[1], 0); cam.Connected = true;
                cam.OutputMode = readout == OUTPUTFORMAT_RGGB ? SonyCamera.ImageMode.RGGB : SonyCamera.ImageMode.RGB;
                double best = double.MaxValue;
                for (int i = 0; i < 3; i++)
                {
                    cam.StartCapture(1.0, PERSONALITY_APT, readout);
                    for (int p = 0; p < 3; p++) { var x = cam.LastImage.Status; } // exposing polls
                    Stopwatch sw = Stopwatch.StartNew();
                    var s = cam.LastImage.Status; // conversion happens here
                    best = Math.Min(best, sw.Elapsed.TotalSeconds);
                }
                Console.WriteLine("PERF {0}MP {1}: {2:F2}s", dims[0] * dims[1] / 1000000, readout == OUTPUTFORMAT_RGGB ? "RAW" : "RGB", best);
                cam.Connected = false;
            }

            // --- Stability: long capture loop, memory must stay flat ---
            {
                SonyCamera cam = NewCamera(6000, 4000, 0); cam.Connected = true;
                long start = 0;
                for (int i = 0; i < 300; i++)
                {
                    cam.StartCapture(0.1, PERSONALITY_APT, OUTPUTFORMAT_RGGB);
                    SonyImage.ImageStatus s = WaitForImage(cam, out err);
                    if (s != SonyImage.ImageStatus.Ready) { Check("Loop capture " + i, false, s.ToString()); break; }
                    if (i == 20) { GC.Collect(); start = Rss(); }
                }
                GC.Collect();
                long end = Rss();
                Check("300x 24MP captures: RSS stable", end - start < 100, string.Format("RSS after 20: {0}MB, after 300: {1}MB", start, end));
            }

            DriverTests.Run();

            Console.WriteLine("SUMMARY pass={0} fail={1}", pass, fail);
            Environment.Exit(fail > 0 ? 1 : 0);
        }

        const int F_FAIL_OPEN = 1, F_FAIL_START = 2, F_TRUNCATE = 4, F_FAIL_PREVIEW = 8, F_NO_ISO = 16, F_FAIL_INFO = 32;
    }
}
