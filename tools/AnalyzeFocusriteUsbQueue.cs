using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Diagnostics.Tracing;

namespace FocusriteDiagnostics {
    public static class UsbQueueTraceAnalysis {
        static readonly Guid AudStream=new Guid("0b8e02e8-5b01-3a7d-d401-5531a6a8bdde");
        static readonly Guid AudioEngine=new Guid("ba5912e4-a67b-3b96-db60-afe06000d08c");
        static readonly Guid Pdb=new Guid("84fa3647-78d6-49c4-b63e-44df0202fe42");
        class Row {
            public double Time;
            public string Kind,Direction;
            public int Index,OtherIndex,CPU;
            public long Value;
            public uint Status,UsbStatus;
            public uint? StartFrame,CurrentFrame;
        }
        static string Distribution(IEnumerable<int> values) {
            return string.Join(", ",values.GroupBy(x=>x).OrderBy(x=>x.Key).Select(x=>x.Key+":"+x.Count()).ToArray());
        }
        public static string Analyze(string path,string csvPath) {
            var rows=new List<Row>();
            var observations=new List<string>();
            var result=new StringBuilder();
            var counts=new Dictionary<string,long>();
            bool matchingPdb=false;
            long lost;
            double traceDurationMs=0;
            using(var source=new ETWTraceEventSource(path)) {
                source.AllEvents += delegate(TraceEvent e) {
                    byte[] data=e.EventData();
                    if((int)e.Opcode==64 && data.Length>=20) {
                        byte[] bytes=new byte[16]; Array.Copy(data,bytes,16);
                        if(new Guid(bytes)==Pdb && BitConverter.ToInt32(data,16)==1) matchingPdb=true;
                    }
                    if(e.TaskGuid!=AudStream && e.TaskGuid!=AudioEngine) return;
                    int id=(int)e.ID;
                    string key=e.TaskGuid+"/0x"+id.ToString("x");
                    long count;counts.TryGetValue(key,out count);counts[key]=count+1;
                    var row=new Row {Time=e.TimeStampRelativeMSec,CPU=e.ProcessorNumber,OtherIndex=-1,Direction="OUT"};
                    if(e.TaskGuid==AudStream && (id==0x3d || id==0x55)) {
                        if(data.Length!=21) throw new InvalidDataException("Unexpected completion/submission payload length");
                        row.Kind=id==0x3d?"Complete":"Submit";
                        row.Direction=data[0]==0x49?"IN":data[0]==0x4f?"OUT":"UNKNOWN";
                        row.Index=BitConverter.ToInt32(data,1);
                        if(id==0x3d) {
                            row.Value=BitConverter.ToInt64(data,5);
                            row.Status=BitConverter.ToUInt32(data,13);
                            row.UsbStatus=BitConverter.ToUInt32(data,17);
                        } else {
                            row.StartFrame=BitConverter.ToUInt32(data,5);
                            row.CurrentFrame=BitConverter.ToUInt32(data,9);
                            row.Value=BitConverter.ToInt32(data,13);
                            row.Status=BitConverter.ToUInt32(data,17);
                            if(unchecked((int)(row.StartFrame.Value-row.CurrentFrame.Value+1u))!=row.Value)
                                throw new InvalidDataException("Submission frame-margin equation mismatch");
                        }
                        rows.Add(row);
                    } else if(e.TaskGuid==AudStream && id==0x3e) {
                        if(data.Length!=8) throw new InvalidDataException("Unexpected render-prepare payload length");
                        row.Kind="PrepareOutput";row.Index=BitConverter.ToInt32(data,0);row.Value=BitConverter.ToInt32(data,4);rows.Add(row);
                    } else if(e.TaskGuid==AudioEngine && id==0x1d) {
                        if(data.Length!=8) throw new InvalidDataException("Unexpected pair payload length");
                        row.Kind="Pair";row.Index=BitConverter.ToInt32(data,4);row.OtherIndex=BitConverter.ToInt32(data,0);rows.Add(row);
                    } else if(e.TaskGuid==AudStream && (id==0x43 || id==0x44 || id==0x45 || id==0x47 || id==0x48 || id==0x54)) {
                        observations.Add("Event 0x"+id.ToString("x")+" at_ms="+row.Time.ToString("F6",CultureInfo.InvariantCulture)+" payload="+BitConverter.ToString(data));
                    }
                };
                source.Process();lost=source.EventsLost;
                traceDurationMs=(source.SessionEndTime-source.SessionStartTime).TotalMilliseconds;
                result.AppendLine("Start="+source.SessionStartTime.ToString("o"));
                result.AppendLine("End="+source.SessionEndTime.ToString("o"));
            }
            if(!matchingPdb) throw new InvalidDataException("Expected 4.150.0.432 USB PDB identity absent");
            if(rows.Count==0) throw new InvalidDataException("No queue events");
            result.AppendLine("Assembly-derived field layout, not vendor TMF; expected PDB identity matched=True");
            result.AppendLine("Events lost="+lost+" selected rows="+rows.Count);
            double firstSelected=rows.Min(x=>x.Time),lastSelected=rows.Max(x=>x.Time);
            result.AppendLine("Selected event coverage_ms="+firstSelected.ToString("F6",CultureInfo.InvariantCulture)+".."+lastSelected.ToString("F6",CultureInfo.InvariantCulture)+" declared_session_ms="+traceDurationMs.ToString("F6",CultureInfo.InvariantCulture));
            if(traceDurationMs-lastSelected>1000) result.AppendLine("WARNING: selected events stop more than one second before session end. Check the file-size cap or stream stopping; zero EventsLost does NOT prove full interval coverage.");
            using(var writer=new StreamWriter(new FileStream(csvPath,FileMode.CreateNew,FileAccess.Write),new UTF8Encoding(false))) {
                writer.WriteLine("time_ms,kind,direction,index,input_pair_index,value,status,usb_status,cpu,start_frame,current_frame");
                foreach(Row row in rows) writer.WriteLine(string.Join(",",new string[]{row.Time.ToString("F6",CultureInfo.InvariantCulture),row.Kind,row.Direction,row.Index.ToString(),row.OtherIndex.ToString(),row.Value.ToString(),"0x"+row.Status.ToString("x8"),"0x"+row.UsbStatus.ToString("x8"),row.CPU.ToString(),row.StartFrame.HasValue?row.StartFrame.Value.ToString():"",row.CurrentFrame.HasValue?row.CurrentFrame.Value.ToString():""}));
            }
            foreach(string direction in new string[]{"IN","OUT"}) {
                var completions=rows.Where(x=>x.Kind=="Complete" && x.Direction==direction).ToList();
                var submissions=rows.Where(x=>x.Kind=="Submit" && x.Direction==direction).ToList();
                result.AppendLine(direction+" completions="+completions.Count+" index_counts="+Distribution(completions.Select(x=>x.Index)));
                result.AppendLine(direction+" submissions="+submissions.Count+" index_counts="+Distribution(submissions.Select(x=>x.Index)));
                result.AppendLine(direction+" failed_completion_statuses="+completions.Count(x=>(int)x.Status<0 || (int)x.UsbStatus<0)+" failed_submission_statuses="+submissions.Count(x=>(int)x.Status<0));
                if(completions.Count>0) result.AppendLine(direction+" logged_completion_elapsed_us="+completions.Min(x=>x.Value)+".."+completions.Max(x=>x.Value));
                var identityGaps=new List<double>();
                foreach(var slot in completions.GroupBy(x=>x.Index)) {
                    Row previous=null;
                    foreach(Row completion in slot.OrderBy(x=>x.Time)) {
                        if(previous!=null) identityGaps.Add(completion.Time-previous.Time);
                        previous=completion;
                    }
                }
                if(identityGaps.Count>0) result.AppendLine(direction+" same_index_completion_interval_ms="+identityGaps.Min().ToString("F6",CultureInfo.InvariantCulture)+".."+identityGaps.Max().ToString("F6",CultureInfo.InvariantCulture));
                if(submissions.Count>0) {
                    result.AppendLine(direction+" submission_margin_start_minus_current_plus_one="+Distribution(submissions.Select(x=>(int)x.Value)));
                    result.AppendLine(direction+" submissions_with_nonpositive_margin="+submissions.Count(x=>x.Value<=0)+" signed_margin_below_10="+submissions.Count(x=>x.Value<10));
                    if(submissions.Any(x=>x.Value<=0)) result.AppendLine("WARNING: "+direction+" StartFrame is behind CurrentFrame; successful status and absence of driver margin warnings do not establish timely transfer.");
                }
            }
            var all=rows.Where(x=>x.Kind=="Complete").ToList();
            int sameDirection=0;
            for(int i=1;i<all.Count;i++) if(all[i].Direction==all[i-1].Direction) sameDirection++;
            var pairs=rows.Where(x=>x.Kind=="Pair").ToList();
            result.AppendLine("Consecutive same-direction completion-entry events="+sameDirection);
            result.AppendLine("This metric is not proof of an overwritten pending entry: completion-entry logging precedes the pairing lock.");
            result.AppendLine("Engine pairs="+pairs.Count+" unequal_IN_OUT_indices="+pairs.Count(x=>x.Index!=x.OtherIndex)+" output_index_counts="+Distribution(pairs.Select(x=>x.Index)));
            result.AppendLine("Render-prepare events="+rows.Count(x=>x.Kind=="PrepareOutput"));
            result.AppendLine("Render-prepare previous_URB_transfer_bytes="+Distribution(rows.Where(x=>x.Kind=="PrepareOutput").Select(x=>(int)x.Value)));
            result.AppendLine("Per-second completion windows (trace-relative; first/last can be partial):");
            foreach(var window in all.GroupBy(x=>(int)(x.Time/1000.0)).OrderBy(x=>x.Key)) {
                var ins=window.Where(x=>x.Direction=="IN").ToList();
                var outs=window.Where(x=>x.Direction=="OUT").ToList();
                result.AppendLine("second="+window.Key+" IN_count="+ins.Count+" IN_indices="+string.Join(";",ins.Select(x=>x.Index).Distinct().OrderBy(x=>x).ToArray())+
                    " OUT_count="+outs.Count+" OUT_indices="+string.Join(";",outs.Select(x=>x.Index).Distinct().OrderBy(x=>x).ToArray()));
            }
            result.AppendLine("Warning/error/margin observations="+observations.Count);
            foreach(string observation in observations.Take(30)) result.AppendLine(observation);
            foreach(var count in counts.OrderBy(x=>x.Key)) result.AppendLine("Count "+count.Key+"="+count.Value);
            result.AppendLine("No PCM payload or kernel-memory dump. This interval does not establish the original fault trigger.");
            return result.ToString();
        }
    }
}
