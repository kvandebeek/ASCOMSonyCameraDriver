using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using ASCOM.DeviceInterface;
using ASCOM.Utilities;

namespace ASCOM.SonyMirrorless
{
    // Exercises the ASCOM-facing Camera class against the fake DLL
    class DriverTests
    {
        [DllImport("SonyMTPCamera.dll")] static extern void Fake_Configure(int w, int h, int flags);
        [DllImport("SonyMTPCamera.dll")] static extern void Fake_Counters(out int infoActive, out int cancels, out int starts);

        const int F_BAD_OPTION = 64;

        static void Check(string name, bool ok, string detail = "") { Tests.Check(name, ok, detail); }
        static string Run(Action a) { try { a(); return "ok"; } catch (Exception e) { return e.GetType().Name + ": " + e.Message; } }

        static Camera NewDriver(int w, int h, string readoutMode, string personality, bool autoLiveview, bool iso)
        {
            Fake_Configure(w, h, 0);
            Profile.Values.Clear();
            Profile.Values["Camera ID"] = "ILCE-7M3 (s/n: 12345)";
            Profile.Values["Readout Mode"] = readoutMode;
            Profile.Values["Personality"] = personality;
            Profile.Values["Auto Liveview"] = autoLiveview.ToString();
            Profile.Values["Use Camera Liveview"] = "true";
            Profile.Values["Allow ISO Adjust"] = iso.ToString();
            Camera.LastSetFastReadout = false;
            return new Camera();
        }

        static bool WaitReady(Camera cam)
        {
            for (int i = 0; i < 100; i++) if (cam.ImageReady) return true;
            return false;
        }

        static string Dims(object a) { Array arr = (Array)a; string s = arr.GetLength(0) + "x" + arr.GetLength(1); if (arr.Rank == 3) s += "x" + arr.GetLength(2); return s; }

        public static void Run()
        {
            // --- Basic ASCOM behaviour ---
            {
                Camera cam = NewDriver(600, 400, "1", "1", false, false);
                cam.Connected = true;
                Check("Driver: connects", cam.Connected);

                string r = Run(() => { var x = cam.ImageArray; });
                Check("Driver: ImageArray before first image -> InvalidOperationException", r.StartsWith("InvalidOperationException"), r);
                r = Run(() => { var x = cam.LastExposureStartTime; });
                Check("Driver: LastExposureStartTime before first image -> InvalidOperationException", r.StartsWith("InvalidOperationException"), r);

                // Restarting mid-exposure is allowed - the DLL cleans up the previous capture itself
                cam.StartExposure(1.0, true);
                r = Run(() => cam.StartExposure(1.0, true));
                int info, cancels, starts;
                Check("Driver: StartExposure while exposing restarts cleanly", r == "ok" && WaitReady(cam), r);

                WaitReady(cam);

                // Timestamp must be UTC and use ':' even where the culture's time separator isn't ':'
                CultureInfo old = Thread.CurrentThread.CurrentCulture;
                CultureInfo dotTime = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                dotTime.DateTimeFormat.TimeSeparator = ".";
                Thread.CurrentThread.CurrentCulture = dotTime;
                Camera.LastSetFastReadout = false;
                cam.StartExposure(1.0, true);
                WaitReady(cam);
                string ts = cam.LastExposureStartTime;
                Thread.CurrentThread.CurrentCulture = old;
                DateTime parsed;
                bool parses = DateTime.TryParseExact(ts, "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed);
                double offsetMin = parses ? (DateTime.UtcNow - parsed).TotalMinutes : double.NaN;
                Check("Driver: LastExposureStartTime is FITS format", parses, ts);
                Check("Driver: LastExposureStartTime is UTC", parses && Math.Abs(offsetMin) < 1, String.Format("{0} vs UTC now (diff {1:F0} min; local offset {2})", ts, offsetMin, TimeZoneInfo.Local.BaseUtcOffset));

                Check("Driver: ImageArray matches NumX x NumY", Dims(cam.ImageArray) == cam.NumX + "x" + cam.NumY, Dims(cam.ImageArray));
                Check("Driver: ImageArrayVariant has same shape as ImageArray", Dims(cam.ImageArrayVariant) == Dims(cam.ImageArray), Dims(cam.ImageArrayVariant));

                cam.StartX = 10;
                r = Run(() => { var x = cam.ImageArray; });
                Check("Driver: subframe outside image -> InvalidValueException", r.StartsWith("InvalidValueException"), r);
                cam.StartX = 0;

                // ReadoutMode get/set and liveview actually being used
                cam.ReadoutMode = 1;
                short rm = cam.ReadoutMode;
                Check("Driver: ReadoutMode get returns what was set (1)", rm == 1, "got " + rm);
                cam.StartExposure(1.0, true);
                WaitReady(cam);
                // Check the captured frame itself - ImageArray is cropped to NumX, and the fake's liveview is wider than its sensor
                int width = Camera.camera.LastImage.Width;
                Check("Driver: ReadoutMode=LiveView produces a liveview frame", width == 1024, "captured frame width " + width);
                cam.ReadoutMode = 0;
                rm = cam.ReadoutMode;
                Check("Driver: ReadoutMode get returns what was set (0)", rm == 0, "got " + rm);

                Fake_Configure(600, 400, 0); // reset counters
                for (int i = 0; i < 5; i++) { var m = cam.ReadoutModes; }
                Fake_Counters(out info, out cancels, out starts);
                Check("Driver: ReadoutModes doesn't re-query the camera every call", info <= 1, "GetCameraInfo(ACTIVE) calls for 5x ReadoutModes: " + info);

                Check("Driver: MaxADU for RAW is sensor depth", cam.MaxADU == 16383, cam.MaxADU.ToString());

                // Disconnecting mid-exposure should cancel the capture first
                cam.StartExposure(1.0, true);
                Fake_Counters(out info, out cancels, out starts);
                int before = cancels;
                cam.Connected = false;
                Fake_Counters(out info, out cancels, out starts);
                Check("Driver: disconnect during exposure cancels the capture", cancels > before, "CancelCapture calls " + before + " -> " + cancels);
                cam.Dispose();
            }

            // --- MaxADU with processed RGB output ---
            {
                Camera cam = NewDriver(600, 400, "2", "1", false, false);
                cam.Connected = true;
                cam.StartExposure(1.0, true);
                WaitReady(cam);
                int max = 0; foreach (int v in (Array)cam.ImageArray) max = Math.Max(max, v);
                Check("Driver: RGB pixel values don't exceed MaxADU", max <= cam.MaxADU, "max pixel " + max + ", MaxADU " + cam.MaxADU);
                cam.Dispose();
            }

            // --- Subframe check uses the mode about to be used (auto-liveview then full-res subframe) ---
            {
                Camera cam = NewDriver(3000, 2000, "1", "1", true, false);
                cam.Connected = true;
                cam.StartExposure(0.0, true); // auto liveview
                WaitReady(cam);
                cam.StartX = 1500; cam.NumX = 500; cam.StartY = 0; cam.NumY = 500;
                string r = Run(() => cam.StartExposure(1.0, true));
                Check("Driver: full-res subframe after an auto-liveview frame is accepted", r == "ok", r);
                cam.Dispose();
            }

            // --- Gain ---
            {
                Camera cam = NewDriver(600, 400, "1", "1", false, true);
                cam.Connected = true;
                cam.Gain = 2;
                Check("Driver: Gain get after set", cam.Gain == 2, cam.Gain.ToString());
                cam.Dispose();

                cam = NewDriver(600, 400, "1", "1", false, true);
                Fake_Configure(600, 400, F_BAD_OPTION);
                cam.Connected = true;
                ArrayList gains = cam.Gains;
                Check("Driver: failed ISO option lookup isn't listed as ISO 0", !gains.Contains("0"), "gains: " + String.Join(",", (string[])gains.ToArray(typeof(string))));
                cam.Dispose();
            }

            // --- Multiple instances: disposing one mustn't disconnect the other ---
            {
                Camera a = NewDriver(600, 400, "1", "1", false, false);
                a.Connected = true;
                Camera b = new Camera();
                b.Dispose();
                bool stillConnected = false;
                string r = Run(() => stillConnected = a.Connected);
                Check("Driver: disposing a second instance keeps the first connected", stillConnected, r);
                r = Run(() => b.Dispose());
                Check("Driver: double Dispose is safe", r == "ok", r);
                Run(() => a.Dispose());
            }

            // --- Stability: disconnect/reconnect while other threads poll ---
            {
                Camera cam = NewDriver(600, 400, "1", "1", false, false);
                cam.Connected = true;
                Dictionary<string, int> errors = new Dictionary<string, int>();
                bool stop = false;
                ThreadStart poll = () =>
                {
                    while (!stop)
                    {
                        foreach (Action act in new Action[] { () => { var s = cam.CameraState; }, () => { var x = cam.NumX; }, () => { var x = cam.ImageReady; }, () => { var x = cam.MaxADU; }, () => { var x = cam.BayerOffsetX; } })
                        {
                            try { act(); }
                            catch (Exception e) { lock (errors) { string k = e.GetType().Name; errors[k] = errors.ContainsKey(k) ? errors[k] + 1 : 1; } }
                        }
                    }
                };
                Thread p1 = new Thread(poll), p2 = new Thread(poll);
                p1.IsBackground = p2.IsBackground = true;
                p1.Start(); p2.Start();
                Thread toggler = new Thread(() =>
                {
                    for (int i = 0; i < 300; i++)
                    {
                        try { cam.Connected = false; cam.Connected = true; }
                        catch (Exception e) { lock (errors) { string k = "toggle:" + e.GetType().Name; errors[k] = errors.ContainsKey(k) ? errors[k] + 1 : 1; } }
                    }
                });
                toggler.IsBackground = true;
                toggler.Start();
                bool finished = toggler.Join(60000);
                stop = true;
                p1.Join(5000); p2.Join(5000);
                List<string> summary = new List<string>();
                bool bad = false;
                foreach (KeyValuePair<string, int> kv in errors)
                {
                    summary.Add(kv.Key + "=" + kv.Value);
                    if (kv.Key.Contains("NullReference") || kv.Key.Contains("IndexOutOfRange") || kv.Key.Contains("SynchronizationLock") || kv.Key.Contains("ApplicationException")) bad = true;
                }
                Check("Driver: concurrent disconnect/poll gives only ASCOM errors, no crashes or hangs", finished && !bad, (finished ? "" : "TIMED OUT ") + String.Join(", ", summary.ToArray()));
                Run(() => cam.Dispose());
            }
        }
    }
}
