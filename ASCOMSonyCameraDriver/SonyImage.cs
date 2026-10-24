using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using ASCOM.Utilities;

namespace ASCOM.SonyMirrorless
{
    public class SonyImage : SonyCommon
    {
        public enum ImageStatus
        {
            Created, Capturing, Reading, Ready, Failed, Cancelled
        }

        internal ImageInfo m_info;
        internal UInt32 m_cameraHandle;
        internal TraceLogger m_logger;
        public DateTime StartTime;
        private int m_personality;
        private short m_readoutMode;

        internal ImageStatus m_status = ImageStatus.Created;

        public int Width = 0;
        public int Height = 0;
        public static int[,,] RGB;
        public static int[,] BAYER;

        /// <param name="startTime">UTC time the exposure was started</param>
        public SonyImage(UInt32 handle, ImageInfo info, int personality, short readoutMode, TraceLogger logger, DateTime startTime)
        {
            m_cameraHandle = handle;
            m_logger = logger;
            m_info = info;
            m_personality = personality;
            m_readoutMode = readoutMode;
            StartTime = startTime;
            Status = SonyImage.ImageStatus.Capturing;

            if (m_info.Status == STATUS_COMPLETE)
            {
                ProcessImageData();
                Status = SonyImage.ImageStatus.Ready;
            }
        }

        public ImageStatus Status
        {
            get
            {
                switch (m_status)
                {
                    case ImageStatus.Created:
                    case ImageStatus.Ready:
                    case ImageStatus.Failed:
                    case ImageStatus.Cancelled:
                        Log(String.Format("get_Status - returning {0}", m_status.ToString()));
                        break;

                    case ImageStatus.Capturing:
                        // Figure out if we've finished
                        Log("get_Status - capturing, refreshing");
                        GetCaptureStatus(m_cameraHandle, ref m_info);

                        switch (m_info.Status)
                        {
                            case STATUS_CANCELLED:
                                m_status = ImageStatus.Cancelled;
                                Log("get_Status - latest = Cancelled");
                                break;

                            case STATUS_FAILED:
                                m_status = ImageStatus.Failed;
                                Log("get_Status - latest = Failed");
                                break;

                            case STATUS_STARTING:   // Getting ready to press button
                            case STATUS_EXPOSING:   // Button is down
                            case STATUS_READING:    // Reading from camera, not driver
                            case STATUS_PROCESSING: // Camera DLL is converting the image
                                m_status = ImageStatus.Capturing;
                                Log("get_Status - latest = Capturing");
                                break;

                            case STATUS_COMPLETE:
                                m_status = ImageStatus.Reading;
                                Log("get_Status - latest = Reading");

                                try
                                {
                                    ProcessImageData();
                                    m_status = ImageStatus.Ready;
                                    Log("get_Status - latest = Ready");
                                }
                                catch (Exception e)
                                {
                                    // Without this the image would be stuck in "Reading" forever
                                    m_status = ImageStatus.Failed;
                                    Log(String.Format("get_Status - unable to process image data, marking as Failed: {0}", e.Message));
                                }
                                break;

                            default:
                                Log(String.Format("get_Status - latest = unknown {0}", m_info.Status.ToString()));
                                break;
                        }
                        break;
                }

                return m_status;
            }

            set
            {
                m_status = value;
            }
        }

        public double Duration
        {
            get
            {
                return m_info.ExposureTime;
            }
        }

        /// <summary>
        /// Personality the image was converted for - decides whether RGB data went into BAYER (mono) or RGB
        /// </summary>
        public int Personality
        {
            get
            {
                return m_personality;
            }
        }

        public void ProcessImageData()
        {
            Log("Processing Image Data - in");

            try
            {
                Log(String.Format("m_info.ImageSize = {0}", m_info.ImageSize.ToString()));
                Log(String.Format("m_info.Width     = {0}", m_info.Width.ToString()));
                Log(String.Format("m_info.Height    = {0}", m_info.Height.ToString()));
                Log(String.Format("m_info.ImageMode = {0}", m_info.ImageMode.ToString()));

                Width = (int)m_info.Width;
                Height = (int)m_info.Height;

                int bytesPerPixel;

                switch (m_info.ImageMode)
                {
                    case IMAGEMODE_RAW:
                        bytesPerPixel = 2;
                        break;

                    case IMAGEMODE_RGB:
                        bytesPerPixel = 6;
                        break;

                    default:
                        throw new ASCOM.DriverException(String.Format("Unsupported image mode {0}", m_info.ImageMode));
                }

                // Make sure the DLL actually handed over enough data for the dimensions it reported,
                // otherwise we'd index past the end of the buffer
                long expectedSize = (long)Width * Height * bytesPerPixel;

                if (m_info.ImageData == IntPtr.Zero || Width <= 0 || Height <= 0 || m_info.ImageSize < expectedSize)
                {
                    throw new ASCOM.DriverException(String.Format("Image data is incomplete: got {0} bytes, expected {1} for {2} x {3}", m_info.ImageSize, expectedSize, Width, Height));
                }

                // Work through the image a band of rows at a time rather than copying the whole image - a
                // full-size copy of a 60MP RGB image is 360MB, which 32-bit hosts frequently can't allocate.
                // The output arrays are [x, y] so consecutive y values are adjacent in memory; walking each
                // band column by column keeps the writes close together instead of striding across the array.
                int rowBytes = Width * bytesPerPixel;
                int bandRows = Math.Max(1, Math.Min(Height, 32));
                byte[] band = new byte[bandRows * rowBytes];

                switch (m_info.ImageMode)
                {
                    case IMAGEMODE_RAW:
                        // Reuse previous array
                        if (BAYER == null || BAYER.GetLength(0) != Width || BAYER.GetLength(1) != Height)
                        {
                            BAYER = new int[Width, Height];
                        }

                        int mi = int.MaxValue, ma = int.MinValue;

                        for (int y0 = 0; y0 < Height; y0 += bandRows)
                        {
                            int rows = Math.Min(bandRows, Height - y0);

                            Marshal.Copy(IntPtr.Add(m_info.ImageData, y0 * rowBytes), band, 0, rows * rowBytes);

                            for (int x = 0; x < Width; x++)
                            {
                                for (int r = 0, i = x * 2; r < rows; r++, i += rowBytes)
                                {
                                    int v = band[i] + (band[i + 1] << 8);

                                    if (v < mi) { mi = v; }
                                    if (v > ma) { ma = v; }
                                    BAYER[x, Height - (y0 + r) - 1] = v;
                                }
                            }
                        }

                        Log(String.Format("min pixel = {0}, max pixel = {1}", mi, ma));
                        break;

                    case IMAGEMODE_RGB:
                        if (m_personality == PERSONALITY_NINA)
                        {
                            // NINA doesn't support multi-channel images, so convert the RGB To Mono
                            // Use BAYER obj since it is only x/y - avoids having another memory object
                            // Reuse previous array
                            if (BAYER == null || BAYER.GetLength(0) != Width || BAYER.GetLength(1) != Height)
                            {
                                BAYER = new int[Width, Height];
                            }

                            for (int y0 = 0; y0 < Height; y0 += bandRows)
                            {
                                int rows = Math.Min(bandRows, Height - y0);

                                Marshal.Copy(IntPtr.Add(m_info.ImageData, y0 * rowBytes), band, 0, rows * rowBytes);

                                for (int x = 0; x < Width; x++)
                                {
                                    for (int r = 0, i = x * 6; r < rows; r++, i += rowBytes)
                                    {
                                        int red = band[i] + (band[i + 1] << 8);
                                        int green = band[i + 2] + (band[i + 3] << 8);
                                        int blue = band[i + 4] + (band[i + 5] << 8);

                                        // Previously cast to short, which wrapped bright pixels (> 32767) to negative values
                                        BAYER[x, y0 + r] = Math.Min(65535, (int)((0.2125 * red) + (0.7154 * green) + (0.0721 * blue)));
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (RGB == null || RGB.GetLength(0) != Width || RGB.GetLength(1) != Height)
                            {
                                RGB = new int[Width, Height, 3];
                            }

                            int rIndex = m_readoutMode == OUTPUTFORMAT_BGR ? 2 : 0;
                            int bIndex = 2 - rIndex;
                            bool invert = m_personality == PERSONALITY_SHARPCAP;

                            for (int y0 = 0; y0 < Height; y0 += bandRows)
                            {
                                int rows = Math.Min(bandRows, Height - y0);

                                Marshal.Copy(IntPtr.Add(m_info.ImageData, y0 * rowBytes), band, 0, rows * rowBytes);

                                for (int x = 0; x < Width; x++)
                                {
                                    for (int r = 0, i = x * 6; r < rows; r++, i += rowBytes)
                                    {
                                        int destY = invert ? y0 + r : Height - (y0 + r) - 1;

                                        RGB[x, destY, rIndex] = band[i] + (band[i + 1] << 8);
                                        RGB[x, destY, 1] = band[i + 2] + (band[i + 3] << 8);
                                        RGB[x, destY, bIndex] = band[i + 4] + (band[i + 5] << 8);
                                    }
                                }
                            }
                        }
                        break;
                }
            }
            finally
            {
                Cleanup();
            }

            Log("Processing Image Data - out");
        }

        public void Cleanup()
        {
            if (m_info.ImageData != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(m_info.ImageData);
                m_info.ImageData = IntPtr.Zero;
            }
        }

        private void Log(String message)
        {
            if (m_logger != null)
            {
                m_logger.LogMessage("SonyImage", message);
            }
        }
    }
}
