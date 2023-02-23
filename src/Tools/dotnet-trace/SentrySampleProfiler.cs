using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.Trace {
    /// <summary>
    ///  Attaches to EventPipeEventSource and collects the profile.
    /// </summary>
    internal class SentrySampleProfiler {
        private readonly EventPipeEventSource _source;
        // Native (not managed) thread ID to skip when collecting samples. 
        private readonly int _skipThreadId; 
        private readonly SampleProfilerTraceEventParser _sampleProfilerTraceEventParser;
        private readonly SentrySampleProfile _profile = new();

        public SentrySampleProfiler(EventPipeEventSource source) {
            _source = source;
            _skipThreadId = GetCurrentThreadId();

            _sampleProfilerTraceEventParser = new(source);
            _sampleProfilerTraceEventParser.ThreadSample += OnThreadSample;

            // TODO See TraceLog & handle stack walking
            // Also see https://github.com/microsoft/perfview/blob/main/documentation/TraceEvent/TraceEventProgrammersGuide.md#using-call-stacks-with-the-traceevent-library
            //_sampleProfilerTraceEventParser.ThreadStackWalk += OnStackWalk;
            //kernel.StackWalkStack += ...
            //...

            // TODO other handlers that were attached in SampleProfilerThreadTimeComputer()
            //source.Clr.GCAllocationTick += OnSampledProfile;
            //source.Clr.GCSampledObjectAllocation += OnSampledProfile;

            //TplEtwProviderTraceEventParser tplEtwProviderTraceEventParser = new TplEtwProviderTraceEventParser(source);
            //tplEtwProviderTraceEventParser.AwaitTaskContinuationScheduledSend += OnSampledProfile;
            //tplEtwProviderTraceEventParser.TaskScheduledSend += OnSampledProfile;
            //tplEtwProviderTraceEventParser.TaskExecuteStart += OnSampledProfile;
            //tplEtwProviderTraceEventParser.TaskWaitSend += OnSampledProfile;
        }

        // TODO other platforms
        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        private void OnThreadSample(TraceEvent traceData) {
            var threadId = traceData.ThreadID;
            if (threadId > 0 && _skipThreadId != threadId) {
                _profile.samples.Add(new() {
                    Timestamp = (ulong)(traceData.TimeStampRelativeMSec * 1_000_000),
                    StackId = 0,
                    ThreadId = threadId
                });
            }
            }
        }

    internal class SentrySampleProfile {
        public GrowableArray<Sample> samples;


        public class Sample {
            /// <summary>
            /// Timestamp in nanoseconds relative to the profile start.
            /// </summary>
            public ulong Timestamp;

            public int ThreadId;
            public int StackId;
        }
    }
}
