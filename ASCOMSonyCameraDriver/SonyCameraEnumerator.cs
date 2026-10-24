using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections;

namespace ASCOM.SonyMirrorless
{
    public class SonyCameraEnumerator : SonyCommon
    {
        public ArrayList Cameras
        {
            get
            {
                ArrayList result = new ArrayList();
                UInt32 count = GetPortableDeviceCount();
                UInt32 hr;

                for (UInt32 iter = 0; iter < count; iter++)
                {
                    // Must be fresh each time - reusing it would marshal the previous device's strings in,
                    // and the DLL overwrites those pointers without freeing them
                    PortableDeviceInfo portableDeviceInfo = new PortableDeviceInfo();

                    hr = GetPortableDeviceInfo(iter, ref portableDeviceInfo);

                    if (hr == ERROR_SUCCESS)
                    {
                        // Try to open the device
                        UInt32 handle = OpenDevice(portableDeviceInfo.id);

                        if (handle != INVALID_HANDLE_VALUE)
                        {
                            DeviceInfo info = new DeviceInfo()
                            {
                                Version = 1
                            };

                            hr = GetDeviceInfo(handle, ref info);

                            if (hr == ERROR_SUCCESS)
                            {
                                result.Add(new SonyCamera(info));
                            }

                            CloseDevice(handle);
                        }
                    }
                }

                return result;
            }
        }
    }
}
