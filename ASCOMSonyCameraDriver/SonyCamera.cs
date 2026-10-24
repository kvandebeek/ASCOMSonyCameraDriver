using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using ASCOM.DeviceInterface;
using ASCOM.Utilities;
using System.IO;

namespace ASCOM.SonyMirrorless
{
    public class SonyCamera : SonyCommon
    {
        private DeviceInfo m_info;
        private UInt32 m_handle = INVALID_HANDLE_VALUE;
        internal SonyImage m_lastImage = null;
        internal CaptureMode m_mode;
        internal ImageMode m_outputMode = ImageMode.RGB;
        internal CameraInfo m_resolutions;
        internal Dictionary<UInt32, CameraProperty> m_properties = new Dictionary<UInt32, CameraProperty>();
        internal TraceLogger m_logger = null;
        internal Boolean m_bulbMode = false;
        internal short m_bulbModeTime = 1;
        internal short m_desiredGain = -1;
        private ArrayList m_gains = new ArrayList();

        public enum ImageMode
        {
            RGB = (int)IMAGEMODE_RGB,
            RGGB = (int)IMAGEMODE_RAW,
        }

        public class CaptureMode
        {
            public Boolean Preview = false;
            public UInt32 ImageWidthPixels = 1;
            public UInt32 ImageHeightPixels = 1;
        }

        public SonyCamera(DeviceInfo info)
        {
            m_info = info;
            m_mode = new CaptureMode();
            m_resolutions = new CameraInfo();

            m_mode.ImageWidthPixels = m_info.CropMode == 0 ? m_info.ImageWidthPixels : m_info.ImageWidthCroppedPixels;
            m_mode.ImageHeightPixels = m_info.CropMode == 0 ? m_info.ImageHeightPixels : m_info.ImageHeightCroppedPixels;
        }

        public TraceLogger Logger
        {
            set
            {
                m_logger = value;
            }
        }

        public bool Connected
        {
            get
            {
                return m_handle != INVALID_HANDLE_VALUE;
            }

            set
            {
                if (value)
                {
                    if (m_handle == INVALID_HANDLE_VALUE)
                    {
                        UInt32 handle = OpenDevice(m_info.DeviceName);

                        if (handle == INVALID_HANDLE_VALUE)
                        {
                            throw new ASCOM.NotConnectedException(String.Format("Unable to open camera '{0}'", DisplayName));
                        }

                        UInt32 hr = GetCameraInfo(handle, ref m_resolutions, 0);

                        if (hr != ERROR_SUCCESS)
                        {
                            CloseDevice(handle);

                            throw new ASCOM.NotConnectedException(String.Format("Unable to read camera info from '{0}' (error {1})", DisplayName, hr));
                        }

                        m_handle = handle;
                    }
                }
                else
                {
                    if (m_handle != INVALID_HANDLE_VALUE)
                    {
                        if (m_lastImage != null)
                        {
                            m_lastImage.Cleanup();
                            m_lastImage = null;
                        }

                        CloseDevice(m_handle);
                    }

                    m_handle = INVALID_HANDLE_VALUE;
                }
            }
        }

        public Boolean BulbMode
        {
            get
            {
                return m_bulbMode;
            }

            set
            {
                m_bulbMode = value;
            }
        }

        public short BulbModeTime
        {
            get
            {
                return m_bulbModeTime;
            }

            set
            {
                m_bulbModeTime = value;
            }
        }

        public DeviceInfo Info
        {
            get
            {
                return m_info;
            }
        }

        public CaptureMode Mode
        {
            get
            {
                return m_mode;
            }
        }

        public CameraStates State
        {
            get
            {
                if (m_lastImage != null)
                {
                    switch (m_lastImage.Status)
                    {
                        case SonyImage.ImageStatus.Capturing:
                            return CameraStates.cameraExposing;

                        case SonyImage.ImageStatus.Reading:
                            return CameraStates.cameraReading;

                        case SonyImage.ImageStatus.Failed:
                            // Lets the client know the exposure isn't going to complete
                            return CameraStates.cameraError;

                        default:
                            return CameraStates.cameraIdle;
                    }
                }
                else
                {
                    return CameraStates.cameraIdle;
                }
            }
        }

        public SonyImage StartCapture(double duration, int personality, short readoutMode)
        {
            ImageInfo info = new ImageInfo();

            if (m_lastImage != null)
            {
                m_lastImage.Cleanup();
                m_lastImage = null;
            }

            // 32-bit apps tend to run close to the line of memory
            // The auto garbage collection tends to fail when we're talking the large files
            // a 60MP camera can deliver, and an attempt to allocate more than is available doesn't
            // seem to trigger a collection - so we'll just do it here.
            // TODO: Make this conditional on 32-bit execution
            if (!Environment.Is64BitProcess)
            {
                GC.Collect();
            }

            info.Status = STATUS_EXPOSING;
            info.ImageMode = (uint)m_outputMode;
            info.ExposureTime = duration;

            if (Mode.Preview)
            {
                info.ImageMode = IMAGEMODE_RGB;
                GetPreviewImage(m_handle, ref info);

                // The DLL leaves Status untouched if it throws internally, so anything other than
                // Complete means we didn't get an image - and polling for one would never finish
                if (info.Status != STATUS_COMPLETE)
                {
                    if (info.ImageData != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(info.ImageData);
                    }

                    throw new ASCOM.DriverException(String.Format("Unable to get liveview image (status {0})", info.Status));
                }

                m_lastImage = new SonyImage(m_handle, info, personality, readoutMode, m_logger);
            }
            else
            {
                UInt32 hr;

                if (m_bulbMode)
                {
                    PropertyValue pv = new PropertyValue();
                    hr = SetExposureTime(m_handle, (float)(duration > m_bulbModeTime ? 0 : duration), ref pv);

                    if (hr != ERROR_SUCCESS)
                    {
                        Log(String.Format("SetExposureTime({0}) failed with error {1}", duration, hr));
                    }
                }

                if (m_desiredGain != -1 && Gains.Count > 0)
                {
                    hr = SetPropertyValue(m_handle, SonyCommon.PROPERTY_ISO, UInt32.Parse((string)Gains[m_desiredGain]));

                    if (hr != ERROR_SUCCESS)
                    {
                        Log(String.Format("Setting ISO to {0} failed with error {1}", Gains[m_desiredGain], hr));
                    }
                }

                hr = StartCapture(m_handle, ref info);

                if (hr != ERROR_SUCCESS)
                {
                    throw new ASCOM.DriverException(String.Format("Unable to start exposure (error {0})", hr));
                }

                m_lastImage = new SonyImage(m_handle, info, personality, readoutMode, m_logger);
            }

            return m_lastImage;
        }

        public void StopCapture()
        {
            if (m_lastImage != null && m_lastImage.Status == SonyImage.ImageStatus.Capturing)
            {
                CancelCapture(m_handle, ref m_lastImage.m_info);
            }
        }

        public CameraInfo Resolutions
        {
            get
            {
                GetCameraInfo(m_handle, ref m_resolutions, INFOFLAG_ACTIVE);

                return m_resolutions;
            }
        }

        public SonyImage LastImage
        {
            get
            {
                return m_lastImage;
            }
        }

        public Boolean ImageReady
        {
            get
            {
                return m_lastImage != null && m_lastImage.Status == SonyImage.ImageStatus.Ready;
            }
        }

        public String Model
        {
            get
            {
                return m_info.Model;
            }
        }

        public String SerialNumber
        {
            get
            {
                return m_info.SerialNumber.TrimStart(new char[] { '0' });
            }
        }

        public String DisplayName
        {
            get
            {
                return String.Format("{0} (s/n: {1})", Model, SerialNumber);
            }
        }

        public Boolean HasLiveView
        {
            get
            {
                return (m_resolutions.CameraFlags & CAMERA_SUPPORTS_LIVEVIEW) != 0;
            }
        }

        public Boolean PreviewMode
        {
            get
            {
                return Mode.Preview;
            }

            set
            {
                if (value != Mode.Preview)
                {
                    Mode.Preview = value && ((m_resolutions.CameraFlags & CAMERA_SUPPORTS_LIVEVIEW) != 0);

                    if (!Mode.Preview)
                    {
                        Mode.ImageWidthPixels = m_info.CropMode == 0 ? m_resolutions.ImageWidthPixels : m_resolutions.ImageWidthCroppedPixels;
                        Mode.ImageHeightPixels = m_info.CropMode == 0 ? m_resolutions.ImageHeightPixels : m_resolutions.ImageHeightCroppedPixels;
                    }
                    else
                    {
                        Mode.ImageWidthPixels = m_resolutions.PreviewWidthPixels;
                        Mode.ImageHeightPixels = m_resolutions.PreviewHeightPixels;
                    }

                    // This allows the driver to return assumed image size not the size of the last image
                    // which will probably now be different
                    if (m_lastImage != null)
                    {
                        m_lastImage.Cleanup();
                        m_lastImage = null;
                    }
                }
            }
        }

        public ImageMode OutputMode
        {
            get
            {
                return m_outputMode;
            }

            set
            {
                m_outputMode = value;
            }
        }

        public ArrayList Gains
        {
            get
            {
                if (m_gains.Count == 0)
                {
                    CameraProperty iso = GetProperty(SonyCommon.PROPERTY_ISO_OPTIONS);

                    if (iso == null)
                    {
                        // Camera doesn't report a list of ISO values - leave the list empty
                        return m_gains;
                    }

                    SonyCommon.PropertyValueOption[] options = iso.Options;

                    foreach (SonyCommon.PropertyValueOption option in options)
                    {
                        // Unsure what the top-byte-set values are for, so filter them out
                        // 0x00ffffff = auto
                        if ((option.Value & 0xff000000) == 0)
                        {
                            m_gains.Add(option.Value.ToString());
                        }
                    }

                }

                return m_gains;
            }
        }

        public short GainIndex
        {
            get
            {
                // The requested gain is only sent to the camera when the next exposure starts, so report
                // that rather than the camera's current value - otherwise a get straight after a set
                // returns the old value
                if (m_desiredGain != -1)
                {
                    Log(String.Format("get GainIndex: returning pending index {0}", m_desiredGain));

                    return m_desiredGain;
                }

                RefreshProperties();

                CameraProperty iso = GetProperty(SonyCommon.PROPERTY_ISO);

                if (iso == null)
                {
                    throw new ASCOM.DriverException("Camera does not report its ISO setting");
                }

                UInt32 value = iso.CurrentValue(m_handle).Value;
                short index = (short)Gains.IndexOf(value.ToString());

                Log(String.Format("get GainIndex: camera reports '{0}', which maps to index {1}", value, index));

                return index;
            }

            set
            {
                if (value >= 0 && value < Gains.Count)
                {
                    m_desiredGain = value;
                }
                else
                {
                    Log(String.Format("Attempting to find gain {0} match", value));
                    int desiredIndex = Gains.IndexOf(value.ToString());

                    if (desiredIndex < 0)
                    {
                        throw new ASCOM.InvalidValueException("Gain", value.ToString(), String.Format("0 to {0}", Gains.Count - 1));
                    }

                    Log(String.Format("Setting gain to index {0} which matches {1}", desiredIndex, value));
                    m_desiredGain = (short)desiredIndex;
                }
            }
        }

        private void PopulatePropertyInfo()
        {
            if (m_properties.Count == 0)
            {
                // Need to actually fetch properties
                // Start with getting a list of all the properties
                UInt32 count = 0;

                // Get # of properties
                UInt32 hr = GetPropertyList(m_handle, IntPtr.Zero, ref count);

                if (count == 0)
                {
                    Log(String.Format("GetPropertyList returned no properties (error {0})", hr));
                    return;
                }

                // The DLL fills the list without checking its size, so leave headroom in case the
                // camera advertises more properties between the two calls
                UInt32 allocated = count + 32;
                int[] ids = new int[allocated];
                IntPtr pIds = Marshal.AllocCoTaskMem((int)allocated * sizeof(UInt32));

                try
                {
                    hr = GetPropertyList(m_handle, pIds, ref count);

                    if (hr != ERROR_SUCCESS)
                    {
                        Log(String.Format("GetPropertyList failed with error {0}", hr));
                        return;
                    }

                    Marshal.Copy(pIds, ids, 0, (int)count);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pIds);
                }

                // Now I want the property descriptors
                for (int i = 0; i < count; i++)
                {
                    PropertyDescriptor descriptor = new PropertyDescriptor();

                    hr = GetPropertyDescriptor(m_handle, (uint)ids[i], ref descriptor);

                    PropertyValueOption[] options = new PropertyValueOption[descriptor.ValueCount];

                    for (uint index = 0; index < descriptor.ValueCount; index++)
                    {
                        GetPropertyValueOption(m_handle, descriptor.Id, ref options[index], index);
//                        UInt32 countReturned = descriptor.ValueCount;
//                        GetPropertyValueOptions(m_handle, descriptor.Id, ref options, ref countReturned);
                    }

                    m_properties[descriptor.Id] = new CameraProperty(descriptor, options);
                }
            }
        }

        public Dictionary<UInt32, CameraProperty> Properties
        {
            get
            {
                PopulatePropertyInfo();

                return m_properties;
            }
        }

        public CameraProperty GetProperty(UInt32 id)
        {
            PopulatePropertyInfo();
            return m_properties.ContainsKey(id) ? m_properties[id] : null;
        }

        public PropertyValue GetPropertyValue(UInt32 id)
        {
            PopulatePropertyInfo();
            return m_properties.ContainsKey(id) ? m_properties[id].CurrentValue(m_handle) : new PropertyValue();
        }

        public void RefreshProperties()
        {
            RefreshPropertyList(m_handle);
        }

        private void Log(String message)
        {
            if (m_logger != null)
            {
                m_logger.LogMessage("SonyCamera", message);
            }
        }
    }
}
