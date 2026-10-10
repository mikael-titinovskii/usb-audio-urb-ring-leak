# Read-only snapshot of application sessions on active Windows audio endpoints.
$ErrorActionPreference = 'Stop'
if (-not ('ScarlettDiagnostics.Sessions' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace ScarlettDiagnostics {
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class EnumeratorClass {}
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out ICollection devices);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ICollection {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDevice {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IManager {
        [PreserveSig] int GetAudioSessionControl(IntPtr session, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr session, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out ISessionEnumerator enumerator);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISessionEnumerator {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IControl control);
    }
    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IControl {
        [PreserveSig] int GetState(out int state);
    }
    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IControl2 {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint processId);
    }
    public class SessionInfo {
        public int Flow;
        public string Endpoint;
        public uint ProcessId;
        public int State;
        public string DisplayName;
        public string Error;
    }
    public static class Sessions {
        static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
        public static SessionInfo[] Read() {
            var result = new List<SessionInfo>();
            var enumerator = (IEnumerator)new EnumeratorClass();
            try {
                for (int flow = 0; flow < 2; flow++) {
                    ICollection devices; Check(enumerator.EnumAudioEndpoints(flow, 1, out devices));
                    try {
                        uint count; Check(devices.GetCount(out count));
                        for (uint i = 0; i < count; i++) {
                            IDevice device; Check(devices.Item(i, out device));
                            string id = null;
                            try {
                                Check(device.GetId(out id));
                                var iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
                                object obj; Check(device.Activate(ref iid, 23, IntPtr.Zero, out obj));
                                var manager = (IManager)obj;
                                try {
                                    ISessionEnumerator sessions; Check(manager.GetSessionEnumerator(out sessions));
                                    try {
                                        int n; Check(sessions.GetCount(out n));
                                        for (int j = 0; j < n; j++) {
                                            IControl basic; Check(sessions.GetSession(j, out basic));
                                            try {
                                                var control = (IControl2)basic;
                                                int state; uint pid; string name;
                                                Check(control.GetState(out state));
                                                Check(control.GetProcessId(out pid));
                                                Check(control.GetDisplayName(out name));
                                                result.Add(new SessionInfo { Flow=flow, Endpoint=id, ProcessId=pid, State=state, DisplayName=name });
                                            } finally { Marshal.ReleaseComObject(basic); }
                                        }
                                    } finally { Marshal.ReleaseComObject(sessions); }
                                } finally { Marshal.ReleaseComObject(manager); }
                            } catch (Exception ex) {
                                result.Add(new SessionInfo { Flow=flow, Endpoint=id, Error=ex.Message });
                            } finally { Marshal.ReleaseComObject(device); }
                        }
                    } finally { Marshal.ReleaseComObject(devices); }
                }
            } finally { Marshal.ReleaseComObject(enumerator); }
            return result.ToArray();
        }
    }
}
'@
}
foreach ($session in [ScarlettDiagnostics.Sessions]::Read()) {
    $flow = if ($session.Flow -eq 0) { 'Render' } else { 'Capture' }
    $endpointGuid = $session.Endpoint.Substring($session.Endpoint.LastIndexOf('.') + 1)
    $properties = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\$flow\$endpointGuid\Properties" -ErrorAction SilentlyContinue
    $process = Get-Process -Id $session.ProcessId -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Time = Get-Date -Format o
        Flow = $flow
        Device = $properties.'{b3f8fa53-0004-438e-9003-51a46e139bfc},6'
        EndpointName = $properties.'{a45c254e-df1c-4efd-8020-67d146a850e0},2'
        EndpointId = $session.Endpoint
        Process = $process.ProcessName
        ProcessId = $session.ProcessId
        State = @('Inactive','Active','Expired')[$session.State]
        DisplayName = $session.DisplayName
        Error = $session.Error
    }
}
