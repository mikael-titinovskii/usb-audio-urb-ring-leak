# Control GUIDs registered by the Focusrite driver itself. They look like classic
# magic values ("deadbeef...feedface..."), but they are genuine - the driver really
# does register these control GUIDs.
#   {deadbeef-dead-beef-0005-feedfacebeef} - per-URB playback/record queue events
#       (Submit/Complete/Pair/PrepareOutput), keywords 0x100401, level 5.
#       This is the provider the queue analyzer consumes.
#   {deadbeef-dead-beef-000b-feedfacebeef} - audio-client buffer counters, keywords 0x11.
param([Guid]$ProviderGuid = 'deadbeef-dead-beef-0005-feedfacebeef')
# Read-only ETW registration query. Does not start/enable/stop a trace.
$ErrorActionPreference = 'Stop'
if (-not ('FocusriteProviderQuery' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class FocusriteProviderQuery {
    [DllImport("advapi32.dll", ExactSpelling=true)]
    private static extern uint EnumerateTraceGuidsEx(int infoClass, ref Guid input,
        uint inputSize, IntPtr output, uint outputSize, out uint returnedSize);
    public static string Query(string id) {
        Guid guid = Guid.Parse(id);
        uint required;
        uint status=EnumerateTraceGuidsEx(1,ref guid,16,IntPtr.Zero,0,out required);
        if(status!=122 && status!=0) return "GUID="+guid+" query status="+status;
        if(required<8 || required>1048576) return "Invalid output size "+required;
        IntPtr buffer=Marshal.AllocHGlobal((int)required);
        try {
            uint actual;
            status=EnumerateTraceGuidsEx(1,ref guid,16,buffer,required,out actual);
            if(status!=0) return "GUID="+guid+" query status="+status;
            int count=Marshal.ReadInt32(buffer);
            StringBuilder text=new StringBuilder("GUID="+guid+" provider instance entries="+count);
            int offset=8;
            for(int i=0;i<count;i++) {
                if(offset<8 || offset>actual-16) throw new InvalidOperationException("Invalid instance offset");
                IntPtr instance=IntPtr.Add(buffer,offset);
                int next=Marshal.ReadInt32(instance,0);
                int enables=Marshal.ReadInt32(instance,4);
                int pid=Marshal.ReadInt32(instance,8);
                int flags=Marshal.ReadInt32(instance,12);
                text.AppendFormat("\ninstance={0} PID={1} flags=0x{2:x} enabled sessions={3}",i,pid,flags,enables);
                if(next==0) break;
                offset=checked(offset+next);
            }
            return text.ToString();
        } finally { Marshal.FreeHGlobal(buffer); }
    }
}
'@
}
[FocusriteProviderQuery]::Query($ProviderGuid.ToString())
