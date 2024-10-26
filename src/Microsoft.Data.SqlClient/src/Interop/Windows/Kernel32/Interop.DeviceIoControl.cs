// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;

internal partial class Interop
{
    internal partial class Kernel32
    {
        internal const ushort FILE_DEVICE_FILE_SYSTEM = 0x0009;

        [DllImport(Libraries.Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool DeviceIoControl
        (
            SafeFileHandle fileHandle,
            uint ioControlCode,
            IntPtr inBuffer,
            uint cbInBuffer,
            IntPtr outBuffer,
            uint cbOutBuffer,
            out uint cbBytesReturned,
            IntPtr overlapped
        );
    }
}
